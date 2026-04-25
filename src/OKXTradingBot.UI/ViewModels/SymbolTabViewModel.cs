using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;
using OKXTradingBot.Core.Trading;
using OKXTradingBot.Infrastructure.Backtest;
using OKXTradingBot.Infrastructure.Gpt;
using OKXTradingBot.Infrastructure.Notifications;
using OKXTradingBot.Infrastructure.OKX;
using OKXTradingBot.Infrastructure.Persistence;
using OKXTradingBot.UI.Services;
using OKXTradingBot.UI.Views;
using ReactiveUI;

namespace OKXTradingBot.UI.ViewModels;

public record BuyPlanDialogArgs(
    string Symbol,
    int Leverage,
    string MarginMode,
    int ConfiguredCount,
    int EffectiveCount,
    List<(string Label, string Amount, bool IsOver)> Steps,
    decimal RequiredTotal,
    decimal Budget,
    string Warning,
    bool IsMockMode);


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
    public bool   UseGpt                 { get; init; } = false;
    public int    GptAnalysisInterval    { get; init; } = 5;
    public string TelegramBotToken       { get; init; } = "";
    public string TelegramChatId         { get; init; } = "";
    public bool   TelegramEnabled        { get; init; }
    public bool   NotifyBotStartStop     { get; init; } = true;
    public bool   NotifyEntry            { get; init; } = true;
    public bool   NotifyMartin           { get; init; } = true;
    public bool   NotifyTakeProfit       { get; init; } = true;
    public bool   NotifyStopLoss         { get; init; } = true;
    public bool   NotifyError            { get; init; } = true;
    public bool   QuietHoursEnabled      { get; init; }
    public string QuietStart             { get; init; } = "23:00";
    public string QuietEnd               { get; init; } = "07:00";
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
    private readonly Func<string, (decimal MinSz, decimal CtVal)>? _getSymbolInfo;

    // ── 트레이딩 내부 ──────────────────────────────────────────────────
    private TradingCore?        _core;
    private IDataProvider?      _dataProvider;
    private NotificationConfig  _notifyConfig = new();
    private CancellationTokenSource? _cts;
    private IDisposable?        _priceTimer;
    private IDisposable?        _chartTimer;
    private OkxWebSocketClient? _chartWs;
    private CancellationTokenSource? _chartWsCts;
    private List<Candle>        _candleBuffer = new();
    private bool                _isLoading    = false;
    private int                 _wsTickCount  = 0;
    private int                 _tradeSeq     = 0;

    // ── 영속화 ────────────────────────────────────────────────────────
    private readonly TradeHistoryRepository _tradeRepo = new();
    private LogFileService? _logService;

    /// <summary>
    /// 모의거래 모드로 시작 시 사용자에게 확인을 요청하는 콜백.
    /// View(code-behind)에서 다이얼로그를 띄워 true/false 반환.
    /// null이면 확인 없이 진행.
    /// </summary>
    public Func<Task<bool>>? ConfirmMockStart    { get; set; }
    public Func<Task<bool>>? ConfirmStop         { get; set; }
    public Func<Task<bool>>? ConfirmForceClose   { get; set; }

    /// <summary>매수계획 다이얼로그 — View에서 주입. bool=isMockMode, 반환값=확인여부</summary>
    public Func<BuyPlanDialogArgs, Task<bool>>? ShowBuyPlan { get; set; }

    // ── 설정 ──────────────────────────────────────────────────────────
    private string        _symbol            = "";
    private decimal       _totalBudget       = 100m;
    private int           _leverage          = 10;
    private string        _marginMode        = "Cross";
    private int           _martinCount       = 9;
    private decimal       _martinGap         = 0.5m;
    private decimal       _targetProfit      = 0.5m;
    private List<decimal>    _martinGapSteps    = new();
    private List<decimal>    _targetProfitSteps = new();
    private MartinAmountMode _amountMode           = MartinAmountMode.Equal;
    private List<decimal>    _martinAmountWeights  = new();
    private bool             _stopLossEnabled   = false;
    private decimal       _stopLossPercent   = 3.0m;
    private bool          _autoRepeat        = true;
    private decimal       _accountBalance    = 1000m;
    private decimal       _usdKrwRate        = 0m;    // 0 = 아직 미조회
    private decimal       _takerFeeRate      = 0.0005m; // 봇 시작 시 API로 갱신

    // ── 차트 ──────────────────────────────────────────────────────────
    private string    _selectedBar       = "1D";
    private ChartType _selectedChartType = ChartType.Candle;

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
    public ReactiveCommand<Unit, Unit> FetchBalanceCommand   { get; }

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
        Func<string, (decimal MinSz, decimal CtVal)>? getSymbolInfo = null,
        SymbolTabSettings? initialSettings = null)
    {
        _symbolOptions   = symbolOptions;
        _onChanged       = onChanged;
        _getGlobalConfig = getGlobalConfig;
        _isSymbolInUse   = isSymbolInUse;
        _getSymbolInfo   = getSymbolInfo;

        if (initialSettings != null)
            ApplySettings(initialSettings);

        _savedSnapshot = ToSettings();

        var canStart = this.WhenAnyValue(x => x.IsRunning).Select(r => !r);
        var canStop  = this.WhenAnyValue(x => x.IsRunning);

        StartCommand         = ReactiveCommand.CreateFromTask(StartBotAsync,      canStart);
        StopCommand          = ReactiveCommand.CreateFromTask(StopBotAsync,       canStop);
        ClosePositionCommand = ReactiveCommand.CreateFromTask(ClosePositionAsync,  canStop);
        FetchBalanceCommand  = ReactiveCommand.CreateFromTask(FetchBalanceAsync);

        StartCommand.ThrownExceptions.Subscribe(ex        => AddLog($"[오류] {ex.Message}"));
        StopCommand.ThrownExceptions.Subscribe(ex         => AddLog($"[오류] {ex.Message}"));
        ClosePositionCommand.ThrownExceptions.Subscribe(ex => AddLog($"[오류] {ex.Message}"));
        FetchBalanceCommand.ThrownExceptions.Subscribe(ex  => AddLog($"[오류] {ex.Message}"));

        // 초기 차트 로드
        _ = RestartChartWebSocketAsync();

        // DB에서 이전 거래 기록 로드
        _ = Task.Run(LoadTradeHistoryFromDb);

        // USD/KRW 환율 조회
        _ = FetchExchangeRateAsync();
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

    // 심볼 검색
    private string _symbolSearch = "";
    public string SymbolSearch
    {
        get => _symbolSearch;
        set
        {
            this.RaiseAndSetIfChanged(ref _symbolSearch, value);
            this.RaisePropertyChanged(nameof(FilteredSymbolOptions));
        }
    }

    public IEnumerable<string> FilteredSymbolOptions =>
        string.IsNullOrEmpty(_symbolSearch)
            ? _symbolOptions
            : _symbolOptions.Where(s => s.Contains(_symbolSearch, StringComparison.OrdinalIgnoreCase));

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

    public decimal? TotalBudget
    {
        get => _totalBudget;
        set
        {
            this.RaiseAndSetIfChanged(ref _totalBudget, value ?? 100m);
            this.RaisePropertyChanged(nameof(SingleOrderAmountText));
            this.RaisePropertyChanged(nameof(SingleOrderAmountValueText));
            this.RaisePropertyChanged(nameof(RequiredSeedText));
            this.RaisePropertyChanged(nameof(TotalPositionText));
            this.RaisePropertyChanged(nameof(ExpectedProfitText));
            this.RaisePropertyChanged(nameof(ExpectedFeeText));
            this.RaisePropertyChanged(nameof(TotalBudgetKrwText));
            this.RaisePropertyChanged(nameof(BudgetWarningText));
            this.RaisePropertyChanged(nameof(HasBudgetWarning));
            this.RaisePropertyChanged(nameof(RequiredTotalText));
            this.RaisePropertyChanged(nameof(RequiredTotalColor));
            MarkUnsaved();
        }
    }

    public decimal? AccountBalance
    {
        get => _accountBalance;
        set
        {
            this.RaiseAndSetIfChanged(ref _accountBalance, value ?? 1000m);
            this.RaisePropertyChanged(nameof(AccountBalanceKrwText));
        }
    }

    public decimal UsdKrwRate
    {
        get => _usdKrwRate;
        private set
        {
            this.RaiseAndSetIfChanged(ref _usdKrwRate, value);
            this.RaisePropertyChanged(nameof(ExchangeRateText));
            this.RaisePropertyChanged(nameof(TotalBudgetKrwText));
            this.RaisePropertyChanged(nameof(AccountBalanceKrwText));
        }
    }

    public string ExchangeRateText    => _usdKrwRate > 0 ? $"1 USD = ₩{_usdKrwRate:N0}" : "환율 조회 중...";
    public string TotalBudgetKrwText  => _usdKrwRate > 0 ? $"≈ ₩{_totalBudget * _usdKrwRate:N0}" : "";
    public string AccountBalanceKrwText => _usdKrwRate > 0 ? $"≈ ₩{_accountBalance * _usdKrwRate:N0}" : "";

    public int? Leverage
    {
        get => _leverage;
        set
        {
            this.RaiseAndSetIfChanged(ref _leverage, value ?? 10);
            this.RaisePropertyChanged(nameof(TotalPositionText));
            this.RaisePropertyChanged(nameof(ExpectedProfitText));
            this.RaisePropertyChanged(nameof(BudgetWarningText));
            this.RaisePropertyChanged(nameof(HasBudgetWarning));
            this.RaisePropertyChanged(nameof(RequiredTotalText));
            this.RaisePropertyChanged(nameof(RequiredTotalColor));
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

    public int? MartinCount
    {
        get => _martinCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _martinCount, value ?? 9);
            this.RaisePropertyChanged(nameof(MartinStepText));
            this.RaisePropertyChanged(nameof(SingleOrderAmountText));
            this.RaisePropertyChanged(nameof(SingleOrderAmountValueText));
            this.RaisePropertyChanged(nameof(ExpectedFeeText));
            this.RaisePropertyChanged(nameof(BudgetWarningText));
            this.RaisePropertyChanged(nameof(HasBudgetWarning));
            this.RaisePropertyChanged(nameof(RequiredTotalText));
            this.RaisePropertyChanged(nameof(RequiredTotalColor));
            MarkUnsaved();
        }
    }

    public decimal? MartinGap
    {
        get => _martinGap;
        set { this.RaiseAndSetIfChanged(ref _martinGap, value ?? 0.5m); MarkUnsaved(); }
    }

    public decimal? TargetProfit
    {
        get => _targetProfit;
        set
        {
            this.RaiseAndSetIfChanged(ref _targetProfit, value ?? 0.5m);
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

    public MartinAmountMode AmountMode
    {
        get => _amountMode;
        set
        {
            _amountMode = value;
            this.RaisePropertyChanged(nameof(AmountMode));
            this.RaisePropertyChanged(nameof(HasCustomSteps));
            this.RaisePropertyChanged(nameof(IsUniformMode));
            this.RaisePropertyChanged(nameof(StepModeSummary));
            this.RaisePropertyChanged(nameof(CustomStepsLabel));
            this.RaisePropertyChanged(nameof(BudgetWarningText));
            this.RaisePropertyChanged(nameof(HasBudgetWarning));
            this.RaisePropertyChanged(nameof(RequiredTotalText));
            this.RaisePropertyChanged(nameof(RequiredTotalColor));
            MarkUnsaved();
        }
    }

    public bool HasCustomSteps => _martinGapSteps.Count > 0 || _martinAmountWeights.Count > 0 || _amountMode != MartinAmountMode.Equal;
    public bool IsUniformMode  => !HasCustomSteps;

    public string StepModeSummary
    {
        get
        {
            var parts = new List<string>();

            if (_martinGapSteps.Count > 0)
            {
                const int maxShow = 4;
                var shown  = _martinGapSteps.Take(maxShow).Select(v => v.ToString("F1"));
                var suffix = _martinGapSteps.Count > maxShow ? " ..." : "";
                parts.Add("간격: " + string.Join(" → ", shown) + suffix);
            }

            if (_amountMode == MartinAmountMode.Multiplier)
                parts.Add("배수");
            else if (_amountMode == MartinAmountMode.Fibonacci)
                parts.Add("피보나치");

            return string.Join(" | ", parts);
        }
    }

    public string CustomStepsLabel => HasCustomSteps ? "● 커스텀" : "";

    public List<decimal> MartinAmountWeights
    {
        get => _martinAmountWeights;
        set
        {
            _martinAmountWeights = value;
            this.RaisePropertyChanged(nameof(MartinAmountWeights));
            this.RaisePropertyChanged(nameof(HasCustomSteps));
            this.RaisePropertyChanged(nameof(IsUniformMode));
            this.RaisePropertyChanged(nameof(StepModeSummary));
            this.RaisePropertyChanged(nameof(CustomStepsLabel));
            this.RaisePropertyChanged(nameof(BudgetWarningText));
            this.RaisePropertyChanged(nameof(HasBudgetWarning));
            this.RaisePropertyChanged(nameof(RequiredTotalText));
            this.RaisePropertyChanged(nameof(RequiredTotalColor));
            MarkUnsaved();
        }
    }

    public void ResetCustomSteps()
    {
        _martinGapSteps    = new List<decimal>();
        _martinAmountWeights = new List<decimal>();
        _amountMode          = MartinAmountMode.Equal;
        this.RaisePropertyChanged(nameof(MartinGapSteps));
        this.RaisePropertyChanged(nameof(MartinAmountWeights));
        this.RaisePropertyChanged(nameof(AmountMode));
        this.RaisePropertyChanged(nameof(HasCustomSteps));
        this.RaisePropertyChanged(nameof(IsUniformMode));
        this.RaisePropertyChanged(nameof(StepModeSummary));
        this.RaisePropertyChanged(nameof(CustomStepsLabel));
        MarkUnsaved();
    }

    private decimal GetMartinGapForStep(int step) =>
        _martinGapSteps.Count > 0
            ? _martinGapSteps[Math.Clamp(step - 1, 0, _martinGapSteps.Count - 1)]
            : _martinGap;

    public bool StopLossEnabled
    {
        get => _stopLossEnabled;
        set { this.RaiseAndSetIfChanged(ref _stopLossEnabled, value); MarkUnsaved(); }
    }

    public decimal? StopLossPercent
    {
        get => _stopLossPercent;
        set { this.RaiseAndSetIfChanged(ref _stopLossPercent, value ?? 3.0m); MarkUnsaved(); }
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

    public string RequiredSeedText      => TotalBudget > 0 ? $"${TotalBudget:N2}" : "-";
    public string TotalPositionText     => TotalBudget > 0 && Leverage > 0 ? $"${TotalBudget * Leverage:N2}" : "-";

    public string BudgetWarningText => ComputeBudgetAdjustment().Warning;
    public bool   HasBudgetWarning  => !string.IsNullOrEmpty(BudgetWarningText);

    /// <summary>
    /// 투자 필요금액: 최소주문금액을 1회차로 고정했을 때 전체 회차에 필요한 금액.
    /// 배수/회차 변경 시 실시간으로 변한다.
    /// </summary>
    public decimal GetMinOrderUsdt()
    {
        if (_currentPrice <= 0 || _getSymbolInfo == null) return 0m;
        var (minSz, ctVal) = _getSymbolInfo(_symbol);
        if (minSz <= 0 || ctVal <= 0) return 0m;
        return minSz * ctVal * _currentPrice / (_leverage > 0 ? _leverage : 1);
    }

    private decimal ComputeRequiredTotal()
    {
        if (_currentPrice <= 0 || _getSymbolInfo == null)
            return _totalBudget;

        var (minSz, ctVal) = _getSymbolInfo(_symbol);
        if (minSz <= 0 || ctVal <= 0)
            return _totalBudget;

        var minUsdt = minSz * ctVal * _currentPrice / (_leverage > 0 ? _leverage : 1);

        if (_martinAmountWeights.Count == 0 || _amountMode == MartinAmountMode.Equal)
        {
            var firstStep = Math.Max(_totalBudget / _martinCount, minUsdt);
            return firstStep * _martinCount;
        }
        else
        {
            var absWeights = _martinAmountWeights.Take(_martinCount).ToList();
            if (absWeights.Count == 0 || absWeights[0] <= 0) return _totalBudget;

            var weightSum = absWeights.Sum();
            var firstStep = Math.Max(_totalBudget / weightSum * absWeights[0], minUsdt);
            return firstStep / absWeights[0] * weightSum;
        }
    }

    public string RequiredTotalText  => $"투자 필요금액: {ComputeRequiredTotal():F2} USDT";
    public string RequiredTotalColor => ComputeRequiredTotal() > _totalBudget ? "#FF5252" : "#2979FF";

    /// <summary>
    /// 예산 + 최소주문금액 기준으로 유효 회차/예산을 계산.
    /// 1회차 금액이 최소주문금액 미달이면 1회차를 최소금액으로 고정하고
    /// 예산 안에 들어오는 최대 회차를 역산한다. 배수는 건드리지 않는다.
    /// </summary>
    private (int EffectiveCount, decimal EffectiveBudget, string Warning) ComputeBudgetAdjustment()
    {
        if (_currentPrice <= 0 || _getSymbolInfo == null)
            return (_martinCount, _totalBudget, "");

        var (minSz, ctVal) = _getSymbolInfo(_symbol);
        if (minSz <= 0 || ctVal <= 0)
            return (_martinCount, _totalBudget, "");

        var minUsdt = minSz * ctVal * _currentPrice / (_leverage > 0 ? _leverage : 1);

        // ── Equal 모드 ──
        if (_martinAmountWeights.Count == 0 || _amountMode == MartinAmountMode.Equal)
        {
            var stepAmt = _totalBudget / _martinCount;
            if (stepAmt >= minUsdt) return (_martinCount, _totalBudget, "");

            var maxSteps = (int)(_totalBudget / minUsdt);
            if (maxSteps < 1) maxSteps = 1;
            return (maxSteps, _totalBudget,
                $"1회차 {stepAmt:F2} < 최소 {minUsdt:F2} USDT → {maxSteps}회차로 조정");
        }

        // ── 가중치(배수/피보나치) 모드 ──
        // MartinAmountWeights 는 절대 가중치 [1, 2, 6, 12, 36 …]
        var absWeights = _martinAmountWeights.Take(_martinCount).ToList();
        if (absWeights.Count == 0) return (_martinCount, _totalBudget, "");

        var weightSum = absWeights.Sum();
        var firstAmt  = _totalBudget * absWeights[0] / weightSum;

        if (firstAmt >= minUsdt) return (_martinCount, _totalBudget, "");

        // 1회차를 minUsdt로 고정 후 예산 안에 들어오는 최대 회차 탐색
        for (int n = _martinCount; n >= 1; n--)
        {
            var partialSum  = absWeights.Take(n).Sum();
            var totalNeeded = minUsdt / absWeights[0] * partialSum;
            if (totalNeeded <= _totalBudget)
            {
                var msg = n < _martinCount
                    ? $"1회차 {firstAmt:F2} < 최소 {minUsdt:F2} USDT → 1회차 최소금액 고정, {n}회차로 조정"
                    : $"1회차 {firstAmt:F2} < 최소 {minUsdt:F2} USDT → 1회차 최소금액으로 고정";
                return (n, totalNeeded, msg);
            }
        }

        return (1, minUsdt,
            $"1회차 최소 {minUsdt:F2} USDT → 예산 {_totalBudget:F2} USDT 부족, 1회차만 가능");
    }

    public BuyPlanDialogArgs BuildBuyPlanArgs(bool isMockMode)
    {
        var (effectiveCount, effectiveBudget, warning) = ComputeBudgetAdjustment();

        // 유효 회차 기준 금액 계산
        var planConfig = new OKXTradingBot.Core.Models.TradeConfig
        {
            TotalBudget         = effectiveBudget,
            MartinCount         = effectiveCount,
            MartinAmountWeights = _martinAmountWeights.Take(effectiveCount).ToList(),
            AmountMode          = _amountMode
        };
        var effectiveAmounts = planConfig.GetAllStepAmounts();

        // 전체 회차 목록 (초과 회차는 IsOver=true)
        var origConfig = new OKXTradingBot.Core.Models.TradeConfig
        {
            TotalBudget         = _totalBudget,
            MartinCount         = _martinCount,
            MartinAmountWeights = _martinAmountWeights,
            AmountMode          = _amountMode
        };
        var steps = new List<(string Label, string Amount, bool IsOver)>();
        for (int i = 0; i < _martinCount; i++)
        {
            if (i < effectiveAmounts.Count)
                steps.Add(($"{i + 1}회차", $"{effectiveAmounts[i]:F2} USDT", false));
            else
                steps.Add(($"{i + 1}회차", $"{origConfig.GetAmountForStep(i + 1):F2} USDT", true));
        }

        return new BuyPlanDialogArgs(
            Symbol:          _symbol,
            Leverage:        _leverage,
            MarginMode:      _marginMode,
            ConfiguredCount: _martinCount,
            EffectiveCount:  effectiveCount,
            Steps:           steps,
            RequiredTotal:   effectiveAmounts.Sum(),
            Budget:          _totalBudget,
            Warning:         warning,
            IsMockMode:      isMockMode);
    }

    public string ExpectedProfitText =>
        TotalBudget > 0 && Leverage > 0 && TargetProfit > 0
            ? $"${TotalBudget * Leverage * TargetProfit / 100m:N2}" : "-";

    public string ExpectedFeeText =>
        TotalBudget > 0 && MartinCount > 0 && Leverage > 0
            ? $"${TotalBudget * (Leverage ?? 1) * _takerFeeRate * 2:N2}" : "-"; // 진입+청산 Taker × 레버리지

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
            this.RaisePropertyChanged(nameof(IsNotRunning));
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(StatusBrush));
            this.RaisePropertyChanged(nameof(TabHeader));
        }
    }

    public bool IsNotRunning => !_isRunning;

    public IBrush StatusBrush => IsRunning ? Brushes.LightGreen : Brushes.Gray;
    public string StatusText  => IsRunning ? "봇 실행 중" : "대기 중";

    public decimal CurrentPrice
    {
        get => _currentPrice;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentPrice, value);
            this.RaisePropertyChanged(nameof(CurrentPriceText));
            this.RaisePropertyChanged(nameof(BudgetWarningText));
            this.RaisePropertyChanged(nameof(HasBudgetWarning));
            this.RaisePropertyChanged(nameof(RequiredTotalText));
            this.RaisePropertyChanged(nameof(RequiredTotalColor));
        }
    }

    public string CurrentPriceText => CurrentPrice > 0 ? $"${Px(CurrentPrice)}" : "--";

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

    public string AvgEntryPriceText => AvgEntryPrice > 0 ? Px(AvgEntryPrice) : "-";

    public decimal NextMartinPrice
    {
        get => _nextMartinPrice;
        private set
        {
            this.RaiseAndSetIfChanged(ref _nextMartinPrice, value);
            this.RaisePropertyChanged(nameof(NextMartinPriceText));
        }
    }

    public string NextMartinPriceText => NextMartinPrice > 0 ? Px(NextMartinPrice) : "-";

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

        // 1단계: 매수 계획 확인
        if (ShowBuyPlan != null)
        {
            var args = BuildBuyPlanArgs(global.IsBacktestMode);
            if (!await ShowBuyPlan(args)) return;
        }

        // 2단계: 모의거래 추가 확인
        if (global.IsBacktestMode && ConfirmMockStart != null)
        {
            var proceed = await ConfirmMockStart();
            if (!proceed) return;
        }

        var config = BuildConfig(global);

        // ── 예산 / 최소주문금액 기준 회차 조정 ──
        var (effectiveCount, effectiveBudget, adjustWarning) = ComputeBudgetAdjustment();
        if (!string.IsNullOrEmpty(adjustWarning))
        {
            AddLog($"⚠ {adjustWarning}");
            config.MartinCount = effectiveCount;
            config.TotalBudget = effectiveBudget;
            if (config.MartinAmountWeights.Count > effectiveCount)
                config.MartinAmountWeights = config.MartinAmountWeights.Take(effectiveCount).ToList();
            if (config.MartinGapSteps.Count > effectiveCount)
                config.MartinGapSteps = config.MartinGapSteps.Take(effectiveCount).ToList();
            if (config.TargetProfitSteps.Count > effectiveCount)
                config.TargetProfitSteps = config.TargetProfitSteps.Take(effectiveCount).ToList();
        }

        // ── 알림 설정 구성 (필드 재사용 — 설정 변경 시 in-place 갱신됨) ──
        ApplyNotifyConfig(global);

        // ── 데이터 프로바이더 (실시간 — 가상매매/실거래 공통) ──
        var rest      = new OkxRestClient(config.ApiKey, config.ApiSecret, config.Passphrase,
                            NullLogger<OkxRestClient>.Instance);
        var ws        = new OkxWebSocketClient(NullLogger<OkxWebSocketClient>.Instance);
        _dataProvider = new OkxDataProvider(ws, rest, config.Symbol);

        // ── 주문 실행기: 가상매매 vs 실거래 (이것만 교체하면 끝) ──
        IOrderExecutor executor;
        if (global.IsBacktestMode)
        {
            executor = new VirtualOrderExecutor(_dataProvider, config.TotalBudget, _accountBalance);
            AddLog($"[가상매매] 가상 잔고 {config.TotalBudget:N2} USDT | 계좌잔고 {_accountBalance:N2} USDT ({config.MarginModeStr} 마진 시뮬레이션)");
        }
        else
        {
            // 실거래: Private WS 포함 OkxOrderExecutor
            var privWs = new OkxPrivateWebSocketClient(
                config.ApiKey, config.ApiSecret, config.Passphrase,
                NullLogger<OkxPrivateWebSocketClient>.Instance);
            var realExecutor = new OkxOrderExecutor(rest, privWs,
                NullLogger<OkxOrderExecutor>.Instance);
            realExecutor.SetSymbolForPrivateStream(config.Symbol);
            executor = realExecutor;
            AddLog($"[실거래] OKX 실주문 + Pre-orders 모드 (서버 트리거)");
        }

        // ── 텔레그램 알림기 (_notifyConfig 참조 공유 — 설정 변경 시 실시간 반영) ──
        var notifier = new TelegramNotifier(config.TelegramBotToken, config.TelegramChatId, _notifyConfig);

        // ── GPT 분석기 (UseGpt 체크 + API Key 모두 있어야 활성화) ──
        OKXTradingBot.Core.Interfaces.IGptAnalyzer? gptAnalyzer = null;
        if (global.UseGpt)
        {
            if (!string.IsNullOrEmpty(global.GptApiKey))
            {
                gptAnalyzer = new GptAnalyzer(global.GptApiKey, global.GptModel,
                                  NullLogger<GptAnalyzer>.Instance);
                AddLog($"[GPT] 활성화 — 모델: {global.GptModel} | 신뢰도 임계값: {global.GptConfidenceThreshold}%");
            }
            else
            {
                AddLog("[GPT] 사용 설정됨 but API Key 없음 → 가격 방향 감지 모드로 전환");
            }
        }
        else
        {
            AddLog("[GPT 미사용] 가격 방향 감지 모드 — 봇 시작가 기준 다음 캔들 방향으로 진입");
        }

        // ── 로그 파일 서비스 ──
        _logService = new LogFileService(_symbol, _martinCount);
        _logService.WriteSeparator(global.IsBacktestMode ? "가상매매 시작" : "실거래 시작");

        // ── TradingCore 생성 ──
        _core = new TradingCore(
            _dataProvider, executor, config,
            NullLogger<TradingCore>.Instance,
            notifier, _notifyConfig,
            gptAnalyzer,
            msg => _logService?.Write(msg));  // 로그 파일 sink

        _core.OnPositionUpdated += HandlePositionUpdated;
        _core.OnLogMessage      += HandleLogMessage;
        _core.OnTradeClosed     += HandleTradeClosed;

        // 수수료율 조회 (UI 예상수수료 표시 갱신)
        var (taker, maker) = await executor.GetFeeRatesAsync(_symbol);
        _takerFeeRate = taker;
        this.RaisePropertyChanged(nameof(ExpectedFeeText));
        if (!global.IsBacktestMode)
            AddLog($"[수수료] Maker {maker * 100:F4}% / Taker {taker * 100:F4}%");

        _cts      = new CancellationTokenSource();
        IsRunning = true;
        AddLog(global.IsBacktestMode ? "봇 시작 중... [가상매매]" : "봇 시작 중... [실거래]");

        StartPricePolling();
        await _core.StartAsync(AutoRepeat, _cts.Token);
    }

    private static readonly HttpClient _httpClient = new();

    private async Task FetchExchangeRateAsync()
    {
        try
        {
            var json = await _httpClient.GetStringAsync("https://api.frankfurter.app/latest?from=USD&to=KRW");
            var doc  = System.Text.Json.JsonDocument.Parse(json);
            var rate = doc.RootElement.GetProperty("rates").GetProperty("KRW").GetDecimal();
            UsdKrwRate = rate;
        }
        catch { /* 조회 실패 시 무시 */ }
    }

    private async Task FetchBalanceAsync()
    {
        var global = _getGlobalConfig();
        if (string.IsNullOrEmpty(global.ApiKey)) return;

        try
        {
            var rest = new OkxRestClient(global.ApiKey, global.ApiSecret, global.Passphrase,
                           NullLogger<OkxRestClient>.Instance);
            var balance = await rest.GetBalanceAsync();
            AccountBalance = balance;
            AddLog($"[잔고 조회] {balance:N2} USDT");
        }
        catch (Exception ex)
        {
            AddLog($"[잔고 조회 실패] {ex.Message}");
        }
    }

    private async Task StopBotAsync()
    {
        if (ConfirmStop != null && !await ConfirmStop()) return;

        StopPricePolling();
        if (_core != null) await _core.StopAsync();
        _cts?.Cancel();
        IsRunning = false;
        _logService?.WriteSeparator("봇 중지");
    }

    private async Task ClosePositionAsync()
    {
        if (ConfirmForceClose != null && !await ConfirmForceClose()) return;

        AddLog("[제어] 포지션 강제 종료 요청");
        if (_core != null)
            await _core.ForceCloseAsync();
        StopPricePolling();
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RecentCandles = _candleBuffer.ToList();
                if (!_isRunning && _candleBuffer.Count > 0 && _candleBuffer[^1].Close > 0)
                    CurrentPrice = _candleBuffer[^1].Close;
            });
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
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RecentCandles = snapshot;
            if (!_isRunning && live.Close > 0)
                CurrentPrice = live.Close;
        });
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
                NextMartinPrice  = pos.GetNextMartinTriggerPrice(GetMartinGapForStep(pos.MartinStep + 1));
                UnrealizedPnlPct = pos.CurrentPnlPercent;
            }
            else if (pos.Status == PositionStatus.Closed)
            {
                // UI 상태 초기화 (TradeRecord는 HandleTradeClosed에서 처리)
                DirectionText    = "-";
                DirectionBrush   = Brushes.Gray;
                MartinStep       = 0;
                TotalAmount      = 0;
                AvgEntryPrice    = 0;
                NextMartinPrice  = 0;
                UnrealizedPnlPct = 0;
                RealizedPnl      = pos.RealizedPnl;
            }
            else if (pos.Status == PositionStatus.None)
            {
                // 자동반복 대기 상태
                PositionStatusText = "다음 사이클 대기 중";
            }
        });
    }

    /// <summary>거래(사이클) 완료 이벤트 핸들러 — TradeRecord 생성 + 통계 + DB 저장</summary>
    private void HandleTradeClosed(object? sender, OKXTradingBot.Core.Models.TradeClosedEventArgs e)
    {
        // DB 저장 (백그라운드, UI 블로킹 방지)
        Task.Run(() =>
        {
            try { _tradeRepo.Save(e); }
            catch { /* 저장 실패는 무시 */ }
        });

        // UI 업데이트
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var record = new TradeRecord
            {
                Number        = ++_tradeSeq,
                Symbol        = e.Symbol,
                Direction     = e.Direction == TradeDirection.Long ? "LONG" : "SHORT",
                AvgEntry      = e.AvgEntryPrice,
                TotalInvested = e.TotalAmount,
                MartinStep    = e.MartinStep,
                MartinMax     = e.MartinMax,
                PnlAmount     = e.PnlAmount,
                PnlPercent    = e.PnlPercent,
                ClosedAt      = e.ClosedAt,
                AmountMode    = _amountMode.ToString()
            };

            TradeHistory.Insert(0, record);
            TotalPnl += e.PnlAmount;
            if (e.PnlAmount > 0) WinCount++;
            else                 LossCount++;
        });
    }

    private void HandleLogMessage(object? sender, string message)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => AddLog(message));

    /// <summary>앱 시작 시 DB에서 이전 거래 기록 로드 (해당 심볼만)</summary>
    private void LoadTradeHistoryFromDb()
    {
        try
        {
            var records = _tradeRepo.LoadAll(_symbol);
            if (records.Count == 0) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TradeHistory.Clear();
                _tradeSeq = 0;
                decimal totalPnl = 0;
                int wins = 0, losses = 0;

                foreach (var e in records) // 이미 최신순
                {
                    var record = new TradeRecord
                    {
                        Number        = ++_tradeSeq,
                        Symbol        = e.Symbol,
                        Direction     = e.Direction == TradeDirection.Long ? "LONG" : "SHORT",
                        AvgEntry      = e.AvgEntryPrice,
                        TotalInvested = e.TotalAmount,
                        MartinStep    = e.MartinStep,
                        MartinMax     = e.MartinMax,
                        PnlAmount     = e.PnlAmount,
                        PnlPercent    = e.PnlPercent,
                        ClosedAt      = e.ClosedAt
                    };
                    TradeHistory.Add(record);
                    totalPnl += e.PnlAmount;
                    if (e.PnlAmount > 0) wins++;
                    else                 losses++;
                }

                TotalPnl  = totalPnl;
                WinCount  = wins;
                LossCount = losses;

                AddLog($"[DB] 이전 거래 기록 {records.Count}건 복원 | 누적 손익: {totalPnl:+0.00;-0.00} USDT");
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                AddLog($"[DB] 거래 기록 로드 실패: {ex.Message}"));
        }
    }

    public void AddLog(string message)
    {
        Logs.Insert(0, message);
        while (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1);
    }

    private static string Px(decimal price)
    {
        if (price <= 0) return "0";
        var digits = Math.Max(2, (int)Math.Ceiling(-Math.Log10((double)price)) + 3);
        return price.ToString($"N{Math.Min(digits, 8)}");
    }

    /// <summary>UI 거래 기록 및 통계 초기화 (DB 삭제는 호출자가 처리)</summary>
    public void ClearHistory()
    {
        TradeHistory.Clear();
        Logs.Clear();
        _tradeSeq = 0;
        TotalPnl  = 0;
        WinCount  = 0;
        LossCount = 0;
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
        TargetProfitSteps  = new List<decimal>(_targetProfitSteps),
        MartinAmountWeights  = new List<decimal>(_martinAmountWeights),
        AmountMode         = _amountMode.ToString(),
        StopLossEnabled    = _stopLossEnabled,
        StopLossPercent    = _stopLossPercent,
    };

    public void ApplySettings(SymbolTabSettings s)
    {
        _isLoading         = true;
        _symbol            = s.Symbol;
        _totalBudget       = s.TotalBudget;
        _leverage          = s.Leverage;
        _marginMode        = s.MarginMode;
        _martinCount       = s.MartinCount;
        _martinGap         = s.MartinGap;
        _targetProfit      = s.TargetProfit;
        _martinGapSteps    = new List<decimal>(s.MartinGapSteps);
        _targetProfitSteps = new List<decimal>(s.TargetProfitSteps);
        _martinAmountWeights = new List<decimal>(s.MartinAmountWeights);
        _amountMode        = Enum.TryParse<MartinAmountMode>(s.AmountMode, out var am) ? am : MartinAmountMode.Equal;
        _stopLossEnabled   = s.StopLossEnabled;
        _stopLossPercent   = s.StopLossPercent;
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
        this.RaisePropertyChanged(nameof(AmountMode));
        this.RaisePropertyChanged(nameof(MartinAmountWeights));
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
     || !_martinAmountWeights.SequenceEqual(_savedSnapshot.MartinAmountWeights)
     || _amountMode.ToString() != _savedSnapshot.AmountMode
     || _stopLossEnabled != _savedSnapshot.StopLossEnabled
     || _stopLossPercent != _savedSnapshot.StopLossPercent;

    // ═══════════════════════════════════════════════════════════════════
    // NotificationConfig 관리
    // ═══════════════════════════════════════════════════════════════════

    private void ApplyNotifyConfig(GlobalBotConfig g)
    {
        _notifyConfig.Enabled            = g.TelegramEnabled;
        _notifyConfig.NotifyBotStartStop = g.NotifyBotStartStop;
        _notifyConfig.NotifyEntry        = g.NotifyEntry;
        _notifyConfig.NotifyMartin       = g.NotifyMartin;
        _notifyConfig.NotifyTakeProfit   = g.NotifyTakeProfit;
        _notifyConfig.NotifyStopLoss     = g.NotifyStopLoss;
        _notifyConfig.NotifyError        = g.NotifyError;
        _notifyConfig.QuietHoursEnabled  = g.QuietHoursEnabled;
        _notifyConfig.QuietStart         = g.QuietStart;
        _notifyConfig.QuietEnd           = g.QuietEnd;
    }

    /// <summary>봇 실행 중 설정 저장 시 호출 — 재시작 없이 즉시 반영</summary>
    public void UpdateNotifyConfig(GlobalBotConfig g) => ApplyNotifyConfig(g);

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
        GptAnalysisInterval    = global.GptAnalysisInterval,
        Symbol                 = _symbol,
        TotalBudget            = _totalBudget,
        Leverage               = _leverage,
        MarginMode             = Enum.Parse<OKXTradingBot.Core.Models.MarginMode>(_marginMode),
        MartinCount            = _martinCount,
        MartinGap              = _martinGap,
        TargetProfit           = _targetProfit,
        MartinGapSteps         = new List<decimal>(_martinGapSteps),
        TargetProfitSteps      = new List<decimal>(_targetProfitSteps),
        MartinAmountWeights    = new List<decimal>(_martinAmountWeights),
        AmountMode             = _amountMode,
        StopLossEnabled        = _stopLossEnabled,
        StopLossPercent        = _stopLossPercent,
        TelegramBotToken       = global.TelegramBotToken,
        TelegramChatId         = global.TelegramChatId,
    };
}
