using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.Logging.Abstractions;
using OKXTradingBot.Infrastructure.OKX;
using OKXTradingBot.Infrastructure.Persistence;
using OKXTradingBot.UI.Services;
using ReactiveUI;

namespace OKXTradingBot.UI.ViewModels;

public class GptModelInfo
{
    public string Id          { get; init; } = "";
    public string Badge       { get; init; } = "";
    public string BadgeColor  { get; init; } = "Gray";
    public string Description { get; init; } = "";

    public override string ToString() => Id;
}

public class MainWindowViewModel : ReactiveObject
{
    private readonly AppSettingsService _settingsService = new();
    private AppSettings _savedSnapshot = new();
    private bool _isLoading = false;

    // ── 전역 설정 필드 ────────────────────────────────────────────────
    private bool   _isDarkMode     = true;
    private bool   _isBacktestMode = true; // 기본값: 모의거래
    private string _apiKey         = "";
    private string _apiSecret      = "";
    private string _passphrase     = "";
    private string _gptApiKey      = "";
    private string _gptModel       = "gpt-5.4-mini";
    private int    _gptCandleCount         = 30;
    private int    _gptConfidenceThreshold = 60;
    private bool   _useGpt                 = false;
    private int    _gptAnalysisInterval    = 5;
    private string _telegramBotToken = "";
    private string _telegramChatId   = "";
    private bool   _telegramEnabled  = false;
    private bool   _notifyBotStartStop = true;
    private bool   _notifyEntry        = true;
    private bool   _notifyMartin       = true;
    private bool   _notifyTakeProfit   = true;
    private bool   _notifyStopLoss     = true;
    private bool   _notifyError        = true;
    private bool   _quietHoursEnabled  = false;
    private string _quietStart = "23:00";
    private string _quietEnd   = "07:00";
    private bool   _globalHasChanges   = false;

    // ── 심볼 탭 ──────────────────────────────────────────────────────
    public ObservableCollection<SymbolTabViewModel> SymbolTabs { get; } = new();

    // ── 수익률 탭 필터 ────────────────────────────────────────────────
    private static readonly IBrush _activePeriodBg = new SolidColorBrush(Color.Parse("#2979FF"));
    private static readonly IBrush _activePeriodFg = Brushes.White;

    public ObservableCollection<string> PnlSymbolOptions { get; } = new() { "전체" };

    private string _selectedPnlSymbol = "전체";
    public string SelectedPnlSymbol
    {
        get => _selectedPnlSymbol;
        set { this.RaiseAndSetIfChanged(ref _selectedPnlSymbol, value); RefreshFilteredPnl(); }
    }

    private string _selectedPnlPeriod = "전체";
    public string SelectedPnlPeriod
    {
        get => _selectedPnlPeriod;
        set { this.RaiseAndSetIfChanged(ref _selectedPnlPeriod, value); RefreshFilteredPnl(); NotifyPeriodBrushes(); }
    }

    public ReactiveCommand<string, Unit> SetPnlPeriodCommand { get; private set; } = null!;

    public ObservableCollection<TradeRecord> FilteredTradeHistory { get; } = new();

    private decimal _filteredTotalPnl;
    private int     _filteredWinCount;
    private int     _filteredLossCount;

    public string FilteredTotalPnlText  => $"{_filteredTotalPnl:+0.00;-0.00;0.00} USDT";
    public IBrush FilteredTotalPnlBrush => _filteredTotalPnl > 0 ? Brushes.LightGreen
                                         : _filteredTotalPnl < 0 ? Brushes.Tomato : Brushes.Gray;
    public string FilteredTradeCountText => $"{_filteredWinCount + _filteredLossCount}회";
    public string FilteredWinRateText    => (_filteredWinCount + _filteredLossCount) > 0
        ? $"{(double)_filteredWinCount / (_filteredWinCount + _filteredLossCount) * 100:F1}%" : "-";
    public string FilteredAvgPnlText     => (_filteredWinCount + _filteredLossCount) > 0
        ? $"{_filteredTotalPnl / (_filteredWinCount + _filteredLossCount):+0.00;-0.00} USDT" : "-";

    public IBrush PnlPeriodAllBg    => _selectedPnlPeriod == "전체" ? _activePeriodBg : Brushes.Transparent;
    public IBrush PnlPeriodDailyBg  => _selectedPnlPeriod == "일간" ? _activePeriodBg : Brushes.Transparent;
    public IBrush PnlPeriodWeeklyBg => _selectedPnlPeriod == "주간" ? _activePeriodBg : Brushes.Transparent;
    public IBrush PnlPeriodMonthlyBg=> _selectedPnlPeriod == "월간" ? _activePeriodBg : Brushes.Transparent;
    public IBrush PnlPeriodYearlyBg => _selectedPnlPeriod == "연간" ? _activePeriodBg : Brushes.Transparent;
    public IBrush PnlPeriodAllFg    => _selectedPnlPeriod == "전체" ? _activePeriodFg : Brushes.Gray;
    public IBrush PnlPeriodDailyFg  => _selectedPnlPeriod == "일간" ? _activePeriodFg : Brushes.Gray;
    public IBrush PnlPeriodWeeklyFg => _selectedPnlPeriod == "주간" ? _activePeriodFg : Brushes.Gray;
    public IBrush PnlPeriodMonthlyFg=> _selectedPnlPeriod == "월간" ? _activePeriodFg : Brushes.Gray;
    public IBrush PnlPeriodYearlyFg => _selectedPnlPeriod == "연간" ? _activePeriodFg : Brushes.Gray;

    private void NotifyPeriodBrushes()
    {
        this.RaisePropertyChanged(nameof(PnlPeriodAllBg));
        this.RaisePropertyChanged(nameof(PnlPeriodDailyBg));
        this.RaisePropertyChanged(nameof(PnlPeriodWeeklyBg));
        this.RaisePropertyChanged(nameof(PnlPeriodMonthlyBg));
        this.RaisePropertyChanged(nameof(PnlPeriodYearlyBg));
        this.RaisePropertyChanged(nameof(PnlPeriodAllFg));
        this.RaisePropertyChanged(nameof(PnlPeriodDailyFg));
        this.RaisePropertyChanged(nameof(PnlPeriodWeeklyFg));
        this.RaisePropertyChanged(nameof(PnlPeriodMonthlyFg));
        this.RaisePropertyChanged(nameof(PnlPeriodYearlyFg));
    }

    private void OnAnyTradeHistoryChanged()
    {
        var symbols = SymbolTabs
            .SelectMany(t => t.TradeHistory)
            .Select(r => r.Symbol)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        foreach (var s in symbols.Where(s => !PnlSymbolOptions.Contains(s)))
            PnlSymbolOptions.Add(s);

        if (_selectedPnlSymbol != "전체" && !symbols.Contains(_selectedPnlSymbol))
            SelectedPnlSymbol = "전체";
        else
            RefreshFilteredPnl();
    }

    private void RefreshFilteredPnl()
    {
        var cutoff = _selectedPnlPeriod switch
        {
            "일간" => DateTime.Today,
            "주간" => DateTime.Today.AddDays(-7),
            "월간" => DateTime.Today.AddMonths(-1),
            "연간" => DateTime.Today.AddYears(-1),
            _     => DateTime.MinValue
        };

        var records = SymbolTabs
            .SelectMany(t => t.TradeHistory)
            .Where(r => _selectedPnlSymbol == "전체" || r.Symbol == _selectedPnlSymbol)
            .Where(r => r.ClosedAt >= cutoff)
            .OrderByDescending(r => r.ClosedAt)
            .ToList();

        FilteredTradeHistory.Clear();
        int seq = 1;
        foreach (var r in records) { r.Number = seq++; FilteredTradeHistory.Add(r); }

        _filteredTotalPnl  = records.Sum(r => r.PnlAmount);
        _filteredWinCount  = records.Count(r => r.PnlAmount > 0);
        _filteredLossCount = records.Count(r => r.PnlAmount <= 0);

        this.RaisePropertyChanged(nameof(FilteredTotalPnlText));
        this.RaisePropertyChanged(nameof(FilteredTotalPnlBrush));
        this.RaisePropertyChanged(nameof(FilteredTradeCountText));
        this.RaisePropertyChanged(nameof(FilteredWinRateText));
        this.RaisePropertyChanged(nameof(FilteredAvgPnlText));
    }

    private SymbolTabViewModel? _selectedSymbolTab;
    public SymbolTabViewModel? SelectedSymbolTab
    {
        get => _selectedSymbolTab;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSymbolTab, value);
            this.RaisePropertyChanged(nameof(HeaderSymbol));
            this.RaisePropertyChanged(nameof(HeaderPrice));
            this.RaisePropertyChanged(nameof(IsAnyRunning));
        }
    }

    // 헤더에 선택된 탭 심볼 / 가격 표시
    public string HeaderSymbol => SelectedSymbolTab?.Symbol ?? "---";
    public string HeaderPrice  => SelectedSymbolTab?.CurrentPriceText ?? "--";
    public bool   IsAnyRunning => SymbolTabs.Any(t => t.IsRunning);

    // ── Commands ──────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit>   AddSymbolTabCommand     { get; }
    public ReactiveCommand<SymbolTabViewModel, Unit> RemoveSymbolTabCommand { get; }
    public ReactiveCommand<Unit, Unit>   SaveSettingsCommand      { get; }
    public ReactiveCommand<Unit, Unit>   ToggleThemeCommand       { get; }
    public ReactiveCommand<Unit, Unit>   ToggleTradingModeCommand { get; }
    public ReactiveCommand<Unit, Unit>   RefreshGptModelsCommand  { get; }
    public ReactiveCommand<Unit, Unit>   FetchBalanceCommand      { get; }
    public ReactiveCommand<Unit, Unit>   RefreshSymbolsCommand    { get; }
    public ReactiveCommand<Unit, Unit>   ClearDataCommand         { get; }

    // ── Unsaved ───────────────────────────────────────────────────────
    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasUnsavedChanges, value);
            this.RaisePropertyChanged(nameof(SaveButtonText));
            this.RaisePropertyChanged(nameof(SaveButtonBorderBrush));
        }
    }

    /// <summary>전역 설정(설정 탭)에만 변경사항이 있는지 여부 — 심볼 탭 변경은 포함하지 않음</summary>
    public bool HasGlobalUnsavedChanges => _globalHasChanges;
    public string SaveButtonText     => HasUnsavedChanges ? "  💾  설정 저장  ●" : "  💾  설정 저장  ";
    public IBrush SaveButtonBorderBrush => HasUnsavedChanges
        ? new SolidColorBrush(Color.Parse("#E91E63"))
        : Brushes.Transparent;

    // ── 심볼 목록 (공유) ──────────────────────────────────────────────
    public ObservableCollection<string> SymbolOptions { get; } = new();

    private bool   _isNotFetchingSymbols = true;
    private string _symbolRefreshStatus  = "";
    public bool IsNotFetchingSymbols
    {
        get => _isNotFetchingSymbols;
        private set => this.RaiseAndSetIfChanged(ref _isNotFetchingSymbols, value);
    }
    public string SymbolRefreshStatus
    {
        get => _symbolRefreshStatus;
        private set => this.RaiseAndSetIfChanged(ref _symbolRefreshStatus, value);
    }
    public IBrush SymbolRefreshStatusBrush => _symbolRefreshStatus.StartsWith("✅")
        ? new SolidColorBrush(Color.Parse("#4CAF50"))
        : new SolidColorBrush(Color.Parse("#FF5252"));

    // ── GPT 모델 ──────────────────────────────────────────────────────
    private static readonly GptModelInfo[] _defaultGptModels =
    {
        new() { Id = "gpt-5.4",      Badge = "최신",   BadgeColor = "#E91E63", Description = "GPT-5.4 · 1M+ 토큰 · 최고 성능" },
        new() { Id = "gpt-5.4-mini", Badge = "추천",   BadgeColor = "#4CAF50", Description = "GPT-5.4급 성능 · 고속 · 비용 효율" },
        new() { Id = "gpt-5.4-nano", Badge = "절약",   BadgeColor = "#FF9800", Description = "초저비용 · 단순 반복 분석용" },
        new() { Id = "gpt-5",        Badge = "",       BadgeColor = "Gray",   Description = "GPT-5 · 400K 토큰 · 안정적" },
        new() { Id = "gpt-5-mini",   Badge = "저비용", BadgeColor = "#2196F3", Description = "GPT-4o급 품질 · 5배 저렴" },
        new() { Id = "gpt-5-nano",   Badge = "절약",   BadgeColor = "#FF9800", Description = "분류·추출 등 단순 작업 초저가" },
        new() { Id = "o4-mini",      Badge = "추론",   BadgeColor = "#9C27B0", Description = "비용효율 추론 · 수학/전략 분석 특화" },
        new() { Id = "o3",           Badge = "추론",   BadgeColor = "#9C27B0", Description = "다단계 추론 특화 · Chain-of-Thought" },
        new() { Id = "o3-pro",       Badge = "추론",   BadgeColor = "#9C27B0", Description = "추론 정확도 극대화 · 프리미엄" },
        new() { Id = "gpt-4.1",      Badge = "레거시", BadgeColor = "Gray",   Description = "1M 토큰 · GPT-5로 대체 추세" },
        new() { Id = "gpt-4.1-mini", Badge = "레거시", BadgeColor = "Gray",   Description = "경량 · GPT-5-mini로 대체 추세" },
    };

    private ObservableCollection<GptModelInfo> _gptModelOptions = new(_defaultGptModels);
    public  ObservableCollection<GptModelInfo> GptModelOptions => _gptModelOptions;

    private GptModelInfo? _selectedGptModelInfo;
    public GptModelInfo? SelectedGptModelInfo
    {
        get => _selectedGptModelInfo;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedGptModelInfo, value);
            if (value != null) GptModel = value.Id;
        }
    }

    private bool   _isNotFetchingModels = true;
    private string _okxApiStatus        = "";
    private string _gptRefreshStatus    = "";

    public bool IsNotFetchingModels
    {
        get => _isNotFetchingModels;
        private set => this.RaiseAndSetIfChanged(ref _isNotFetchingModels, value);
    }
    public string OkxApiStatus
    {
        get => _okxApiStatus;
        private set => this.RaiseAndSetIfChanged(ref _okxApiStatus, value);
    }
    public IBrush OkxApiStatusBrush => _okxApiStatus.StartsWith("✅")
        ? new SolidColorBrush(Color.Parse("#4CAF50"))
        : _okxApiStatus.StartsWith("⏳")
        ? new SolidColorBrush(Color.Parse("#FF9800"))
        : new SolidColorBrush(Color.Parse("#FF5252"));
    public string GptRefreshStatus
    {
        get => _gptRefreshStatus;
        private set => this.RaiseAndSetIfChanged(ref _gptRefreshStatus, value);
    }
    public IBrush GptRefreshStatusBrush => _gptRefreshStatus.StartsWith("✅")
        ? new SolidColorBrush(Color.Parse("#4CAF50"))
        : new SolidColorBrush(Color.Parse("#FF5252"));

    // ── Telegram 테스트 ───────────────────────────────────────────────
    private string _telegramTestStatus = "";
    public string TelegramTestStatus
    {
        get => _telegramTestStatus;
        private set => this.RaiseAndSetIfChanged(ref _telegramTestStatus, value);
    }
    public IBrush TelegramTestStatusBrush => _telegramTestStatus.StartsWith("✅")
        ? new SolidColorBrush(Color.Parse("#4CAF50"))
        : _telegramTestStatus.StartsWith("⏳")
        ? new SolidColorBrush(Color.Parse("#FF9800"))
        : new SolidColorBrush(Color.Parse("#FF5252"));

    // ── 잔고 ─────────────────────────────────────────────────────────
    private decimal _accountBalance = -1m;
    public decimal AccountBalance
    {
        get => _accountBalance;
        private set
        {
            this.RaiseAndSetIfChanged(ref _accountBalance, value);
            this.RaisePropertyChanged(nameof(AccountBalanceText));
        }
    }
    public string AccountBalanceText => _accountBalance >= 0 ? $"잔고: {_accountBalance:N2} USDT" : "잔고: -";

    // ── 탭 추가 가능 여부 ─────────────────────────────────────────────
    public bool CanAddTab => SymbolTabs.Count < AppConstants.MaxSymbolTabs;
    public string AddTabTooltip => CanAddTab
        ? "종목 추가"
        : $"최대 {AppConstants.MaxSymbolTabs}개 탭까지 추가 가능합니다";

    private static readonly HttpClient _http = new();

    // ═══════════════════════════════════════════════════════════════════
    // 생성자
    // ═══════════════════════════════════════════════════════════════════

    public MainWindowViewModel()
    {
        LoadSettings();
        _selectedGptModelInfo = _gptModelOptions.FirstOrDefault(m => m.Id == _gptModel)
                                ?? _gptModelOptions.FirstOrDefault();

        var canStart    = this.WhenAnyValue(x => x.CanAddTab);
        var canAddTab   = this.WhenAnyValue(x => x.CanAddTab);

        AddSymbolTabCommand    = ReactiveCommand.Create(() => AddSymbolTab(), canAddTab);
        RemoveSymbolTabCommand = ReactiveCommand.Create<SymbolTabViewModel>(RemoveSymbolTab);
        SaveSettingsCommand    = ReactiveCommand.CreateFromTask(SaveSettingsAsync);
        ToggleThemeCommand     = ReactiveCommand.Create(ToggleTheme);
        ToggleTradingModeCommand = ReactiveCommand.Create(ToggleTradingMode);
        RefreshGptModelsCommand  = ReactiveCommand.CreateFromTask(RefreshGptModelsAsync);
        RefreshSymbolsCommand    = ReactiveCommand.CreateFromTask(RefreshSymbolsAsync);
        FetchBalanceCommand      = ReactiveCommand.CreateFromTask(FetchBalanceAsync);
        SetPnlPeriodCommand      = ReactiveCommand.Create<string>(p => SelectedPnlPeriod = p);
        ClearDataCommand         = ReactiveCommand.Create(ClearData);

        SaveSettingsCommand.ThrownExceptions.Subscribe(ex     => { });
        RefreshGptModelsCommand.ThrownExceptions.Subscribe(ex => { });
        RefreshSymbolsCommand.ThrownExceptions.Subscribe(ex   => { });
        FetchBalanceCommand.ThrownExceptions.Subscribe(ex     => { });

        _ = RefreshSymbolsAsync();
        _ = FetchBalanceAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 심볼 탭 관리
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>이 탭을 제외한 다른 탭이 이미 해당 심볼을 사용하는지 확인</summary>
    private bool IsSymbolInUseByOtherTab(string symbol, SymbolTabViewModel? excludeTab = null)
        => SymbolTabs.Any(t => t != excludeTab && t.Symbol == symbol);

    /// <summary>아직 사용되지 않은 첫 번째 심볼 반환 (없으면 BTC)</summary>
    private string PickUnusedSymbol()
    {
        var usedSymbols = SymbolTabs.Select(t => t.Symbol).ToHashSet();
        return SymbolOptions.FirstOrDefault(s => !usedSymbols.Contains(s))
               ?? SymbolOptions.FirstOrDefault()
               ?? "";
    }

    private void AddSymbolTab(SymbolTabSettings? settings = null)
    {
        if (SymbolTabs.Count >= AppConstants.MaxSymbolTabs) return;

        // 새 탭 추가 시 사용하지 않는 심볼을 기본값으로
        if (settings == null)
        {
            var unusedSymbol = PickUnusedSymbol();
            settings = new SymbolTabSettings { Symbol = unusedSymbol };
        }

        SymbolTabViewModel? tab = null;
        tab = new SymbolTabViewModel(
            symbolOptions: SymbolOptions,
            onChanged: OnTabChanged,
            getGlobalConfig: BuildGlobalConfig,
            isSymbolInUse: sym => IsSymbolInUseByOtherTab(sym, tab),
            initialSettings: settings);

        // 선택된 탭의 심볼/가격 변화를 헤더에 반영
        tab.PropertyChanged += (_, e) =>
        {
            if (tab == SelectedSymbolTab && e.PropertyName is nameof(SymbolTabViewModel.Symbol)
                                                           or nameof(SymbolTabViewModel.CurrentPriceText))
            {
                this.RaisePropertyChanged(nameof(HeaderSymbol));
                this.RaisePropertyChanged(nameof(HeaderPrice));
            }
            if (e.PropertyName == nameof(SymbolTabViewModel.IsRunning))
            {
                this.RaisePropertyChanged(nameof(IsAnyRunning));
                if (tab.IsRunning && CanAddTab)
                    AddSymbolTab();
            }
        };

        // 수익률 탭: TradeHistory 변경 감지
        tab.TradeHistory.CollectionChanged += (_, _) => OnAnyTradeHistoryChanged();

        SymbolTabs.Add(tab);
        SelectedSymbolTab = tab;
        this.RaisePropertyChanged(nameof(CanAddTab));
        this.RaisePropertyChanged(nameof(AddTabTooltip));
    }

    private void RemoveSymbolTab(SymbolTabViewModel tab)
    {
        if (tab.IsRunning)
        {
            _ = tab.StopCommand.Execute();
        }

        var idx = SymbolTabs.IndexOf(tab);
        SymbolTabs.Remove(tab);

        if (SymbolTabs.Count == 0)
        {
            // 탭이 하나도 없으면 기본 탭 자동 추가
            AddSymbolTab();
        }
        else
        {
            var newIdx    = Math.Clamp(idx, 0, SymbolTabs.Count - 1);
            SelectedSymbolTab = SymbolTabs[newIdx];
        }

        this.RaisePropertyChanged(nameof(CanAddTab));
        this.RaisePropertyChanged(nameof(AddTabTooltip));
        OnTabChanged();
    }

    private void OnTabChanged()
    {
        this.RaisePropertyChanged(nameof(HasGlobalUnsavedChanges));
        var changed = _globalHasChanges || SymbolTabs.Any(t => t.HasUnsavedChanges);
        if (HasUnsavedChanges == changed) return;
        HasUnsavedChanges = changed;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 전역 설정 프로퍼티
    // ═══════════════════════════════════════════════════════════════════

    public string ApiKey
    {
        get => _apiKey;
        set { this.RaiseAndSetIfChanged(ref _apiKey, value); MarkUnsaved(); }
    }
    public string ApiSecret
    {
        get => _apiSecret;
        set { this.RaiseAndSetIfChanged(ref _apiSecret, value); MarkUnsaved(); }
    }
    public string Passphrase
    {
        get => _passphrase;
        set { this.RaiseAndSetIfChanged(ref _passphrase, value); MarkUnsaved(); }
    }
    public string GptApiKey
    {
        get => _gptApiKey;
        set { this.RaiseAndSetIfChanged(ref _gptApiKey, value); MarkUnsaved(); }
    }
    public string GptModel
    {
        get => _gptModel;
        set
        {
            this.RaiseAndSetIfChanged(ref _gptModel, value);
            var matched = _gptModelOptions.FirstOrDefault(m => m.Id == value);
            if (matched != null && _selectedGptModelInfo != matched)
                SelectedGptModelInfo = matched;
            MarkUnsaved();
        }
    }
    public int GptCandleCount
    {
        get => _gptCandleCount;
        set { this.RaiseAndSetIfChanged(ref _gptCandleCount, value); MarkUnsaved(); }
    }
    public int GptConfidenceThreshold
    {
        get => _gptConfidenceThreshold;
        set { this.RaiseAndSetIfChanged(ref _gptConfidenceThreshold, value); MarkUnsaved(); }
    }

    public bool UseGpt
    {
        get => _useGpt;
        set
        {
            this.RaiseAndSetIfChanged(ref _useGpt, value);
            this.RaisePropertyChanged(nameof(GptSettingsEnabled));
            MarkUnsaved();
        }
    }

    /// <summary>GPT 미사용 시 세부 설정 비활성화 (UI 바인딩용)</summary>
    public bool GptSettingsEnabled => _useGpt;

    public int GptAnalysisInterval
    {
        get => _gptAnalysisInterval;
        set { this.RaiseAndSetIfChanged(ref _gptAnalysisInterval, value); MarkUnsaved(); }
    }

    public string TelegramBotToken
    {
        get => _telegramBotToken;
        set { this.RaiseAndSetIfChanged(ref _telegramBotToken, value); MarkUnsaved(); }
    }
    public string TelegramChatId
    {
        get => _telegramChatId;
        set { this.RaiseAndSetIfChanged(ref _telegramChatId, value); MarkUnsaved(); }
    }
    public bool TelegramEnabled
    {
        get => _telegramEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _telegramEnabled, value);
            MarkUnsaved();
            if (value) _ = TestTelegramConnectionAsync();
            else TelegramTestStatus = "";
        }
    }
    public bool NotifyBotStartStop
    {
        get => _notifyBotStartStop;
        set { this.RaiseAndSetIfChanged(ref _notifyBotStartStop, value); MarkUnsaved(); }
    }
    public bool NotifyEntry
    {
        get => _notifyEntry;
        set { this.RaiseAndSetIfChanged(ref _notifyEntry, value); MarkUnsaved(); }
    }
    public bool NotifyMartin
    {
        get => _notifyMartin;
        set { this.RaiseAndSetIfChanged(ref _notifyMartin, value); MarkUnsaved(); }
    }
    public bool NotifyTakeProfit
    {
        get => _notifyTakeProfit;
        set { this.RaiseAndSetIfChanged(ref _notifyTakeProfit, value); MarkUnsaved(); }
    }
    public bool NotifyStopLoss
    {
        get => _notifyStopLoss;
        set { this.RaiseAndSetIfChanged(ref _notifyStopLoss, value); MarkUnsaved(); }
    }
    public bool NotifyError
    {
        get => _notifyError;
        set { this.RaiseAndSetIfChanged(ref _notifyError, value); MarkUnsaved(); }
    }
    public bool QuietHoursEnabled
    {
        get => _quietHoursEnabled;
        set { this.RaiseAndSetIfChanged(ref _quietHoursEnabled, value); MarkUnsaved(); }
    }
    public string QuietStart
    {
        get => _quietStart;
        set { this.RaiseAndSetIfChanged(ref _quietStart, value); MarkUnsaved(); }
    }
    public string QuietEnd
    {
        get => _quietEnd;
        set { this.RaiseAndSetIfChanged(ref _quietEnd, value); MarkUnsaved(); }
    }

    // ── 테마 / 모드 ───────────────────────────────────────────────────

    public bool IsBacktestMode
    {
        get => _isBacktestMode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBacktestMode, value);
            this.RaisePropertyChanged(nameof(TradingModeText));
            this.RaisePropertyChanged(nameof(TradingModeBrush));
        }
    }
    public string TradingModeText  => _isBacktestMode ? "🧪 모의거래" : "💹 실거래";
    public IBrush TradingModeBrush => _isBacktestMode
        ? new SolidColorBrush(Color.Parse("#FF9800"))
        : new SolidColorBrush(Color.Parse("#4CAF50"));
    public string ThemeButtonText  => _isDarkMode ? "☀ 라이트" : "🌙 다크";

    private void ToggleTheme()
    {
        _isDarkMode = !_isDarkMode;
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = _isDarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        this.RaisePropertyChanged(nameof(ThemeButtonText));
    }

    private void ToggleTradingMode()
    {
        IsBacktestMode = !IsBacktestMode;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 심볼 목록 갱신
    // ═══════════════════════════════════════════════════════════════════

    private CancellationTokenSource? _symbolStatusCts;

    private async Task ShowSymbolRefreshStatusAsync(string message)
    {
        _symbolStatusCts?.Cancel();
        _symbolStatusCts = new CancellationTokenSource();
        var token = _symbolStatusCts.Token;
        SymbolRefreshStatus = message;
        this.RaisePropertyChanged(nameof(SymbolRefreshStatusBrush));
        try
        {
            await Task.Delay(3000, token);
            SymbolRefreshStatus = "";
        }
        catch (TaskCanceledException) { }
    }

    private async Task RefreshSymbolsAsync()
    {
        IsNotFetchingSymbols = false;
        try
        {
            var resp = await _http.GetStringAsync(
                "https://www.okx.com/api/v5/public/instruments?instType=SWAP");
            using var doc  = System.Text.Json.JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.GetProperty("code").GetString() != "0")
            {
                await ShowSymbolRefreshStatusAsync("❌ OKX API 오류");
                return;
            }

            var symbols = root.GetProperty("data")
                .EnumerateArray()
                .Select(e => e.GetProperty("instId").GetString() ?? "")
                .Where(s => s.EndsWith("-USDT-SWAP"))
                .OrderBy(s => s == "BTC-USDT-SWAP" ? "\0" : s)
                .ToList();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SymbolOptions.Clear();
                foreach (var s in symbols) SymbolOptions.Add(s);

                // 심볼 목록 로드 후 각 탭의 Symbol을 재알림
                // → ComboBox가 목록이 비어있을 때 생성되면 선택값을 잃으므로 강제 재바인딩
                foreach (var tab in SymbolTabs)
                    tab.RefreshSymbolBinding();
            });

            await ShowSymbolRefreshStatusAsync($"✅ {symbols.Count}개 심볼 로드");
        }
        catch (Exception ex)
        {
            await ShowSymbolRefreshStatusAsync($"❌ 조회 실패: {ex.Message}");
        }
        finally
        {
            IsNotFetchingSymbols = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // GPT 모델 갱신
    // ═══════════════════════════════════════════════════════════════════

    private static readonly string[] _modelPrefixes = { "gpt-", "o1", "o3", "o4" };

    private CancellationTokenSource? _statusCts;
    private async void ShowRefreshStatus(string message)
    {
        _statusCts?.Cancel();
        _statusCts = new CancellationTokenSource();
        var token  = _statusCts.Token;
        GptRefreshStatus = message;
        this.RaisePropertyChanged(nameof(GptRefreshStatusBrush));
        try { await Task.Delay(3000, token); GptRefreshStatus = ""; }
        catch (TaskCanceledException) { }
    }

    private async Task RefreshGptModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(GptApiKey)) return;
        IsNotFetchingModels = false;
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, "https://api.openai.com/v1/models");
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GptApiKey);
            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var ids = doc.RootElement
                .GetProperty("data")
                .EnumerateArray()
                .Select(e => e.GetProperty("id").GetString() ?? "")
                .Where(id => _modelPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .Where(id => !id.Contains("audio") && !id.Contains("realtime")
                             && !id.Contains("embedding") && !id.Contains("tts")
                             && !id.Contains("whisper") && !id.Contains("dall"))
                .OrderByDescending(id => id)
                .ToList();
            if (ids.Count == 0) { ShowRefreshStatus("❌ 모델 없음"); return; }
            var infos = AssignBadges(ids);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _isLoading = true;
                var prevId = GptModel;
                _gptModelOptions.Clear();
                foreach (var m in infos) _gptModelOptions.Add(m);
                var matched = _gptModelOptions.FirstOrDefault(m => m.Id == prevId) ?? _gptModelOptions.First();
                SelectedGptModelInfo = matched;
                _isLoading = false;
            });
            ShowRefreshStatus($"✅ {ids.Count}개 모델 로드 완료");
        }
        catch (Exception ex) { ShowRefreshStatus($"❌ 조회 실패: {ex.Message}"); }
        finally { IsNotFetchingModels = true; }
    }

    private static List<GptModelInfo> AssignBadges(List<string> ids)
    {
        var topVersion = ids
            .Select(id => System.Text.RegularExpressions.Regex.Match(id, @"gpt-(\d+(?:\.\d+)?)"))
            .Where(m => m.Success)
            .Select(m => double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
            .DefaultIfEmpty(0).Max();

        return ids.Select(id =>
        {
            var lower = id.ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"^o\d"))
            {
                var badge = lower.Contains("pro") ? "추론" : lower.Contains("mini") ? "추론" : "추론";
                var desc  = lower.Contains("pro") ? "최고 정확도 추론 · 프리미엄"
                          : lower.Contains("mini") ? "비용효율 추론 · 전략 분석 특화"
                          : "다단계 추론 특화 · Chain-of-Thought";
                return new GptModelInfo { Id = id, Badge = badge, BadgeColor = "#9C27B0", Description = desc };
            }
            var verMatch = System.Text.RegularExpressions.Regex.Match(lower, @"gpt-(\d+(?:\.\d+)?)");
            var ver = verMatch.Success && double.TryParse(verMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var vv) ? vv : 0;
            var isLatest = ver > 0 && ver == topVersion;
            if (lower.Contains("nano"))
                return new GptModelInfo { Id = id, Badge = "절약", BadgeColor = "#FF9800",
                    Description = $"GPT-{ver} · 초저비용 · 단순 분석용" };
            if (lower.Contains("mini"))
                return new GptModelInfo { Id = id,
                    Badge = isLatest ? "추천" : "저비용",
                    BadgeColor = isLatest ? "#4CAF50" : "#2196F3",
                    Description = $"GPT-{ver} mini · 비용 효율 · 고속" };
            if (lower.Contains("pro"))
                return new GptModelInfo { Id = id, Badge = "프리미엄", BadgeColor = "#E91E63",
                    Description = $"GPT-{ver} Pro · 최고 성능" };
            return new GptModelInfo { Id = id,
                Badge = isLatest ? "최신" : "", BadgeColor = isLatest ? "#E91E63" : "Gray",
                Description = isLatest ? $"GPT-{ver} · 최신 플래그십" : $"GPT-{ver} · 플래그십" };
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Telegram 테스트
    // ═══════════════════════════════════════════════════════════════════

    private async Task TestTelegramConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(TelegramBotToken) || string.IsNullOrWhiteSpace(TelegramChatId))
        {
            TelegramTestStatus = "❌ Bot Token 또는 Chat ID가 비어있습니다";
            this.RaisePropertyChanged(nameof(TelegramTestStatusBrush));
            return;
        }
        TelegramTestStatus = "⏳ 연결 테스트 중...";
        this.RaisePropertyChanged(nameof(TelegramTestStatusBrush));
        try
        {
            var url  = $"https://api.telegram.org/bot{TelegramBotToken}/getMe";
            var resp = await _http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("ok").GetBoolean())
            {
                var name = doc.RootElement.GetProperty("result").GetProperty("username").GetString();
                TelegramTestStatus = $"✅ 연결 성공 — @{name}";
            }
            else TelegramTestStatus = "❌ Bot Token 오류";
        }
        catch (Exception ex) { TelegramTestStatus = $"❌ 연결 실패: {ex.Message}"; }
        this.RaisePropertyChanged(nameof(TelegramTestStatusBrush));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 잔고 조회
    // ═══════════════════════════════════════════════════════════════════

    private async Task FetchBalanceAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret) || string.IsNullOrWhiteSpace(Passphrase))
            return;
        try
        {
            var rest = new OkxRestClient(ApiKey, ApiSecret, Passphrase,
                NullLogger<OkxRestClient>.Instance);
            AccountBalance = await rest.GetBalanceAsync();
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 설정 저장
    // ═══════════════════════════════════════════════════════════════════

    private async Task SaveSettingsAsync()
    {
        _isLoading = true;
        var okxTask      = ValidateOkxAsync();
        var gptTask      = ValidateGptAsync();
        var telegramTask = ValidateTelegramAsync();
        await Task.WhenAll(okxTask, gptTask, telegramTask);

        _settingsService.Save(new AppSettings
        {
            ApiKey                 = ApiKey,
            ApiSecret              = ApiSecret,
            Passphrase             = Passphrase,
            GptApiKey              = GptApiKey,
            GptModel               = GptModel,
            GptCandleCount         = GptCandleCount,
            GptConfidenceThreshold = GptConfidenceThreshold,
            UseGpt                 = UseGpt,
            GptAnalysisInterval    = GptAnalysisInterval,
            TelegramBotToken       = TelegramBotToken,
            TelegramChatId         = TelegramChatId,
            TelegramEnabled        = TelegramEnabled,
            NotifyBotStartStop     = NotifyBotStartStop,
            NotifyEntry            = NotifyEntry,
            NotifyMartin           = NotifyMartin,
            NotifyTakeProfit       = NotifyTakeProfit,
            NotifyStopLoss         = NotifyStopLoss,
            NotifyError            = NotifyError,
            QuietHoursEnabled      = QuietHoursEnabled,
            QuietStart             = QuietStart,
            QuietEnd               = QuietEnd,
            Tabs                   = SymbolTabs.Select(t => t.ToSettings()).ToList(),
        });

        // 스냅샷 갱신
        _savedSnapshot    = BuildGlobalSnapshot();
        _globalHasChanges = false;
        foreach (var tab in SymbolTabs) tab.MarkSaved();

        // 실행 중인 탭의 알림 설정 즉시 반영
        var updatedGlobal = BuildGlobalConfig();
        foreach (var tab in SymbolTabs.Where(t => t.IsRunning))
            tab.UpdateNotifyConfig(updatedGlobal);

        _isLoading        = false;
        HasUnsavedChanges = false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // API 검증
    // ═══════════════════════════════════════════════════════════════════

    private async Task<bool> ValidateOkxAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(ApiSecret) || string.IsNullOrWhiteSpace(Passphrase))
        {
            OkxApiStatus = "";
            this.RaisePropertyChanged(nameof(OkxApiStatusBrush));
            return true;
        }
        try
        {
            OkxApiStatus = "⏳ 인증 확인 중...";
            this.RaisePropertyChanged(nameof(OkxApiStatusBrush));
            var rest = new OkxRestClient(ApiKey, ApiSecret, Passphrase,
                NullLogger<OkxRestClient>.Instance);
            await rest.GetBalanceAsync();
            OkxApiStatus = "✅ API 인증 성공";
            this.RaisePropertyChanged(nameof(OkxApiStatusBrush));
            return true;
        }
        catch (Exception ex)
        {
            OkxApiStatus = $"❌ 인증 실패: {ex.Message}";
            this.RaisePropertyChanged(nameof(OkxApiStatusBrush));
            return false;
        }
    }

    private async Task<bool> ValidateGptAsync()
    {
        if (string.IsNullOrWhiteSpace(GptApiKey)) return true;
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, "https://api.openai.com/v1/models");
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GptApiKey);
            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<bool> ValidateTelegramAsync()
    {
        if (!TelegramEnabled || string.IsNullOrWhiteSpace(TelegramBotToken) || string.IsNullOrWhiteSpace(TelegramChatId))
            return !TelegramEnabled;
        try
        {
            var url  = $"https://api.telegram.org/bot{TelegramBotToken}/getMe";
            var resp = await _http.GetAsync(url);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("ok").GetBoolean();
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 설정 로드 / 적용 / 복원
    // ═══════════════════════════════════════════════════════════════════

    private GlobalBotConfig BuildGlobalConfig() => new()
    {
        ApiKey                 = ApiKey,
        ApiSecret              = ApiSecret,
        Passphrase             = Passphrase,
        GptApiKey              = GptApiKey,
        GptModel               = GptModel,
        GptCandleCount         = GptCandleCount,
        GptConfidenceThreshold = GptConfidenceThreshold,
        UseGpt                 = UseGpt,
        GptAnalysisInterval    = GptAnalysisInterval,
        TelegramBotToken       = TelegramBotToken,
        TelegramChatId         = TelegramChatId,
        TelegramEnabled        = TelegramEnabled,
        NotifyBotStartStop     = NotifyBotStartStop,
        NotifyEntry            = NotifyEntry,
        NotifyMartin           = NotifyMartin,
        NotifyTakeProfit       = NotifyTakeProfit,
        NotifyStopLoss         = NotifyStopLoss,
        NotifyError            = NotifyError,
        QuietHoursEnabled      = QuietHoursEnabled,
        QuietStart             = QuietStart,
        QuietEnd               = QuietEnd,
        IsBacktestMode         = IsBacktestMode,
    };

    private void LoadSettings()
    {
        var s = _settingsService.Load();
        _savedSnapshot = s;
        ApplyGlobalSettings(s);

        // 탭 생성
        SymbolTabs.Clear();
        foreach (var tabSettings in s.Tabs)
            AddSymbolTab(tabSettings);

        if (SymbolTabs.Count == 0)
            AddSymbolTab();

        SelectedSymbolTab = SymbolTabs.FirstOrDefault();
        _globalHasChanges = false;
        HasUnsavedChanges = false;
    }

    private void ApplyGlobalSettings(AppSettings s)
    {
        _isLoading             = true;
        _apiKey                = s.ApiKey;
        _apiSecret             = s.ApiSecret;
        _passphrase            = s.Passphrase;
        _gptApiKey             = s.GptApiKey;
        _gptModel              = s.GptModel;
        _gptCandleCount        = s.GptCandleCount;
        _gptConfidenceThreshold = s.GptConfidenceThreshold;
        _useGpt                = s.UseGpt;
        _gptAnalysisInterval   = s.GptAnalysisInterval;
        _telegramBotToken      = s.TelegramBotToken;
        _telegramChatId        = s.TelegramChatId;
        _telegramEnabled       = s.TelegramEnabled;
        _notifyBotStartStop    = s.NotifyBotStartStop;
        _notifyEntry           = s.NotifyEntry;
        _notifyMartin          = s.NotifyMartin;
        _notifyTakeProfit      = s.NotifyTakeProfit;
        _notifyStopLoss        = s.NotifyStopLoss;
        _notifyError           = s.NotifyError;
        _quietHoursEnabled     = s.QuietHoursEnabled;
        _quietStart            = s.QuietStart;
        _quietEnd              = s.QuietEnd;
        _isLoading             = false;

        // 전체 프로퍼티 갱신
        this.RaisePropertyChanged(nameof(ApiKey));
        this.RaisePropertyChanged(nameof(ApiSecret));
        this.RaisePropertyChanged(nameof(Passphrase));
        this.RaisePropertyChanged(nameof(GptApiKey));
        this.RaisePropertyChanged(nameof(GptModel));
        this.RaisePropertyChanged(nameof(GptCandleCount));
        this.RaisePropertyChanged(nameof(GptConfidenceThreshold));
        this.RaisePropertyChanged(nameof(UseGpt));
        this.RaisePropertyChanged(nameof(GptSettingsEnabled));
        this.RaisePropertyChanged(nameof(GptAnalysisInterval));
        this.RaisePropertyChanged(nameof(TelegramBotToken));
        this.RaisePropertyChanged(nameof(TelegramChatId));
        this.RaisePropertyChanged(nameof(TelegramEnabled));
        this.RaisePropertyChanged(nameof(NotifyBotStartStop));
        this.RaisePropertyChanged(nameof(NotifyEntry));
        this.RaisePropertyChanged(nameof(NotifyMartin));
        this.RaisePropertyChanged(nameof(NotifyTakeProfit));
        this.RaisePropertyChanged(nameof(NotifyStopLoss));
        this.RaisePropertyChanged(nameof(NotifyError));
        this.RaisePropertyChanged(nameof(QuietHoursEnabled));
        this.RaisePropertyChanged(nameof(QuietStart));
        this.RaisePropertyChanged(nameof(QuietEnd));
    }

    public void DiscardChanges()
    {
        ApplyGlobalSettings(_savedSnapshot);
        foreach (var tab in SymbolTabs) tab.ApplySettings(tab.ToSettings()); // no-op; snapshot is inside tab
        _globalHasChanges = false;
        HasUnsavedChanges = false;
    }

    private AppSettings BuildGlobalSnapshot() => new()
    {
        ApiKey                 = ApiKey,
        ApiSecret              = ApiSecret,
        Passphrase             = Passphrase,
        GptApiKey              = GptApiKey,
        GptModel               = GptModel,
        GptCandleCount         = GptCandleCount,
        GptConfidenceThreshold = GptConfidenceThreshold,
        UseGpt                 = UseGpt,
        GptAnalysisInterval    = GptAnalysisInterval,
        TelegramBotToken       = TelegramBotToken,
        TelegramChatId         = TelegramChatId,
        TelegramEnabled        = TelegramEnabled,
        NotifyBotStartStop     = NotifyBotStartStop,
        NotifyEntry            = NotifyEntry,
        NotifyMartin           = NotifyMartin,
        NotifyTakeProfit       = NotifyTakeProfit,
        NotifyStopLoss         = NotifyStopLoss,
        NotifyError            = NotifyError,
        QuietHoursEnabled      = QuietHoursEnabled,
        QuietStart             = QuietStart,
        QuietEnd               = QuietEnd,
    };

    private void MarkUnsaved()
    {
        if (_isLoading) return;
        _globalHasChanges = DiffersFromSnapshot();
        OnTabChanged();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 데이터 초기화
    // ═══════════════════════════════════════════════════════════════════

    public void ClearData()
    {
        // DB 전체 삭제
        new TradeHistoryRepository().DeleteAll();

        // 로그 파일 전체 삭제
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".okxtradingbot", "logs");
        if (Directory.Exists(logDir))
        {
            foreach (var f in Directory.GetFiles(logDir, "*.log"))
                try { File.Delete(f); } catch { }
        }

        // 각 탭 UI 초기화
        foreach (var tab in SymbolTabs)
            tab.ClearHistory();

        // 수익률 탭 초기화
        PnlSymbolOptions.Clear();
        PnlSymbolOptions.Add("전체");
        SelectedPnlSymbol = "전체";
        RefreshFilteredPnl();
    }

    private bool DiffersFromSnapshot() =>
        ApiKey                 != _savedSnapshot.ApiKey
     || ApiSecret              != _savedSnapshot.ApiSecret
     || Passphrase             != _savedSnapshot.Passphrase
     || GptApiKey              != _savedSnapshot.GptApiKey
     || GptModel               != _savedSnapshot.GptModel
     || GptCandleCount         != _savedSnapshot.GptCandleCount
     || GptConfidenceThreshold != _savedSnapshot.GptConfidenceThreshold
     || UseGpt                 != _savedSnapshot.UseGpt
     || GptAnalysisInterval    != _savedSnapshot.GptAnalysisInterval
     || TelegramBotToken       != _savedSnapshot.TelegramBotToken
     || TelegramChatId         != _savedSnapshot.TelegramChatId
     || TelegramEnabled        != _savedSnapshot.TelegramEnabled
     || NotifyBotStartStop     != _savedSnapshot.NotifyBotStartStop
     || NotifyEntry            != _savedSnapshot.NotifyEntry
     || NotifyMartin           != _savedSnapshot.NotifyMartin
     || NotifyTakeProfit       != _savedSnapshot.NotifyTakeProfit
     || NotifyStopLoss         != _savedSnapshot.NotifyStopLoss
     || NotifyError            != _savedSnapshot.NotifyError
     || QuietHoursEnabled      != _savedSnapshot.QuietHoursEnabled
     || QuietStart             != _savedSnapshot.QuietStart
     || QuietEnd               != _savedSnapshot.QuietEnd;
}
