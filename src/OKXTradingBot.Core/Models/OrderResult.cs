namespace OKXTradingBot.Core.Models;

public enum OrderSide { Buy, Sell }
public enum OrderType { Market, Limit }

public class OrderRequest
{
    public string         Symbol     { get; set; } = string.Empty;
    public OrderSide      Side       { get; set; }
    public OrderType      Type       { get; set; } = OrderType.Market;
    public decimal        Amount     { get; set; } // USDT 기준 금액
    public decimal?       Price      { get; set; } // Limit 주문 시
    public TradeDirection Direction  { get; set; }
    public bool           IsClose    { get; set; } = false;
    public string         MarginMode { get; set; } = "cross"; // "cross" | "isolated"
}

public class OrderResult
{
    public bool    Success      { get; set; }
    public string  OrderId      { get; set; } = string.Empty;
    public decimal FilledPrice  { get; set; }
    public decimal FilledAmount { get; set; }
    public decimal FilledSize   { get; set; } // 계약 수 (디버깅용)
    public string? State        { get; set; } // 주문 상태 (filled / partially_filled 등)
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp   { get; set; } = DateTime.UtcNow;
}
