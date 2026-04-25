using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Core.Interfaces;

/// <summary>
/// 주문 실행 인터페이스.
/// 실매매: OkxOrderExecutor (OKX REST + Private WS)
/// 백테스트: VirtualOrderExecutor (가상 주문 처리)
/// </summary>
public interface IOrderExecutor
{
    // ── 시장가 즉시 주문 ──────────────────────────────
    Task<OrderResult> PlaceOrderAsync(OrderRequest request);

    /// <summary>전체 포지션 청산 (시장가)</summary>
    Task<OrderResult> ClosePositionAsync(string symbol, TradeDirection direction);

    /// <summary>레버리지 및 마진 모드 설정</summary>
    Task<bool> SetLeverageAsync(string symbol, int leverage, string marginMode);

    /// <summary>잔고 조회 (USDT)</summary>
    Task<decimal> GetBalanceAsync();

    /// <summary>Taker/Maker 수수료율 조회. 실거래: OKX API. 가상매매: 기본값 반환.</summary>
    Task<(decimal Taker, decimal Maker)> GetFeeRatesAsync(string symbol);

    /// <summary>
    /// 현재 포지션의 청산가 반환 (모의거래 전용 — 실거래는 null).
    /// 마진 모드(교차/격리)와 계좌잔고를 반영한 시뮬레이션 값.
    /// </summary>
    decimal? GetLiquidationPrice();

    // ── 서버 사이드 예약 주문 (실거래 전용) ─────────────
    /// <summary>
    /// true = 마틴 N단계 진입 트리거 + 익절 주문을 OKX 서버에 등록할 수 있다.
    /// false = 캔들 폴링 방식으로 봇이 직접 감시한다.
    /// </summary>
    bool SupportsServerSidePreOrders { get; }

    /// <summary>트리거 시장가 주문 등록 (마틴 단계 사전 예약). 반환 OrderId = algoId</summary>
    Task<OrderResult> PlaceTriggerOrderAsync(TriggerOrderRequest request);

    /// <summary>익절 conditional 주문 등록 (close 100%). 반환 OrderId = algoId</summary>
    Task<OrderResult> PlaceTakeProfitOrderAsync(
        string symbol, TradeDirection direction,
        decimal triggerPrice, string marginMode);

    /// <summary>특정 algo 주문 취소</summary>
    Task<bool> CancelAlgoOrderAsync(string symbol, string algoId);

    /// <summary>심볼의 모든 pending algo 주문 일괄 취소</summary>
    Task<bool> CancelAllAlgoOrdersAsync(string symbol);

    /// <summary>거래소에서 현재 열린 포지션 조회 (재시작 동기화용). 포지션 없으면 null.</summary>
    Task<ExchangePositionInfo?> GetPositionAsync(string symbol);

    /// <summary>미체결 algo 주문 목록 조회 (재시작 동기화용)</summary>
    Task<List<AlgoOrderInfo>> GetOpenAlgoOrdersAsync(string symbol);

    /// <summary>최근 발동/취소된 algo 주문 히스토리 (WS 누락 복구용). effective 상태만 반환.</summary>
    Task<List<AlgoOrderInfo>> GetAlgoOrderHistoryAsync(string symbol, int limit = 50);

    // ── 지정가 watchdog 용 ─────────────────────────
    /// <summary>심볼의 미체결 일반 주문 목록 (지정가/post_only 등). algo는 별도 endpoint.</summary>
    Task<List<PendingOrderInfo>> GetPendingOrdersAsync(string symbol);

    /// <summary>일반 지정가 주문 가격 정정.</summary>
    Task<bool> AmendOrderAsync(string symbol, string orderId, decimal newPrice);

    /// <summary>일반 주문 취소.</summary>
    Task<bool> CancelOrderAsync(string symbol, string orderId);

    /// <summary>지정가 청산 주문 (reduceOnly=true). USDT 금액 기준, 자동으로 계약수 환산.</summary>
    Task<OrderResult> PlaceLimitReduceOrderAsync(
        string symbol, TradeDirection direction, decimal usdtAmount, decimal price);

    /// <summary>algo 주문 체결/발동 이벤트 (Private WS).</summary>
    event EventHandler<AlgoOrderFillEvent>? OnAlgoOrderFilled;

    /// <summary>Private WS 재연결 완료 이벤트 — 구독자는 누락 이벤트 복구를 수행해야 함.</summary>
    event EventHandler? OnStreamReconnected;

    /// <summary>Private WS 시작 (실거래 모드 진입 시 1회)</summary>
    Task StartPrivateStreamAsync(CancellationToken ct);

    /// <summary>Private WS 중지</summary>
    Task StopPrivateStreamAsync();
}
