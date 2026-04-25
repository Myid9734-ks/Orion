namespace OKXTradingBot.Core.Models;

/// <summary>거래소에서 조회한 실제 포지션 정보 (재시작 동기화용)</summary>
public class ExchangePositionInfo
{
    public string         Symbol        { get; set; } = "";
    public TradeDirection Direction     { get; set; }
    public decimal        AvgEntryPrice { get; set; }
    public decimal        TotalQuantity { get; set; }  // 계약 수량
    public decimal        NotionalUsd   { get; set; }  // 명목 USDT 금액
    public DateTime       OpenedAt      { get; set; }
}

/// <summary>미체결 일반 주문 정보 (지정가 watchdog 용)</summary>
public class PendingOrderInfo
{
    public string   OrderId    { get; set; } = "";
    public string   Side       { get; set; } = "";   // buy | sell
    public string   PosSide    { get; set; } = "";   // long | short
    public string   OrdType    { get; set; } = "";   // limit | market | post_only ...
    public decimal  Price      { get; set; }
    public decimal  Size       { get; set; }
    public decimal  FilledSize { get; set; }
    public bool     ReduceOnly { get; set; }
    public DateTime CreatedAt  { get; set; }
}

/// <summary>거래소 미체결 algo 주문 정보 (재시작 동기화용)</summary>
public class AlgoOrderInfo
{
    public string  AlgoId      { get; set; } = "";
    public string  OrdType     { get; set; } = "";  // "trigger" | "conditional"
    public decimal TriggerPx   { get; set; }        // trigger 주문용
    public decimal TpTriggerPx { get; set; }        // conditional TP 주문용
    public bool    IsClose     { get; set; }        // true = 익절(reduceOnly)
    public long    UpdatedAtMs { get; set; }        // 히스토리 조회 시 uTime (ms)
}
