using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.Backtest;

/// <summary>
/// IOrderExecutor 구현 — 가상 주문 처리 (백테스트용)
/// 실제 API 호출 없이 내부 계산만 수행
/// </summary>
public class VirtualOrderExecutor : IOrderExecutor
{
    private decimal _virtualBalance;

    public VirtualOrderExecutor(decimal initialBalance = 1000m)
    {
        _virtualBalance = initialBalance;
    }

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request)
    {
        // 가상 주문: 항상 성공, 즉시 체결
        var result = new OrderResult
        {
            Success      = true,
            OrderId      = $"VIRTUAL-{Guid.NewGuid():N}",
            FilledPrice  = 0, // TradingCore에서 현재가 직접 사용
            FilledAmount = request.Amount
        };
        return Task.FromResult(result);
    }

    public Task<OrderResult> ClosePositionAsync(string symbol, TradeDirection direction)
    {
        return Task.FromResult(new OrderResult
        {
            Success = true,
            OrderId = $"VIRTUAL-CLOSE-{Guid.NewGuid():N}"
        });
    }

    public Task<bool> SetLeverageAsync(string symbol, int leverage, string marginMode)
        => Task.FromResult(true);

    public Task<decimal> GetBalanceAsync()
        => Task.FromResult(_virtualBalance);
}
