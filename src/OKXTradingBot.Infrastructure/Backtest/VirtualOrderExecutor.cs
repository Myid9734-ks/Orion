using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.Backtest;

/// <summary>
/// IOrderExecutor 구현 — 가상매매(Paper Trading)
/// 실시간 시장 데이터 + 가상 주문 처리.
/// IDataProvider에서 현재가를 가져와 실제와 동일한 체결가 시뮬레이션.
/// 잔고는 가상으로 관리하며, 실거래 전환 시 OkxOrderExecutor로 교체하면 끝.
/// </summary>
public class VirtualOrderExecutor : IOrderExecutor
{
    private readonly IDataProvider _dataProvider;
    private decimal _virtualBalance;
    private decimal _initialBalance;
    private decimal _accountBalance;   // 총 계좌잔고 (교차 마진 청산가 계산용)
    private string  _marginMode = "cross";

    // 현재 열린 포지션 추적 (가상 잔고 반영용)
    private decimal _openPositionAmount  = 0;
    private decimal _openPositionEntry   = 0;
    private TradeDirection _openDirection = TradeDirection.Long;
    private int _leverage = 10;

    public VirtualOrderExecutor(IDataProvider dataProvider, decimal initialBalance = 1000m, decimal accountBalance = 0m)
    {
        _dataProvider   = dataProvider;
        _virtualBalance = initialBalance;
        _initialBalance = initialBalance;
        _accountBalance = accountBalance > 0 ? accountBalance : initialBalance;
    }

    /// <summary>현재 가상 잔고</summary>
    public decimal VirtualBalance => _virtualBalance;

    public async Task<OrderResult> PlaceOrderAsync(OrderRequest request)
    {
        // 실시간 현재가로 체결
        decimal filledPrice;
        try
        {
            filledPrice = await _dataProvider.GetCurrentPriceAsync();
        }
        catch
        {
            filledPrice = 0; // fallback — TradingCore에서 candle.Close 사용
        }

        // 가상 잔고에서 투입금 차감 (레버리지 적용 전 원금 기준)
        var margin = request.Amount / _leverage; // 실제 필요 증거금
        if (_virtualBalance < margin)
        {
            return new OrderResult
            {
                Success      = false,
                ErrorMessage = $"잔고 부족: 필요 {margin:F2} USDT, 보유 {_virtualBalance:F2} USDT"
            };
        }

        _virtualBalance -= margin;
        _openPositionAmount += request.Amount;
        if (_openPositionEntry == 0)
        {
            _openPositionEntry = filledPrice;
            _openDirection     = request.Direction;
        }
        else
        {
            // 가중 평균 — TradingCore에서도 계산하지만, 여기서도 추적
            _openPositionEntry = ((_openPositionAmount - request.Amount) * _openPositionEntry
                                  + request.Amount * filledPrice)
                                 / _openPositionAmount;
        }

        return new OrderResult
        {
            Success      = true,
            OrderId      = $"VIRTUAL-{Guid.NewGuid():N}",
            FilledPrice  = filledPrice,
            FilledAmount = request.Amount,
            Timestamp    = DateTime.UtcNow
        };
    }

    public async Task<OrderResult> ClosePositionAsync(string symbol, TradeDirection direction)
    {
        decimal exitPrice;
        try
        {
            exitPrice = await _dataProvider.GetCurrentPriceAsync();
        }
        catch
        {
            exitPrice = _openPositionEntry; // fallback
        }

        // PnL 계산 후 잔고 반영
        if (_openPositionAmount > 0 && _openPositionEntry > 0)
        {
            var pnlPct = _openDirection == TradeDirection.Long
                ? (exitPrice - _openPositionEntry) / _openPositionEntry
                : (_openPositionEntry - exitPrice) / _openPositionEntry;

            var totalMargin = _openPositionAmount / _leverage;
            var pnlAmount   = totalMargin * _leverage * pnlPct; // 레버리지 적용 PnL

            _virtualBalance += totalMargin + pnlAmount; // 증거금 + 손익 반환
        }

        // 포지션 초기화
        _openPositionAmount = 0;
        _openPositionEntry  = 0;

        return new OrderResult
        {
            Success     = true,
            OrderId     = $"VIRTUAL-CLOSE-{Guid.NewGuid():N}",
            FilledPrice = exitPrice,
            Timestamp   = DateTime.UtcNow
        };
    }

    public Task<bool> SetLeverageAsync(string symbol, int leverage, string marginMode)
    {
        _leverage   = leverage;
        _marginMode = marginMode.ToLower();
        return Task.FromResult(true);
    }

    /// <summary>
    /// 현재 포지션의 예상 청산가.
    /// 격리: 포지션 증거금만으로 계산. 교차: 전체 계좌잔고 기준으로 계산.
    /// </summary>
    public decimal? GetLiquidationPrice()
    {
        if (_openPositionAmount <= 0 || _openPositionEntry <= 0) return null;

        if (_marginMode == "isolated")
        {
            return _openDirection == TradeDirection.Long
                ? _openPositionEntry * (1 - 1m / _leverage)
                : _openPositionEntry * (1 + 1m / _leverage);
        }
        else // cross
        {
            // 전체 계좌잔고가 손실을 다 흡수했을 때 청산
            var liq = _openDirection == TradeDirection.Long
                ? _openPositionEntry * (1 - _accountBalance / _openPositionAmount)
                : _openPositionEntry * (1 + _accountBalance / _openPositionAmount);
            return liq > 0 ? liq : null; // 계좌잔고 > 포지션 규모면 사실상 청산 없음
        }
    }

    public Task<decimal> GetBalanceAsync()
        => Task.FromResult(_virtualBalance);

    // ── 서버 사이드 예약 주문 미지원 (모의거래는 캔들 폴링 방식 유지) ──
    public bool SupportsServerSidePreOrders => false;

    public event EventHandler<AlgoOrderFillEvent>? OnAlgoOrderFilled
    {
        add    { /* no-op */ }
        remove { /* no-op */ }
    }

    public event EventHandler? OnStreamReconnected
    {
        add    { /* no-op */ }
        remove { /* no-op */ }
    }

    public Task<OrderResult> PlaceTriggerOrderAsync(TriggerOrderRequest request)
        => Task.FromResult(new OrderResult { Success = false, ErrorMessage = "Virtual: trigger 미지원" });

    public Task<OrderResult> PlaceTakeProfitOrderAsync(
        string symbol, TradeDirection direction, decimal triggerPrice, string marginMode)
        => Task.FromResult(new OrderResult { Success = false, ErrorMessage = "Virtual: TP algo 미지원" });

    public Task<bool> CancelAlgoOrderAsync(string symbol, string algoId) => Task.FromResult(true);
    public Task<bool> CancelAllAlgoOrdersAsync(string symbol) => Task.FromResult(true);
    public Task StartPrivateStreamAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopPrivateStreamAsync() => Task.CompletedTask;

    // 가상매매는 재시작 동기화 불필요
    public Task<ExchangePositionInfo?> GetPositionAsync(string symbol)
        => Task.FromResult<ExchangePositionInfo?>(null);

    public Task<List<AlgoOrderInfo>> GetOpenAlgoOrdersAsync(string symbol)
        => Task.FromResult(new List<AlgoOrderInfo>());

    public Task<List<AlgoOrderInfo>> GetAlgoOrderHistoryAsync(string symbol, int limit = 50)
        => Task.FromResult(new List<AlgoOrderInfo>());
}
