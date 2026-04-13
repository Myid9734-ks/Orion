using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.OKX;

/// <summary>
/// IDataProvider 구현 — OKX WebSocket 실시간 데이터
/// </summary>
public class OkxDataProvider : IDataProvider
{
    private readonly OkxWebSocketClient _ws;
    private readonly OkxRestClient      _rest;
    private readonly string             _instId;
    private decimal                     _lastPrice;

    public event EventHandler<Candle>? OnCandleCompleted;

    public OkxDataProvider(OkxWebSocketClient ws, OkxRestClient rest, string instId)
    {
        _ws     = ws;
        _rest   = rest;
        _instId = instId;

        _ws.OnCandleCompleted += (_, c) => OnCandleCompleted?.Invoke(this, c);
        _ws.OnPriceUpdated    += (_, p) => _lastPrice = p;
    }

    public async Task StartAsync(CancellationToken ct)
        => await _ws.StartAsync(_instId, ct);

    public async Task StopAsync()
        => await _ws.StopAsync();

    public async Task<List<Candle>> GetRecentCandlesAsync(int count, string bar = "1m")
        => await _rest.GetCandlesAsync(_instId, count, bar);

    public Task<decimal> GetCurrentPriceAsync()
        => _lastPrice > 0
            ? Task.FromResult(_lastPrice)
            : _rest.GetTickerPriceAsync(_instId);
}
