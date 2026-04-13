using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.OKX;

/// <summary>
/// IOrderExecutor 구현 — OKX REST API 실제 주문 실행
/// </summary>
public class OkxOrderExecutor : IOrderExecutor
{
    private readonly OkxRestClient _rest;
    private readonly ILogger<OkxOrderExecutor> _logger;
    private string _marginMode = "cross"; // SetLeverageAsync 호출 시 갱신

    public OkxOrderExecutor(OkxRestClient rest, ILogger<OkxOrderExecutor> logger)
    {
        _rest   = rest;
        _logger = logger;
    }

    public async Task<OrderResult> PlaceOrderAsync(OrderRequest request)
    {
        var (side, posSide) = request.Direction switch
        {
            TradeDirection.Long  => ("buy",  "long"),
            TradeDirection.Short => ("sell", "short"),
            _ => throw new ArgumentException("Invalid direction")
        };

        var sz = request.Amount;

        _logger.LogInformation("주문: {side} {posSide} {sz}USDT @ {symbol} [{mgnMode}]",
            side, posSide, sz, request.Symbol, request.MarginMode);

        return await _rest.PlaceMarketOrderAsync(request.Symbol, side, posSide, sz, request.MarginMode);
    }

    public async Task<OrderResult> ClosePositionAsync(string symbol, TradeDirection direction)
    {
        var posSide  = direction == TradeDirection.Long ? "long" : "short";
        _logger.LogInformation("청산: {symbol} {posSide}", symbol, posSide);
        return await _rest.ClosePositionAsync(symbol, posSide, _marginMode);
    }

    public async Task<bool> SetLeverageAsync(string symbol, int leverage, string marginMode)
    {
        _marginMode = marginMode;
        return await _rest.SetLeverageAsync(symbol, leverage, marginMode);
    }

    public async Task<decimal> GetBalanceAsync()
        => await _rest.GetBalanceAsync();
}
