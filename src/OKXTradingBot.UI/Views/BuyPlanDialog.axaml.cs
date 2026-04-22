using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OKXTradingBot.UI.Views;

public class BuyPlanStepRow
{
    public string Label  { get; init; } = "";
    public string Amount { get; init; } = "";
    public string Color  { get; init; } = "#E0E0FF";
    public string Note   { get; init; } = "";
}

public partial class BuyPlanDialog : Window
{
    public BuyPlanDialog(
        string symbol,
        int leverage,
        string marginMode,
        int configuredCount,
        int effectiveCount,
        List<(string Label, string Amount, bool IsOver)> steps,
        decimal requiredTotal,
        decimal budget,
        string warning,
        bool isMockMode)
    {
        InitializeComponent();

        // 기본 정보 그리드
        var infoGrid = this.FindControl<Grid>("InfoGrid")!;
        AddInfoRow(infoGrid, 0, "심볼",     symbol);
        AddInfoRow(infoGrid, 1, "레버리지", $"{leverage}x  ({marginMode})");
        AddInfoRow(infoGrid, 2, "분할",
            configuredCount == effectiveCount
                ? $"{configuredCount}회차"
                : $"{configuredCount}회차 → {effectiveCount}회차 조정");

        // 회차별 목록
        var stepList = this.FindControl<ItemsControl>("StepList")!;
        stepList.ItemsSource = steps.Select(s => new BuyPlanStepRow
        {
            Label  = s.Label,
            Amount = s.Amount,
            Color  = s.IsOver ? "#FF5252" : "#4FC3F7",
            Note   = s.IsOver ? "(예산 초과)" : ""
        }).ToList();

        // 합계
        var totalText = this.FindControl<TextBlock>("TotalText")!;
        totalText.Text       = $"{requiredTotal:F2} USDT";
        totalText.Foreground = requiredTotal > budget
            ? SolidColorBrush.Parse("#FF5252")
            : SolidColorBrush.Parse("#4FC3F7");

        // 경고
        if (!string.IsNullOrEmpty(warning))
        {
            this.FindControl<Border>("WarnBorder")!.IsVisible = true;
            this.FindControl<TextBlock>("WarnText")!.Text     = warning;
        }

        // 확인 버튼
        var btn = this.FindControl<Button>("ConfirmButton")!;
        if (isMockMode)
        {
            btn.Content    = "다음 →";
            btn.Background = SolidColorBrush.Parse("#FF9800");
        }
        else
        {
            btn.Content    = "실거래 시작";
            btn.Background = SolidColorBrush.Parse("#2979FF");
        }
        btn.Foreground = Brushes.White;
    }

    private static void AddInfoRow(Grid grid, int row, string label, string value)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var lbl = new TextBlock
        {
            Text       = label,
            FontSize   = 11,
            Foreground = SolidColorBrush.Parse("#888899"),
            Margin     = new Avalonia.Thickness(0, 0, 16, 3),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetRow(lbl, row);
        Grid.SetColumn(lbl, 0);

        var val = new TextBlock
        {
            Text       = value,
            FontSize   = 12,
            FontWeight = FontWeight.Bold,
            Foreground = SolidColorBrush.Parse("#E0E0FF"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin     = new Avalonia.Thickness(0, 0, 0, 3)
        };
        Grid.SetRow(val, row);
        Grid.SetColumn(val, 1);

        grid.Children.Add(lbl);
        grid.Children.Add(val);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e)  => Close(false);
}
