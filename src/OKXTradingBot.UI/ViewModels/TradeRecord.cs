using Avalonia.Media;

namespace OKXTradingBot.UI.ViewModels;

public class TradeRecord
{
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#00C853"));
    private static readonly IBrush RedBrush   = new SolidColorBrush(Color.Parse("#FF5252"));

    public int      Number        { get; set; }
    public string   Symbol        { get; set; } = "";
    public string   Direction     { get; set; } = "";
    public decimal  AvgEntry      { get; set; }
    public decimal  TotalInvested { get; set; }
    public int      MartinStep    { get; set; }
    public int      MartinMax     { get; set; }
    public decimal  PnlAmount     { get; set; }
    public decimal  PnlPercent    { get; set; }
    public DateTime ClosedAt      { get; set; }

    public bool   IsWin          => PnlAmount > 0;
    public IBrush DirectionBrush => Direction == "LONG" ? GreenBrush : RedBrush;
    public IBrush PnlBrush       => IsWin ? GreenBrush : RedBrush;

    public string AvgEntryText   => AvgEntry >= 1000 ? $"{AvgEntry:N2}" : $"{AvgEntry:F4}";
    public string MartinText     => $"{MartinStep} / {MartinMax}";
    public string PnlAmountText  => $"{PnlAmount:+0.00;-0.00}";
    public string PnlPercentText => $"{PnlPercent:+0.00;-0.00}%";
    public string TimeText       => ClosedAt.ToString("MM/dd HH:mm");
}
