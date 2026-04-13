using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReactiveUI;

namespace OKXTradingBot.UI.Views;

/// <summary>단계별 파라미터 설정 다이얼로그 반환값</summary>
public class StepParamsResult
{
    public bool          Confirmed { get; init; }
    public List<decimal> GapSteps  { get; init; } = new();
}

/// <summary>테이블 한 행 (회차별 진입 간격)</summary>
public class StepParamRow : ReactiveObject
{
    public int Step { get; init; }

    private decimal _gap;
    public decimal Gap
    {
        get => _gap;
        set => this.RaiseAndSetIfChanged(ref _gap, value);
    }
}

public partial class StepParamsDialog : Window
{
    private readonly decimal _defaultGap;

    public ObservableCollection<StepParamRow> Rows { get; } = new();

    public StepParamsDialog(
        int           martinCount,
        decimal       defaultGap,
        List<decimal> existingGapSteps)
    {
        _defaultGap = defaultGap;

        for (int i = 1; i <= martinCount; i++)
        {
            Rows.Add(new StepParamRow
            {
                Step = i,
                Gap  = i <= existingGapSteps.Count
                           ? existingGapSteps[i - 1]
                           : defaultGap,
            });
        }

        InitializeComponent();
        DataContext = this;
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        foreach (var row in Rows)
            row.Gap = _defaultGap;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var gapSteps = Rows.Select(r => r.Gap).ToList();

        // 모든 값이 기본값과 동일하면 균등 모드로 복귀 (빈 리스트 반환)
        var allEqual = gapSteps.All(g => g == _defaultGap);

        Close(new StepParamsResult
        {
            Confirmed = true,
            GapSteps  = allEqual ? new List<decimal>() : gapSteps,
        });
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
