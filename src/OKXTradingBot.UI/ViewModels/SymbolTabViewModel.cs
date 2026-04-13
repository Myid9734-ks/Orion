using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;
using OKXTradingBot.Core.Trading;
using OKXTradingBot.Infrastructure.Notifications;
using OKXTradingBot.Infrastructure.OKX;
using OKXTradingBot.UI.Services;
using OKXTradingBot.UI.Views;
using ReactiveUI;

namespace OKXTradingBot.UI.ViewModels;

/// <summary>봇 시작 시 부모 VM에서 전달받는 전역 설정 (API 키 등)</summary>
public class GlobalBotConfig
{
    public string ApiKey                 { get; init; } = "";
    public string ApiSecret              { get; init; } = "";
    public string Passphrase             { get; init; } = "";
    public string GptApiKey              { get; init; } = "";
    public string GptModel               { get; init; } = "";
    public int    GptCandleCount         { get; init; } = 30;
    public int    GptConfidenceThreshold { get; init; } = 60;
    public string TelegramBotToken       { get; init; } = "";
    public string TelegramChatId         { get; init; } = "";
    public bool   IsBacktestMode         { get; init; }
}

/// <summary>심볼 탭 하나의 상태 + 설정 + TradingCore 관리</summary>
public class SymbolTabViewModel : ReactiveObject
{
    // ── 공유 참조 ──────────────────────────────────────────────────────
    private readonly ObservableCollection<string> _symbolOptions;
    private readonly Action _onChanged;                     // 설정 변경 → 부모 dirty 알림
    private readonly Func<GlobalBotConfig> _getGlobalConfig;
    private readonly Func<string, bool> _isSymbolInUse;    // 중복 심볼 검사

    // ── 트레이딩 내부 ──────────────────────────────────────────────────
    private TradingCore?        _core;
    private IDataProvider?      _dataProvider;
    private CancellationTokenSource? _cts;
    private IDisposable?        _priceTimer;
    private IDisposable?        _chartTimer;
    private OkxWebSocketClient? _chartWs;
    private CancellationTokenSource? _chartWsCts;
    private List<Candle>        _candleBuffer = new();
    private bool                _isLoading    = false;
    private int                 _wsTickCount  = 0;
    private int                 _tradeSeq     = 0;

    // ── 설정 ──────────────────────────────────────────────────────────
    private string        _symbol            = "BTC-USDT-SWAP";
    private decimal       _totalBudget       = 100m;
    private int           _leverage          = 10;
    private string        _marginMode        = "Cross";
    private int           _martinCount       = 9;
    private decimal       _martinGap         = 0.5m;
    private decimal       _targetProfit      = 0.5m;
    private List<decimal> _martinGapSteps    = new();
    private List<decimal> _targetProfitSteps = new();
    private bool          _stopLossEnabled   = false;
    private decimal       _stopLossPercent   = 3.0m;
    private bool          _autoRepeat        = true;

    // ── 차트 ──────────────────────────────────────────────────────────
    private string    _selectedBar       = "1m";
    private ChartType _selectedChartType = ChartType.Line;

    // ── 런타임 상태 ───────────────────────────────────────────────────
    private bool                    _isRunning;
    private decimal                 _currentPrice;
    private IReadOnlyList<Candle>   _recentCandles   = Array.Empty<Candle>();
    private string                  _positionStatusText = "대기 중";
    private string                  _directionText   = "-";
    private int                     _martinStep;
    private decimal                 _totalAmount;
    private decimal                 _avgEntryPrice;
    private decimal                 _nextMartinPrice;
    private decimal                 _unrealizedPnlPct;
    private decimal                 _realizedPnl;
    private IBrush                  _directionBrush  = Brushes.Gray;
    private IBrush                  _pnlBrush        = Brushes.Gray;

    // ── 통계 ──────────────────────────────────────────────────────────
    private decimal _totalPnl;
    private int     _winCount;
    private int     _lossCount;

    // ── Unsaved ───────────────────────────────────────────────────────
    private SymbolTabSettings _savedSnapshot = new();
    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => this.RaiseAndSetIfChanged(ref _hasUnsavedChanges, value);
    }

    // ── Commands ──────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> StartCommand          { get; }
    public ReactiveCommand<Unit, Unit> StopCommand           { get; }
    public ReactiveCommand<Unit, Unit> ClosePositionCommand  { get; }

    // ── Observable collections ────────────────────────────────────────
    public ObservableCollection<string>      Logs         { get; } = new();
    public ObservableCollection<TradeRecord> TradeHistory { get; } = new();
    public ObservableCollection<string>      SymbolOptions => _symbolOptions;

    // ═══════════════════════════════════════════════════════════════════
    // 생성자
    // ═══════════════════════════════════════════════════════════════════

    public SymbolTabViewModel(
        ObservableCollection<string> symbolOptions,
        Action onChanged,
        Func<GlobalBotConfig> getGlobalConfig,
        Func<string, bool> isSymbolInUse,
        SymbolTabSettings? initialSettings = null)
    {
        _symbolOptions   = symbolOptions;
        _onChanged       = onChanged;
        _getGlobalConfig = getGlobalConfig;
        _isSymbolInUse   = isSymbolInUse;

        if (initialSettings != null)
            ApplySettings(initialSettings);

        _savedSnapshot = ToSettings();

        var canStart = this.WhenAnyValue(x => x.IsRunning).Select(r => !r);
        var canStop  = this.WhenAnyValue(x => x.IsRunning);

        StartCommand         = ReactiveCommand.CreateFromTask(StartBotAsync,     canStart);
        StopCommand          = ReactiveCommand.CreateFromTask(StopBotAsync,      canStop);
        ClosePositionCommand = ReactiveCommand.CreateFromTask(ClosePositionAsync, canStop);

        StartCommand.ThrownExceptions.Subscribe(ex        => AddLog($"[오류] {ex.Message}"));
        StopCommand.ThrownExceptions.Subscribe(ex         => AddLog($"[오류] {ex.Message}"));
        ClosePositionCommand.ThrownExceptions.Subscribe(ex => AddLog($"[오류] {ex.Message}"));

        // 초기 차트 로드
        _ = RestartChartWebSocketAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 탭 표시
    // ═══════════════════════════════════════════════════════════════════

    public string TabHeader
    {
        get
        {
            var name = _symbol
                .Replace("-USDT-SWAP", "")
                .Replace("-USDT-PERP", "")
                .Replace("-USDC-SWAP", "");
            return _isRunning ? $"{name} ●" : name;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 설정 프로퍼티
    // ═══════════════════════════════════════════════════════════════════

    // 중복 심볼 경고 텍스트
    private string _symbolDuplicateWarning = "";
    public string SymbolDuplicateWarning
    {
        get => _symbolDuplicateWarning;
        private set => this.RaiseAndSetIfChanged(ref _symbolDuplicateWarning, value);
    }
    public bool HasSymbolDuplicateWarning => !string.IsNullOrEmpty(_symbolDuplicateWarning);

    public string Symbol
    {
        get => _symbol;
        set
        {
            if (_symbol == value) return;
            this.RaiseAndSetIfChanged(ref _symbol, value);
            this.RaisePropertyChanged(nameof(TabHeader));
            this.RaisePropertyChanged(nameof(SingleOrderAmountText));
            this.RaisePropertyChanged(nameof(SingleOrderAmountValueText));

            // 중복 심볼 경고 (로딩 중에는 체크 생략)
            if (!_isLoading && _isSymbolInUse(value))
            {
                SymbolDuplicateWarning = $"⚠ {value.Replace("-USDT-SWAP","").Replace("-USDT-PERP","").Replace("-USDC-SWAP","")} 은 다른 탭에서 이미 사용 중입니다";
                this.RaisePropertyChanged(nameof(HasSymbolDuplicateWarning));
            }
            else
            {
                SymbolDuplicateWarning = "";
                this.RaisePropertyChanged(nameof(HasSymbolDuplicateWarning));
            }

            // 차트 심볼 변경
            _ = RestartChartWebSocketAsync();
            MarkUnsaved();
        }
    }

    public decimal TotalBudget
    {
        get => _totalBudget;
        set
        {
            this.RaiseAndSetIfChanged(ref _totalBudget, value);
            this.RaisePropertyChanged(nameof(SingleOrderAmountText));
            this.RaisePropertyChanged(nameof(SingleOrderAmountValueText));
            this.RaisePropertyChanged(nameof(RequiredSeedText));
            this.RaisePropertyChanged(nameof(ExpectedProfitText));
            this.RaisePropertyChanged(nameof(ExpectedFeeText));
            MarkUnsaved();
        }
    }

    public int Leverage
    {
        get => _leverage;
        set
        {
            this.RaiseAndSetIfChanged(ref _leverage, value);
            this.RaisePropertyChanged(nameof(ExpectedProfitText));
            MarkUnsaved();
        }
    }

    public string MarginMode
    {
        get => _marginMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _marginMode, value);
            this.RaisePropertyChanged(nameof(MarginModeCrossBg));
            this.RaisePropertyChanged(nameof(MarginModeCrossFg));
            this.RaisePropertyChanged(nameof(MarginModeIsolatedBg));
            this.RaisePropertyChanged(nameof(MarginModeIsolatedFg));
            MarkUnsaved();
        }
    }

    public IBrush MarginModeCrossBg    => _marginMode == "Cross"    ? new SolidColorBrush(Color.Parse("#2979FF")) : Brushes.Transparent;
    public IBrush MarginModeCrossFg    => _marginMode == "Cross"    ? Brushes.White : Brushes.Gray;
    public IBrush MarginModeIsolatedBg => _marginMode == "Isolated" ? new SolidColorBrush(Color.Parse("#2979FF")) : Brushes.Transparent;
    public IBrush MarginModeIsolatedFg => _marginMode == "Isolated" ? Brushes.White : Brushes.Gray;

    public int MartinCount
    {
        get => _martinCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _martinCount, value);
            this.RaisePropertyChanged(nameof(MartinStepText));
            this.RaisePropertyChanged(nameof(SingleOrderAmountText));
            this.RaisePropertyChanged(nameof(SingleOrderAmountValueText));
            this.RaisePropertyChanged(nameof(ExpectedFeeText));
            MarkUnsaved();
        }
    }

    public decimal MartinGap
    {
        get => _martinGap;
        set { this.RaiseAndSetIfChanged(ref _martinGap, value); MarkUnsaved(); }
    }

    public decimal TargetProfit
    {
        get => _targetProfit;
        set
        {
            this.RaiseAndSetIfChanged(ref _targetProfit, value);
            this.RaisePropertyChanged(nameof(ExpectedProfitText));
            MarkUnsaved();
        }
    }

    public List<decimal> MartinGapSteps
    {
        get => _martinGapSteps;
        set
        {
            _martinGapSteps = value;
            this.RaisePropertyChanged(nameof(MartinGapSteps));
            this.RaisePropertyChanged(nameof(HasCustomSteps));
            this.RaisePropertyChanged(nameof(IsUniformMode));
            this.RaisePropertyChanged(nameof(StepModeSummary));
            this.RaisePropertyChanged(nameof(CustomStepsLabel));
            MarkUnsaved();
        }
    }

    public List<decimal> TargetProfitSteps
    {
        get => _targetProfitSteps;
        set
        {
            _targetProfitSteps = value;
            this.RaisePropertyChanged(nameof(TargetProfitSteps));
            this.RaisePropertyChanged(nameof(HasCustomSteps));
            this.RaisePropertyChanged(nameof(IsUniformMode));
            this.RaisePropertyChanged(nameof(StepModeSummary));
            this.RaisePropertyChanged(nameof(CustomStepsLabel));
            MarkUnsaved();
        }
    }

    public bool HasCustomSteps => _martinGapSteps.Count > 0;
    public bool IsUniformMode  => !HasCustomSteps;

    public string StepModeSummary
    {
        get
        {
            if (!HasCustomSteps) return "";
            const int maxShow = 4;
            var shown  = _martinGapSteps.Take(maxShow).Select(v => v.ToString("F1"));
            var suffix = _martinGapSteps.Count > maxShow ? " ..." : "";
            return "간격: " + string.Join(" → ", shown) + suffix;
        }
    }

    public string CustomStepsLabel => HasCustomSteps ? "● 커스텀" : "";

    private decimal GetMartinGapForStep(int step) =>
        _martinGapSteps.Count > 0
            ? _martinGapSteps[Math.Clamp(step - 1, 0, _martinGapSteps.Count - 1)]
            : _martinGap;

    public bool StopLossEnabled
    {
        get => _stopLossEnabled;
        set { this.RaiseAndSetIfChanged(ref _stopLossEnabled, value); MarkUnsaved(); }
    }

    public decimal StopLossPercent
    {
        get => _stopLossPercent;
        set { this.RaiseAndSetIfChanged(ref _stopLossPercent, value); MarkUnsaved(); }
    }

    public bool AutoRepeat
    {
        get => _autoRepeat;
        set => this.RaiseAndSetIfChanged(ref _autoRepeat, value);
    }

    // ── 자동 계산 ─────────────────────────────────────────────────────

    public string SingleOrderAmountText =>
        TotalBudget > 0 && MartinCount > 0
            ? $"1회 진입금: {TotalBudget / MartinCount:F2} USDT" : "";

    public string SingleOrderAmountValueText =>
        TotalBudget > 0 && MartinCount > 0
            ? $"{TotalBudget / MartinCount:F2} USDT" : "-";

    public string RequiredSeedText     => TotalBudget > 0 ? $"${TotalBudget:N2}" : "-";

    public string ExpectedProfitText =>
        TotalBudget > 0 && Leverage > 0 && TargetProfit > 0
            ? $"${TotalBudget * Leverage * TargetProfit / 100m:N2}" : "-";

    public string ExpectedFeeText =>
        TotalBudget > 0 && MartinCount > 0
            ? $"${TotalBudget * 0.0005m * MartinCount:N2}" : "-";

    // ═══════════════════════════════════════════════════════════════════
    // 차트 설정
    // ═══════════════════════════════════════════════════════════════════

    public string[] BarOptions { get; } = ["1m", "5m", "15m", "1H", "4H", "1D"];

    public string SelectedBar
    {
        get => _selectedBar;
        set
        {
            if (_selectedBar == value) return;
            this.RaiseAndSetIfChanged(ref _selectedBar, value);
            _ = RestartChartWebSocketAsync();
        }
    }

    public string[] ChartTypeOptions { get; } = ["라인", "캔들"];

    public string SelectedChartTypeLabel
    {
        get => _selectedChartType == ChartType.Candle ? "캔들" : "라인";
        set
        {
            var t = value == "캔들" ? ChartType.Candle : ChartType.Line;
            if (_selectedChartType == t) return;
            _selectedChartType = t;
            this.RaisePropertyChanged(nameof(SelectedChartTypeLabel));
            this.RaisePropertyChanged(nameof(SelectedChartType));
        }
    }

    public ChartType SelectedChartType => _selectedChartType;

    public IReadOnlyList<Candle> RecentCandles
    {
        get => _recentCandles;
        private set => this.RaiseAndSetIfChanged(ref _recentCandles, value);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 런타임 상태 프로퍼티
    // ═══════════════════════════════════════════════════════════════════

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusBrush));
            this.RaisePropertyChanged(nameof(TabHeader));
        }
    }

    public IBrush StatusBrush => IsRunning ? Brushes.LightGreen : Brushes.Gray;
    public string StatusText  => IsRunning ? "봇 실행 중" : "대기 중";

    public decimal CurrentPrice
    {
        get => _currentPrice;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentPrice, value);
            this.RaisePropertyChanged(nameof(CurrentPriceText));
        }
    }

    public string CurrentPriceText => CurrentPrice > 0 ? $"${CurrentPrice:N2}" : "--";

    public string PositionStatusText
    {
        get => _positionStatusText;
        private set => this.RaiseAndSetIfChanged(ref _positionStatusText, value);
    }

    public string DirectionText
    {
        get => _directionText;
        private set => this.RaiseAndSetIfChanged(ref _directionText, value);
    }

    public IBrush DirectionBrush
    {
        get => _directionBrush;
        private set => this.RaiseAndSetIfChanged(ref _directionBrush, value);
    }

    public int MartinStep
    {
        get => _martinStep;
        private set
        {
            this.RaiseAndSetIfChanged(ref _martinStep, value);
            this.RaisePropertyChanged(nameof(MartinStepText));
        }
    }

    public string MartinStepText => MartinStep > 0 ? $"{MartinStep} / {MartinCount}" : "-";

    public decimal TotalAmount
    {
        get => _totalAmount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _totalAmount, value);
            this.RaisePropertyChanged(nameof(TotalAmountText));
        }
    }

    public string TotalAmountText => TotalAmount > 0 ? $"{TotalAmount:F2} USDT" : "-";

    public decimal AvgEntryPrice
    {
        get => _avgEntryPrice;
        private set
        {
            this.RaiseAndSetIfChanged(ref _avgEntryPrice, value);
            this.RaisePropertyChanged(nameof(AvgEntryPriceText));
        }
    }

    public string AvgEntryPriceText => AvgEntryPrice > 0 ? $"{AvgEntryPrice:N2}" : "-";

    public decimal NextMartinPrice
    {
        get => _nextMartinPrice;
        private set
        {
            this.RaiseAndSetIfChanged(ref _nextMartinPrice, value);
            this.RaisePropertyChanged(nameof(NextMartinPriceText));
        }
    }

    public string NextMartinPriceText => NextMartinPrice > 0 ? $"{NextMartinPrice:N2}" : "-";

    public decimal UnrealizedPnlPct
    {
        get => _unrealizedPnlPct;
        private set
        {
            this.RaiseAndSetIfChanged(ref _unrealizedPnlPct, value);
            this.RaisePropertyChanged(nameof(UnrealizedPnlText));
            PnlBrush = value > 0 ? Brushes.LightGreen : value < 0 ? Brushes.Tomato : Brushes.Gray;
        }
    }

    public string UnrealizedPnlText => UnrealizedPnlPct != 0 ? $"{UnrealizedPnlPct:+0.00;-0.00}%" : "-";

    public IBrush PnlBrush
    {
        get => _pnlBrush;
        private set => this.RaiseAndSetIfChanged(ref _pnlBrush, value);
    }

    public decimal RealizedPnl
    {
        get => _realizedPnl;
        private set
        {
            this.RaiseAndSetIfChanged(ref _realizedPnl, value);
            this.RaisePropertyChanged(nameof(RealizedPnlText));
        }
    }

    public string RealizedPnlText => RealizedPnl != 0 ? $"{RealizedPnl:+0.00;-0.00} USDT" : "-";

    // ── 통계 ─────────────────────────────────────────────────────────

    public decimal TotalPnl
    {
        get => _totalPnl;
        private set
        {
            this.RaiseAndSetIfChanged(ref _totalPnl, value);
            this.RaisePropertyChanged(nameof(TotalPnlText));
            this.RaisePropertyChanged(nameof(TotalPnlBrush));
        }
    }

    public string TotalPnlText  => $"{TotalPnl:+0.00;-0.00;0.00} USDT";
    public IBrush TotalPnlBrush => TotalPnl > 0 ? Brushes.LightGreen : TotalPnl < 0 ? Brushes.Tomato : Brushes.Gray;

    public int WinCount
    {
        get => _winCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _winCount, value);
            this.RaisePropertyChanged(nameof(TradeCountText));
            this.RaisePropertyChanged(nameof(WinRateText));
            this.RaisePropertyChanged(nameof(AvgPnlText));
        }
    }

    public int LossCount
    {
        get => _lossCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _lossCount, value);
            this.RaisePropertyChanged(nameof(TradeCountText));
            this.RaisePropertyChanged(nameof(WinRateText));
            this.RaisePropertyChanged(nameof(AvgPnlText));
        }
    }

    public string TradeCountText => $"{WinCount + LossCount}회";
    public string WinRateText    => (WinCount + LossCount) > 0
        ? $"{(double)WinCount / (WinCount + LossCount) * 100:F1}%" : "-";
    public string AvgPnlText     => (WinCount + LossCount) > 0
        ? $"{TotalPnl / (WinCount + LossCount):+0.00;-0.00} USDT" : "-";

    // ═══════════════════════════════════════════════════════════════════
    // 봇 제어
    // ═══════════════════════════════════════════════════════════════════

    private async Task StartBotAsync()
    {
        var global = _getGlobalConfig();
        var config = BuildConfig(global);

        var rest      = new OkxRestClient(config.ApiKey, config.ApiSecret, config.Passphrase,
                            NullLogger<OkxRestClient>.Instance);
        var ws        = new OkxWebSocketClient(NullLogger<OkxWebSocketClient>.Instance);
        _dataProvider = new OkxDataProvider(ws, rest, config.Symbol);

        IOrderExecutor executor;
        if (global.IsBacktestMode)
        {
            executor = new OKXTradingBot.Infrastructure.Backtest.VirtualOrderExecutor(config.TotalBudget);
            AddLog($"[백테스트] 가상 잔고 {config.TotalBudget:N2} USDT 으로 시작");
        }
        else
        {
            executor = new OkxOrderExecutor(rest, NullLogger<OkxOrderExecutor>.Instance);
        }

        var notifier = new TelegramNotifier(config.TelegramBotToken, config.TelegramChatId);

        _core = new TradingCore(_dataProvider, executor, config,
                    NullLogger<TradingCore>.Instance, notifier);

        _core.OnPositionUpdated += HandlePositionUpdated;
        _core.OnLogMessage      += HandleLogMessage;

        _cts      = new CancellationTokenSource();
        IsRunning = true;
        AddLog(global.IsBacktestMode ? "봇 시작 중... [백테스트]" : "봇 시작 중... [실거래]");

        StartPricePolling();
        await _core.StartAsync(AutoRepeat, _cts.Token);
    }

    private async Task StopBotAsync()
    {
        StopPricePolling();
        if (_core != null) await _core.StopAsync();
        _cts?.Cancel();
        IsRunning = false;
    }

    private async Task ClosePositionAsync()
    {
        AddLog("[제어] 포지션 강제 종료 요청");
        StopPricePolling();
        if (_core != null) await _core.StopAsync();
        _cts?.Cancel();
        IsRunning = false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 차트 WebSocket
    // ═══════════════════════════════════════════════════════════════════

    private async Task RefreshChartAsync()
    {
        try
        {
            var rest    = new OkxRestClient("", "", "", NullLogger<OkxRestClient>.Instance);
            var candles = await rest.GetCandlesAsync(_symbol, 200, _selectedBar);
            _candleBuffer = candles;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RecentCandles = _candleBuffer.ToList());
        }
        catch { }
    }

    public async Task RestartChartWebSocketAsync()
    {
        _chartWsCts?.Cancel();
        if (_chartWs != null)
        {
            try { await _chartWs.StopAsync(); } catch { }
            await _chartWs.DisposeAsync();
            _chartWs = null;
        }

        await RefreshChartAsync();

        _chartWsCts = new CancellationTokenSource();
        _chartWs    = new OkxWebSocketClient(NullLogger<OkxWebSocketClient>.Instance);

        _chartWs.OnCandleUpdated   += OnChartCandleUpdated;
        _chartWs.OnCandleCompleted += OnChartCandleCompleted;

        _ = Task.Run(async () =>
        {
            try
            {
                await _chartWs.StartAsync(_symbol, _chartWsCts.Token, _selectedBar);
            }
            catch
            {
                _chartTimer = Observable
                    .Interval(TimeSpan.FromSeconds(30))
                    .Subscribe(_ => RefreshChartAsync().ConfigureAwait(false));
            }
        });
    }

    private void OnChartCandleUpdated(object? sender, Candle live)
    {
        _wsTickCount++;
        if (_candleBuffer.Count == 0) return;
        var last = _candleBuffer[^1];
        if (last.Timestamp == live.Timestamp)
            _candleBuffer[^1] = live;
        else
            _candleBuffer.Add(live);
        while (_candleBuffer.Count > 200) _candleBuffer.RemoveAt(0);
        var snapshot = _candleBuffer.ToList();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RecentCandles = snapshot);
    }

    private void OnChartCandleCompleted(object? sender, Candle completed)
        => _ = RefreshChartAsync();

    // ═══════════════════════════════════════════════════════════════════
    // 가격 polling
    // ═══════════════════════════════════════════════════════════════════

    private void StartPricePolling()
    {
        _priceTimer = Observable
            .Interval(TimeSpan.FromSeconds(1))
            .SelectMany(_ => Observable.FromAsync(async () =>
            {
                if (_dataProvider == null) return 0m;
                try { return await _dataProvider.GetCurrentPriceAsync(); }
                catch { return 0m; }
            }))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(price => { if (price > 0) CurrentPrice = price; });
    }

    private void StopPricePolling()
    {
        _priceTimer?.Dispose();
        _priceTimer = null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 포지션 / 로그 이벤트
    // ═══════════════════════════════════════════════════════════════════

    private void HandlePositionUpdated(object? sender, Position pos)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            PositionStatusText = pos.Status switch
            {
                PositionStatus.None   => "대기 중",
                PositionStatus.Open   => "포지션 보유 중",
                PositionStatus.Closed => "청산 완료",
                _                     => "-"
            };

            if (pos.Status == PositionStatus.Open)
            {
                DirectionText    = pos.Direction == TradeDirection.Long ? "LONG  ▲" : "SHORT  ▼";
                DirectionBrush   = pos.Direction == TradeDirection.Long ? Brushes.LightGreen : Brushes.Tomato;
                MartinStep       = pos.MartinStep;
                TotalAmount      = pos.TotalAmount;
                AvgEntryPrice    = pos.AvgEntryPrice;
                NextMartinPrice  = pos.GetNextMartinTriggerPrice(GetMartinGapForStep(pos.MartinStep));
                if (CurrentPrice > 0)
                    UnrealizedPnlPct = pos.GetUnrealizedPnlPercent(CurrentPrice);
            }
            else if (pos.Status == PositionStatus.Closed)
            {
                var pnlPct = pos.TotalAmount > 0 && Leverage > 0
                    ? pos.RealizedPnl / (pos.TotalAmount * Leverage) * 100m : 0m;
                var record = new TradeRecord
                {
                    Number        = ++_tradeSeq,
                    Symbol        = _symbol,
                    Direction     = pos.Direction == TradeDirection.Long ? "LONG" : "SHORT",
                    AvgEntry      = pos.AvgEntryPrice,
                    TotalInvested = pos.TotalAmount,
                    MartinStep    = pos.MartinStep,
                    MartinMax     = MartinCount,
                    PnlAmount     = pos.RealizedPnl,
                    PnlPercent    = pnlPct,
                    ClosedAt      = pos.ClosedAt ?? DateTime.Now
                };
                TradeHistory.Insert(0, record);
                TotalPnl += pos.RealizedPnl;
                if (pos.RealizedPnl > 0) WinCount++;
                else                     LossCount++;

                DirectionText    = "-";
                DirectionBrush   = Brushes.Gray;
                MartinStep       = 0;
                TotalAmount      = 0;
                AvgEntryPrice    = 0;
                NextMartinPrice  = 0;
                UnrealizedPnlPct = 0;
                RealizedPnl      = pos.RealizedPnl;
            }
        });
    }

    private void HandleLogMessage(object? sender, string message)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => AddLog(message));

    public void AddLog(string message)
    {
        Logs.Insert(0, message);
        while (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 설정 변환 (저장 / 복원)
    // ═══════════════════════════════════════════════════════════════════

    public SymbolTabSettings ToSettings() => new()
    {
        Symbol            = _symbol,
        TotalBudget       = _totalBudget,
        Leverage          = _leverage,
        MarginMode        = _marginMode,
        MartinCount       = _martinCount,
        MartinGap         = _martinGap,
        TargetProfit      = _targetProfit,
        MartinGapSteps    = new List<decimal>(_martinGapSteps),
        TargetProfitSteps = new List<decimal>(_targetProfitSteps),
        StopLossEnabled   = _stopLossEnabled,
        StopLossPercent   = _stopLossPercent,
    };

    public void ApplySettings(SymbolTabSettings s)
    {
        _isLoading        = true;
        _symbol           = s.Symbol;
        _totalBudget      = s.TotalBudget;
        _leverage         = s.Leverage;
        _marginMode       = s.MarginMode;
        _martinCount      = s.MartinCount;
        _martinGap        = s.MartinGap;
        _targetProfit     = s.TargetProfit;
        _martinGapSteps   = new List<decimal>(s.MartinGapSteps);
        _targetProfitSteps = new List<decimal>(s.TargetProfitSteps);
        _stopLossEnabled  = s.StopLossEnabled;
        _stopLossPercent  = s.StopLossPercent;
        _isLoading = false;

        // UI 갱신
        this.RaisePropertyChanged(nameof(Symbol));
        this.RaisePropertyChanged(nameof(TabHeader));
        this.RaisePropertyChanged(nameof(TotalBudget));
        this.RaisePropertyChanged(nameof(Leverage));
        this.RaisePropertyChanged(nameof(MarginMode));
        this.RaisePropertyChanged(nameof(MarginModeCrossBg));
        this.RaisePropertyChanged(nameof(MarginModeCrossFg));
        this.RaisePropertyChanged(nameof(MarginModeIsolatedBg));
        this.RaisePropertyChanged(nameof(MarginModeIsolatedFg));
        this.RaisePropertyChanged(nameof(MartinCount));
        this.RaisePropertyChanged(nameof(MartinGap));
        this.RaisePropertyChanged(nameof(TargetProfit));
        this.RaisePropertyChanged(nameof(HasCustomSteps));
        this.RaisePropertyChanged(nameof(IsUniformMode));
        this.RaisePropertyChanged(nameof(StepModeSummary));
        this.RaisePropertyChanged(nameof(CustomStepsLabel));
        this.RaisePropertyChanged(nameof(StopLossEnabled));
        this.RaisePropertyChanged(nameof(StopLossPercent));
        this.RaisePropertyChanged(nameof(SingleOrderAmountText));
        this.RaisePropertyChanged(nameof(SingleOrderAmountValueText));
        this.RaisePropertyChanged(nameof(RequiredSeedText));
        this.RaisePropertyChanged(nameof(ExpectedProfitText));
        this.RaisePropertyChanged(nameof(ExpectedFeeText));
    }

    /// <summary>
    /// 심볼 목록이 나중에 로드된 경우 ComboBox가 선택값을 잃을 수 있으므로 강제 재알림.
    /// </summary>
    public void RefreshSymbolBinding()
    {
        // _symbol을 잠깐 바꿨다가 복원하면 ComboBox가 재탐색함
        var current = _symbol;
        _symbol = "";
        this.RaisePropertyChanged(nameof(Symbol));
        _symbol = current;
        this.RaisePropertyChanged(nameof(Symbol));
    }

    public void MarkSaved()
    {
        _savedSnapshot    = ToSettings();
        HasUnsavedChanges = false;
    }

    private void MarkUnsaved()
    {
        if (_isLoading) return;
        var changed = DiffersFromSnapshot();
        HasUnsavedChanges = changed;
        _onChanged();
    }

    private bool DiffersFromSnapshot() =>
        _symbol          != _savedSnapshot.Symbol
     || _totalBudget     != _savedSnapshot.TotalBudget
     || _leverage        != _savedSnapshot.Leverage
     || _marginMode      != _savedSnapshot.MarginMode
     || _martinCount     != _savedSnapshot.MartinCount
     || _martinGap       != _savedSnapshot.MartinGap
     || _targetProfit    != _savedSnapshot.TargetProfit
     || !_martinGapSteps.SequenceEqual(_savedSnapshot.MartinGapSteps)
     || !_targetProfitSteps.SequenceEqual(_savedSnapshot.TargetProfitSteps)
     || _stopLossEnabled != _savedSnapshot.StopLossEnabled
     || _stopLossPercent != _savedSnapshot.StopLossPercent;

    // ═══════════════════════════════════════════════════════════════════
    // TradeConfig 빌드
    // ═══════════════════════════════════════════════════════════════════

    private TradeConfig BuildConfig(GlobalBotConfig global) => new()
    {
        ApiKey                 = global.ApiKey,
        ApiSecret              = global.ApiSecret,
        Passphrase             = global.Passphrase,
        GptApiKey              = global.GptApiKey,
        GptModel               = global.GptModel,
        GptCandleCount         = global.GptCandleCount,
        GptConfidenceThreshold = global.GptConfidenceThreshold,
        Symbol                 = _symbol,
        TotalBudget            = _totalBudget,
        Leverage               = _leverage,
        MarginMode             = Enum.Parse<OKXTradingBot.Core.Models.MarginMode>(_marginMode),
        MartinCount            = _martinCount,
        MartinGap              = _martinGap,
        TargetProfit           = _targetProfit,
        MartinGapSteps         = new List<decimal>(_martinGapSteps),
        TargetProfitSteps      = new List<decimal>(_targetProfitSteps),
        StopLossEnabled        = _stopLossEnabled,
        StopLossPercent        = _stopLossPercent,
        TelegramBotToken       = global.TelegramBotToken,
        TelegramChatId         = global.TelegramChatId,
    };
}
