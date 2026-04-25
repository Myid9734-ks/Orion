using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.OKX;

/// <summary>
/// IOrderExecutor 구현 — OKX REST + Private WS
/// 실거래 모드에서 마틴 트리거/익절 conditional 주문을 서버에 등록하고
/// Private WS 로 체결 알림을 수신해 TradingCore 에 이벤트 전달.
/// </summary>
public class OkxOrderExecutor : IOrderExecutor
{
    private readonly OkxRestClient                _rest;
    private readonly OkxPrivateWebSocketClient    _privWs;
    private readonly ILogger<OkxOrderExecutor>    _logger;
    private string                                _marginMode = "cross";
    private int                                   _leverage   = 1;

    /// <summary>현재 설정된 레버리지 (지정가 변환용).</summary>
    public int Leverage => _leverage;
    /// <summary>REST 클라이언트 노출 (지정가 watchdog 등 추가 작업용).</summary>
    public OkxRestClient Rest => _rest;

    public bool SupportsServerSidePreOrders => true;

    public event EventHandler<AlgoOrderFillEvent>? OnAlgoOrderFilled;
    public event EventHandler?                     OnStreamReconnected;

    public OkxOrderExecutor(
        OkxRestClient rest,
        OkxPrivateWebSocketClient privWs,
        ILogger<OkxOrderExecutor> logger)
    {
        _rest   = rest;
        _privWs = privWs;
        _logger = logger;

        _privWs.OnAlgoOrderFilled += (s, e) =>
        {
            _logger.LogInformation("[Executor] PrivWS → AlgoFill 이벤트 중계: algoId={a} {d} {sz}@{px} close={c}",
                e.AlgoId, e.Direction, e.FilledSize, e.FilledPrice, e.IsClose);
            try { OnAlgoOrderFilled?.Invoke(this, e); }
            catch (Exception ex) { _logger.LogError(ex, "[Executor] AlgoFill 핸들러 예외"); }
        };

        _privWs.OnReconnected += (s, _) =>
        {
            _logger.LogInformation("[Executor] PrivWS 재연결 감지 → OnStreamReconnected 발사");
            try { OnStreamReconnected?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.LogError(ex, "[Executor] Reconnect 핸들러 예외"); }
        };
    }

    // ── 진입 (지정가 우선, 미체결 시 시장가 fallback) ──────
    public async Task<OrderResult> PlaceOrderAsync(OrderRequest request)
    {
        var (side, posSide) = request.Direction switch
        {
            TradeDirection.Long  => ("buy",  "long"),
            TradeDirection.Short => ("sell", "short"),
            _ => throw new ArgumentException("Invalid direction")
        };

        // 지정가 시도 — Price 가 지정되어 있고 OrderType.Limit
        if (request.Type == OrderType.Limit && request.Price.HasValue && request.Price.Value > 0)
        {
            var px = request.Price.Value;
            _logger.LogInformation("[Executor] 지정가 진입 시도: {side} {posSide} {amt}USDT @ {px} [lev x{lev}]",
                side, posSide, request.Amount, px, _leverage);

            var limitRes = await _rest.PlaceLimitOrderUsdtAsync(
                request.Symbol, side, posSide, request.Amount, px, _leverage, request.MarginMode);

            if (limitRes.Success)
            {
                // 3초 대기 → 체결 확인. 미체결이면 1회 정정(현재가 추정 = 동일 px), 추가 3초, 최종 시장가 fallback
                var fill = await _rest.WaitForFillAsync(request.Symbol, limitRes.OrderId, timeoutMs: 3000);
                if (fill.Success && fill.FilledSize > 0)
                {
                    _logger.LogInformation("[Executor] 지정가 체결: ordId={oid} px={px} sz={sz}",
                        limitRes.OrderId, fill.FilledPrice, fill.FilledSize);
                    return fill;
                }

                // 미체결 → 취소 후 시장가 fallback
                _logger.LogWarning("[Executor] 지정가 미체결 → 취소 + 시장가 fallback: ordId={oid}", limitRes.OrderId);
                try { await _rest.CancelOrderAsync(request.Symbol, limitRes.OrderId); } catch { }
            }
            else
            {
                _logger.LogWarning("[Executor] 지정가 주문 실패 → 시장가 fallback: {err}", limitRes.ErrorMessage);
            }
        }

        _logger.LogInformation("[Executor] 시장가 주문: {side} {posSide} {sz}USDT @ {symbol} [{mgnMode}]",
            side, posSide, request.Amount, request.Symbol, request.MarginMode);

        return await _rest.PlaceMarketOrderAsync(request.Symbol, side, posSide, request.Amount, request.MarginMode);
    }

    public async Task<OrderResult> ClosePositionAsync(string symbol, TradeDirection direction)
    {
        var posSide = direction == TradeDirection.Long ? "long" : "short";
        _logger.LogInformation("[Executor] 시장가 청산: {symbol} {posSide}", symbol, posSide);
        return await _rest.ClosePositionAsync(symbol, posSide, _marginMode);
    }

    public async Task<bool> SetLeverageAsync(string symbol, int leverage, string marginMode)
    {
        _marginMode = marginMode;
        _leverage   = leverage > 0 ? leverage : 1;
        _logger.LogInformation("[Executor] 레버리지 설정: {symbol} x{lev} [{mode}]",
            symbol, leverage, marginMode);
        return await _rest.SetLeverageAsync(symbol, leverage, marginMode);
    }

    public async Task<decimal> GetBalanceAsync()
        => await _rest.GetBalanceAsync();

    public async Task<(decimal Taker, decimal Maker)> GetFeeRatesAsync(string symbol)
        => await _rest.GetFeeRatesAsync(symbol);

    // 실거래는 OKX 서버가 청산 처리 — 시뮬레이션 불필요
    public decimal? GetLiquidationPrice() => null;

    // ── Algo (트리거 / 익절) ──────────────────────────
    public async Task<OrderResult> PlaceTriggerOrderAsync(TriggerOrderRequest request)
    {
        // 추가 진입 트리거 = 동일 방향 매수 추가 (reduceOnly=false)
        var (side, posSide) = request.Direction switch
        {
            TradeDirection.Long  => ("buy",  "long"),
            TradeDirection.Short => ("sell", "short"),
            _ => throw new ArgumentException("Invalid direction")
        };

        _logger.LogInformation(
            "[Executor] 트리거 주문 등록: 마틴{step}단계 {side}/{posSide} {amt}USDT @ trigger={trig:N4}",
            request.Step, side, posSide, request.Amount, request.TriggerPrice);

        return await _rest.PlaceTriggerAlgoOrderAsync(
            request.Symbol, side, posSide,
            request.Amount, request.TriggerPrice,
            request.MarginMode, reduceOnly: false);
    }

    public async Task<OrderResult> PlaceTakeProfitOrderAsync(
        string symbol, TradeDirection direction, decimal triggerPrice, string marginMode)
    {
        // 익절: 보유 방향의 반대 side, posSide 는 유지
        var (side, posSide) = direction switch
        {
            TradeDirection.Long  => ("sell", "long"),   // long 청산 = sell
            TradeDirection.Short => ("buy",  "short"),  // short 청산 = buy
            _ => throw new ArgumentException("Invalid direction")
        };

        _logger.LogInformation(
            "[Executor] 익절 conditional 등록: {symbol} {dir} → trigger={trig:N4}",
            symbol, direction, triggerPrice);

        return await _rest.PlaceTakeProfitAlgoOrderAsync(
            symbol, side, posSide, triggerPrice, marginMode);
    }

    public async Task<bool> CancelAlgoOrderAsync(string symbol, string algoId)
    {
        _logger.LogInformation("[Executor] algo 주문 취소: {symbol} algoId={id}", symbol, algoId);
        return await _rest.CancelAlgoOrderAsync(symbol, algoId);
    }

    public async Task<bool> CancelAllAlgoOrdersAsync(string symbol)
    {
        _logger.LogInformation("[Executor] 모든 algo 주문 취소 요청: {symbol}", symbol);
        return await _rest.CancelAllAlgoOrdersAsync(symbol);
    }

    public async Task<ExchangePositionInfo?> GetPositionAsync(string symbol)
    {
        _logger.LogInformation("[Executor] 포지션 조회: {symbol}", symbol);
        return await _rest.GetPositionAsync(symbol);
    }

    public async Task<List<AlgoOrderInfo>> GetOpenAlgoOrdersAsync(string symbol)
    {
        _logger.LogInformation("[Executor] 미체결 algo 주문 조회: {symbol}", symbol);
        return await _rest.GetOpenAlgoOrdersAsync(symbol);
    }

    public async Task<List<AlgoOrderInfo>> GetAlgoOrderHistoryAsync(string symbol, int limit = 50)
    {
        _logger.LogInformation("[Executor] algo 히스토리 조회: {symbol} limit={n}", symbol, limit);
        return await _rest.GetAlgoOrderHistoryAsync(symbol, limit);
    }

    // ── 지정가 watchdog 용 ─────────────────────────
    public Task<List<PendingOrderInfo>> GetPendingOrdersAsync(string symbol)
        => _rest.GetPendingOrdersAsync(symbol);

    public Task<bool> AmendOrderAsync(string symbol, string orderId, decimal newPrice)
        => _rest.AmendOrderAsync(symbol, orderId, newPrice);

    public Task<bool> CancelOrderAsync(string symbol, string orderId)
        => _rest.CancelOrderAsync(symbol, orderId);

    public async Task<OrderResult> PlaceLimitReduceOrderAsync(
        string symbol, TradeDirection direction, decimal usdtAmount, decimal price)
    {
        // reduceOnly 청산: 보유 방향의 반대 side, posSide 유지
        var (side, posSide) = direction switch
        {
            TradeDirection.Long  => ("sell", "long"),
            TradeDirection.Short => ("buy",  "short"),
            _ => throw new ArgumentException("Invalid direction")
        };
        _logger.LogInformation("[Executor] 지정가 청산 요청: {symbol} {dir} {amt}USDT @ {px}",
            symbol, direction, usdtAmount, price);
        return await _rest.PlaceLimitOrderUsdtAsync(
            symbol, side, posSide, usdtAmount, price, _leverage, _marginMode, reduceOnly: true);
    }

    public async Task StartPrivateStreamAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Executor] Private WS 시작 요청");
        // instId/instType는 호출자가 SetSymbolAsync 같은 별도 채널로 알려줘야 하지만
        // 단순화를 위해 PrivateWS.StartAsync 시점에 미리 알 수 있는 심볼이 있어야 한다.
        // → 외부에서 SetSymbolForPrivateStream 호출 후 StartPrivateStreamAsync 호출
        if (_pendingPrivStreamSymbol == null)
            throw new InvalidOperationException(
                "Private WS 심볼 미설정 — SetSymbolForPrivateStream() 먼저 호출하세요.");

        await _privWs.StartAsync(_pendingPrivStreamSymbol, ct);
    }

    public async Task StopPrivateStreamAsync()
    {
        _logger.LogInformation("[Executor] Private WS 중지 요청");
        await _privWs.StopAsync();
    }

    // ── Private WS 시작 전 심볼 지정 (인터페이스 외 헬퍼) ──
    private string? _pendingPrivStreamSymbol;
    public void SetSymbolForPrivateStream(string instId)
    {
        _pendingPrivStreamSymbol = instId;
        _logger.LogInformation("[Executor] Private WS 대상 심볼 설정: {id}", instId);
    }
}
