namespace OKXTradingBot.Core.Models;

/// <summary>
/// 마틴 단계별 트리거(예약) 주문 요청.
/// OKX algo orders (ordType: trigger) 로 변환되어 서버에 등록된다.
/// </summary>
public class TriggerOrderRequest
{
    public string         Symbol       { get; set; } = string.Empty;
    public TradeDirection Direction    { get; set; }
    public decimal        Amount       { get; set; }    // USDT 명목금액
    public decimal        TriggerPrice { get; set; }    // 발동 가격
    public string         MarginMode   { get; set; } = "cross";
    public int            Step         { get; set; }    // 마틴 N단계 (추적/디버그용)
    public bool           IsClose      { get; set; } = false; // true = reduce-only (TP)
}

/// <summary>
/// OKX Private WS 로부터 수신한 algo 주문 체결 이벤트.
/// orders-algo 채널의 state="effective" or "filled" 메시지에서 생성.
/// </summary>
public class AlgoOrderFillEvent
{
    public string         AlgoId      { get; set; } = string.Empty;
    public string         OrderId     { get; set; } = string.Empty;
    public string         Symbol      { get; set; } = string.Empty;
    public TradeDirection Direction   { get; set; }
    public decimal        FilledPrice { get; set; }
    public decimal        FilledSize  { get; set; }    // 체결 수량
    public decimal        NotionalUsd { get; set; }    // 명목 USDT 금액
    public bool           IsClose     { get; set; }    // reduce-only 여부 (TP 체결 식별)
    public int            Step        { get; set; }    // 매핑된 마틴 단계 (없으면 0)
    public DateTime       Timestamp   { get; set; } = DateTime.UtcNow;
}
