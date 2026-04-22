using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OKXTradingBot.Core.Models;
using ReactiveUI;

namespace OKXTradingBot.UI.Views;

/// <summary>단계별 파라미터 설정 다이얼로그 반환값</summary>
public class StepParamsResult
{
    public bool              Confirmed     { get; init; }
    public List<decimal>     GapSteps      { get; init; } = new();
    public List<decimal>     AmountWeights { get; init; } = new();
    public MartinAmountMode  AmountMode    { get; init; }
}

/// <summary>테이블 한 행</summary>
public class StepParamRow : ReactiveObject
{
    public int Step { get; init; }

    private decimal _gap;
    public decimal Gap
    {
        get => _gap;
        set => this.RaiseAndSetIfChanged(ref _gap, value);
    }

    private decimal _weight = 1m;
    public decimal Weight
    {
        get => _weight;
        set
        {
            this.RaiseAndSetIfChanged(ref _weight, value);
            OnWeightChanged?.Invoke();
        }
    }

    private string _amountDisplay = "";
    public string AmountDisplay
    {
        get => _amountDisplay;
        set => this.RaiseAndSetIfChanged(ref _amountDisplay, value);
    }

    private string _krwDisplay = "";
    public string KrwDisplay
    {
        get => _krwDisplay;
        set => this.RaiseAndSetIfChanged(ref _krwDisplay, value);
    }

    private bool _showWeight;
    public bool ShowWeight
    {
        get => _showWeight;
        set => this.RaiseAndSetIfChanged(ref _showWeight, value);
    }

    private bool _weightEditable = true;
    public bool WeightEditable
    {
        get => _weightEditable;
        set => this.RaiseAndSetIfChanged(ref _weightEditable, value);
    }

    private bool _gapEditable = true;
    public bool GapEditable
    {
        get => _gapEditable;
        set => this.RaiseAndSetIfChanged(ref _gapEditable, value);
    }

    public Action? OnWeightChanged { get; set; }
}

public partial class StepParamsDialog : Window
{
    private readonly decimal _defaultGap;
    private readonly decimal _totalBudget;
    private readonly int     _martinCount;
    private readonly decimal _usdKrwRate;

    private MartinAmountMode _currentMode;

    public ObservableCollection<StepParamRow> Rows { get; } = new();

    public StepParamsDialog(
        int              martinCount,
        decimal          defaultGap,
        List<decimal>    existingGapSteps,
        decimal          totalBudget,
        MartinAmountMode currentMode,
        List<decimal>    existingWeights,
        decimal          usdKrwRate = 0m)
    {
        _defaultGap  = defaultGap;
        _totalBudget = totalBudget;
        _martinCount = martinCount;
        _usdKrwRate  = usdKrwRate;
        // Equal 모드로 열리면 배수 모드로 기본 진입
        _currentMode = currentMode == MartinAmountMode.Equal ? MartinAmountMode.Multiplier : currentMode;

        var showW = _currentMode != MartinAmountMode.Equal;

        // 배수 모드: 저장된 절대 가중치를 "이전 단계 대비 배수"로 변환해서 표시
        var displayWeights = (currentMode == MartinAmountMode.Multiplier && existingWeights.Count > 0)
            ? TradeConfig.AbsoluteWeightsToMultipliers(existingWeights)
            : existingWeights;

        for (int i = 1; i <= martinCount; i++)
        {
            var row = new StepParamRow
            {
                Step        = i,
                Gap         = i <= existingGapSteps.Count ? existingGapSteps[i - 1] : defaultGap,
                Weight      = i <= displayWeights.Count ? displayWeights[i - 1] : 1m,
                ShowWeight  = showW,
                GapEditable = i != 1,
            };
            row.OnWeightChanged = RecalcAmounts;
            Rows.Add(row);
        }

        // 기존 가중치가 없으면 프리셋 채움
        if (existingWeights.Count == 0)
        {
            var preset = TradeConfig.GeneratePresetWeights(currentMode, martinCount);
            for (int i = 0; i < Rows.Count && i < preset.Count; i++)
                Rows[i].Weight = preset[i];
        }

        InitializeComponent();
        DataContext = this;

        ApplyModeUI(_currentMode);
        RecalcAmounts();
    }

    // ── 모드 전환 ──────────────────────────────────────────

    private void OnModeMultiplier(object? sender, RoutedEventArgs e)
    {
        _currentMode = MartinAmountMode.Multiplier;
        var preset = TradeConfig.GeneratePresetWeights(MartinAmountMode.Multiplier, _martinCount);
        for (int i = 0; i < Rows.Count && i < preset.Count; i++)
            Rows[i].Weight = preset[i];
        ApplyModeUI(_currentMode);
        RecalcAmounts();
    }

    private void OnModeFibonacci(object? sender, RoutedEventArgs e)
    {
        _currentMode = MartinAmountMode.Fibonacci;
        var preset = TradeConfig.GeneratePresetWeights(MartinAmountMode.Fibonacci, _martinCount);
        for (int i = 0; i < Rows.Count && i < preset.Count; i++)
            Rows[i].Weight = preset[i];
        ApplyModeUI(_currentMode);
        RecalcAmounts();
    }

    private void ApplyModeUI(MartinAmountMode mode)
    {
        var accent     = this.FindResource("AccentColor") as IBrush ?? Brushes.DodgerBlue;
        var inactive   = this.FindResource("CardBorder") as IBrush ?? Brushes.Gray;
        var fgActive   = Brushes.White;
        var fgInactive = this.FindResource("TextSecondary") as IBrush ?? Brushes.Gray;

        BtnModeMultiplier.Background   = mode == MartinAmountMode.Multiplier ? accent : inactive;
        BtnModeFibonacci.Background    = mode == MartinAmountMode.Fibonacci ? accent : inactive;

        BtnModeMultiplier.Foreground   = mode == MartinAmountMode.Multiplier ? fgActive : fgInactive;
        BtnModeFibonacci.Foreground    = mode == MartinAmountMode.Fibonacci ? fgActive : fgInactive;

        var showW = mode != MartinAmountMode.Equal;
        foreach (var row in Rows)
        {
            row.ShowWeight    = showW;
            // 배수 모드: 1회차는 기준값이므로 편집 불가
            row.WeightEditable = row.Step != 1;
        }

        // 헤더 표시
        WeightHeader.IsVisible = showW;
        AmountHeader.IsVisible = showW;
        WeightHeader.Text = mode == MartinAmountMode.Multiplier ? "배수 (이전 대비)" : "비율";
    }

    // ── 금액 재계산 ────────────────────────────────────────

    private void RecalcAmounts()
    {
        if (_currentMode == MartinAmountMode.Equal)
        {
            if (TotalAmountLabel != null)
                TotalAmountLabel.Text = $"총 투자금: {_totalBudget:F2} USDT  |  균등 분할: {Math.Round(_totalBudget / _martinCount, 2):F2} USDT × {_martinCount}";
            return;
        }

        var rawWeights = Rows.Select(r => r.Weight).ToList();

        // 배수 모드: "이전 단계 대비 배수" → 절대 가중치로 변환 후 금액 계산
        var absWeights = _currentMode == MartinAmountMode.Multiplier
            ? TradeConfig.MultipliersToAbsoluteWeights(rawWeights)
            : rawWeights;

        var config = new TradeConfig { TotalBudget = _totalBudget, MartinCount = _martinCount };
        var amounts = config.WeightsToAmounts(absWeights);
        var total   = amounts.Sum();

        for (int i = 0; i < Rows.Count && i < amounts.Count; i++)
        {
            Rows[i].AmountDisplay = amounts[i].ToString("F2");
            Rows[i].KrwDisplay    = _usdKrwRate > 0
                ? $"₩{amounts[i] * _usdKrwRate:N0}"
                : "";
        }

        if (TotalAmountLabel != null)
            TotalAmountLabel.Text = $"총 투자금: {_totalBudget:F2} USDT  |  배분 합계: {total:F2} USDT";
    }

    // ── 버튼 이벤트 ────────────────────────────────────────

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        Close(new StepParamsResult
        {
            Confirmed     = true,
            GapSteps      = new List<decimal>(),
            AmountWeights = new List<decimal>(),
            AmountMode    = MartinAmountMode.Equal,
        });
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var gapSteps    = Rows.Select(r => r.Gap).ToList();
        var allGapEqual = gapSteps.All(g => g == _defaultGap);

        List<decimal> weights;
        if (_currentMode == MartinAmountMode.Equal)
            weights = new List<decimal>();
        else if (_currentMode == MartinAmountMode.Multiplier)
            // 배수 모드: 화면의 "이전 단계 대비 배수" → 절대 가중치로 변환해서 저장
            weights = TradeConfig.MultipliersToAbsoluteWeights(Rows.Select(r => r.Weight).ToList());
        else
            weights = Rows.Select(r => r.Weight).ToList();

        Close(new StepParamsResult
        {
            Confirmed     = true,
            GapSteps      = allGapEqual ? new List<decimal>() : gapSteps,
            AmountWeights = weights,
            AmountMode    = _currentMode,
        });
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
