namespace OKXTradingBot.Core.Models;

/// <summary>
/// 거래(사이클) 완료 시 발행되는 이벤트 데이터
/// TradingCore → 외부(UI, 기록 등)
/// </summary>
public class TradeClosedEventArgs
{
    public string         Symbol        { get; set; } = "";
    public TradeDirection Direction     { get; set; }
    public decimal        AvgEntryPrice { get; set; }
    public decimal        ExitPrice     { get; set; }
    public decimal        TotalAmount   { get; set; }
    public int            MartinStep    { get; set; }
    public int            MartinMax     { get; set; }
    public decimal        PnlPercent    { get; set; }
    public decimal        PnlAmount     { get; set; }
    public bool           IsStopLoss    { get; set; }
    public int            Leverage      { get; set; }
    public DateTime       OpenedAt      { get; set; }
    public DateTime       ClosedAt      { get; set; }
}
