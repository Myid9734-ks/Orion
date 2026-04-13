using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Core.Interfaces;

/// <summary>
/// 주문 실행 인터페이스.
/// 실매매: OkxOrderExecutor (OKX REST API)
/// 백테스트: VirtualOrderExecutor (가상 주문 처리)
/// </summary>
public interface IOrderExecutor
{
    /// <summary>포지션 진입 주문</summary>
    Task<OrderResult> PlaceOrderAsync(OrderRequest request);

    /// <summary>전체 포지션 청산</summary>
    Task<OrderResult> ClosePositionAsync(string symbol, TradeDirection direction);

    /// <summary>레버리지 및 마진 모드 설정</summary>
    Task<bool> SetLeverageAsync(string symbol, int leverage, string marginMode);

    /// <summary>잔고 조회 (USDT)</summary>
    Task<decimal> GetBalanceAsync();
}
