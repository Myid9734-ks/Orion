namespace OKXTradingBot.Core.Models;

/// <summary>
/// 1분봉 캔들 데이터
/// </summary>
public class Candle
{
    public DateTime Timestamp { get; set; }
    public decimal Open   { get; set; }
    public decimal High   { get; set; }
    public decimal Low    { get; set; }
    public decimal Close  { get; set; }
    public decimal Volume { get; set; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm}] O:{Open} H:{High} L:{Low} C:{Close} V:{Volume}";
}
