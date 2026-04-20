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

    public bool SupportsServerSidePreOrders => true;

    public event EventHandler<AlgoOrderFillEvent>? OnAlgoOrderFilled;

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
    }

    // ── 시장가 즉시 진입 ──────────────────────────────
    public async Task<OrderResult> PlaceOrderAsync(OrderRequest request)
    {
        var (side, posSide) = request.Direction switch
        {
            TradeDirection.Long  => ("buy",  "long"),
            TradeDirection.Short => ("sell", "short"),
            _ => throw new ArgumentException("Invalid direction")
        };

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
        _logger.LogInformation("[Executor] 레버리지 설정: {symbol} x{lev} [{mode}]",
            symbol, leverage, marginMode);
        return await _rest.SetLeverageAsync(symbol, leverage, marginMode);
    }

    public async Task<decimal> GetBalanceAsync()
        => await _rest.GetBalanceAsync();

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
