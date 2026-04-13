using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Core.Trading;

/// <summary>
/// 매매 로직 코어 — 마틴게일 전략
/// 백테스트/실매매 동일 코드. IDataProvider, IOrderExecutor 교체만으로 모드 전환.
/// </summary>
public class TradingCore
{
    private readonly IDataProvider  _data;
    private readonly IOrderExecutor _executor;
    private readonly INotifier?     _notifier;
    private readonly TradeConfig    _config;
    private readonly ILogger<TradingCore> _logger;

    private Position _position = new();
    private bool     _running  = false;
    private bool     _autoRepeat = false;

    // 상태 업데이트 이벤트 (UI 바인딩용)
    public event EventHandler<Position>? OnPositionUpdated;
    public event EventHandler<string>?  OnLogMessage;

    public TradingCore(
        IDataProvider dataProvider,
        IOrderExecutor orderExecutor,
        TradeConfig config,
        ILogger<TradingCore> logger,
        INotifier? notifier = null)
    {
        _data     = dataProvider;
        _executor = orderExecutor;
        _config   = config;
        _logger   = logger;
        _notifier = notifier;

        _data.OnCandleCompleted += OnCandleCompletedAsync;
    }

    // ─────────────────────────────────────────────
    // 시작 / 중지
    // ─────────────────────────────────────────────

    public async Task StartAsync(bool autoRepeat, CancellationToken ct)
    {
        _running    = true;
        _autoRepeat = autoRepeat;

        await _executor.SetLeverageAsync(_config.Symbol, _config.Leverage, _config.MarginModeStr);
        await _data.StartAsync(ct);
        Log("매매 시작. 첫 캔들 완성 대기 중...");
    }

    public async Task StopAsync()
    {
        _running = false;
        await _data.StopAsync();
        Log("매매 중지");
    }

    // ─────────────────────────────────────────────
    // 캔들 완성 시 처리 (메인 루프)
    // ─────────────────────────────────────────────

    private async void OnCandleCompletedAsync(object? sender, Candle candle)
    {
        if (!_running) return;

        try
        {
            var currentPrice = candle.Close;

            // 포지션 없음 → GPT 분석 후 신규 진입
            if (_position.Status == PositionStatus.None)
            {
                await TryEnterNewPositionAsync(candle);
            }
            // 포지션 보유 중 → 마틴게일 / 익절 / 손절 체크
            else if (_position.Status == PositionStatus.Open)
            {
                await CheckExitConditionsAsync(currentPrice);

                if (_position.Status == PositionStatus.Open)
                    await CheckMartinEntryAsync(currentPrice);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "캔들 처리 중 오류");
            await NotifyAsync($"⚠️ 오류 발생: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────
    // 신규 진입 (GPT 분석 → 1단계 진입)
    // ─────────────────────────────────────────────

    private async Task TryEnterNewPositionAsync(Candle latestCandle)
    {
        // 최근 N개 봉 조회
        var candles = await _data.GetRecentCandlesAsync(_config.GptCandleCount);

        // TODO: GptAnalyzer 호출 (2단계에서 구현)
        // var gptResult = await _gptAnalyzer.AnalyzeAsync(candles);
        // if (!gptResult.ShouldEnter) { Log($"GPT 신뢰도 부족 ({gptResult.Confidence}), 진입 보류"); return; }

        // --- 임시: 랜덤 방향으로 테스트 ---
        var direction = DateTime.UtcNow.Second % 2 == 0
            ? TradeDirection.Long : TradeDirection.Short;
        Log($"[GPT 분석] 방향: {direction} (임시 랜덤 — 2단계에서 실제 GPT 연동)");

        await EnterAsync(direction, latestCandle.Close, isFirstEntry: true);
    }

    // ─────────────────────────────────────────────
    // 마틴게일 추가 진입 체크
    // ─────────────────────────────────────────────

    private async Task CheckMartinEntryAsync(decimal currentPrice)
    {
        if (_position.MartinStep >= _config.MartinCount)
        {
            Log($"마틴 최대 단계 도달 ({_config.MartinCount}회), 추가 진입 없음");
            return;
        }

        var gap          = _config.GetMartinGapForStep(_position.MartinStep);
        var triggerPrice = _position.GetNextMartinTriggerPrice(gap);
        bool triggered = _position.Direction == TradeDirection.Long
            ? currentPrice <= triggerPrice
            : currentPrice >= triggerPrice;

        if (triggered)
        {
            Log($"🔁 마틴 {_position.MartinStep + 1}단계 트리거 (현재가: {currentPrice}, 트리거: {triggerPrice:F2}, 간격: {gap}%)");
            await EnterAsync(_position.Direction, currentPrice, isFirstEntry: false);
        }
    }

    // ─────────────────────────────────────────────
    // 익절 / 손절 체크
    // ─────────────────────────────────────────────

    private async Task CheckExitConditionsAsync(decimal currentPrice)
    {
        var pnlPct = _position.GetUnrealizedPnlPercent(currentPrice);

        // 익절
        var targetProfit = _config.GetTargetProfitForStep(_position.MartinStep);
        if (pnlPct >= targetProfit)
        {
            Log($"✅ 익절 조건 충족: {pnlPct:F2}% (목표: {targetProfit}%)");
            await ClosePositionAsync(currentPrice, isStopLoss: false);
            return;
        }

        // 손절 (활성화된 경우)
        if (_config.StopLossEnabled && pnlPct <= -_config.StopLossPercent)
        {
            Log($"🛑 손절 조건 충족: {pnlPct:F2}% (기준: -{_config.StopLossPercent}%)");
            await ClosePositionAsync(currentPrice, isStopLoss: true);
        }
    }

    // ─────────────────────────────────────────────
    // 진입 실행
    // ─────────────────────────────────────────────

    private async Task EnterAsync(TradeDirection direction, decimal price, bool isFirstEntry)
    {
        var amount = _config.SingleOrderAmount;

        var result = await _executor.PlaceOrderAsync(new OrderRequest
        {
            Symbol     = _config.Symbol,
            Side       = direction == TradeDirection.Long ? OrderSide.Buy : OrderSide.Sell,
            Direction  = direction,
            Amount     = amount,
            MarginMode = _config.MarginModeStr
        });

        if (!result.Success)
        {
            Log($"❌ 주문 실패: {result.ErrorMessage}");
            return;
        }

        // 포지션 업데이트
        if (isFirstEntry)
        {
            _position = new Position
            {
                Direction      = direction,
                Status         = PositionStatus.Open,
                MartinStep     = 1,
                TotalAmount    = amount,
                AvgEntryPrice  = price,
                LastEntryPrice = price,
                OpenedAt       = DateTime.UtcNow
            };
        }
        else
        {
            _position.MartinStep++;
            _position.TotalAmount    += amount;
            // 가중 평균 진입가 재계산
            _position.AvgEntryPrice   = (_position.AvgEntryPrice * (_position.TotalAmount - amount) + price * amount)
                                        / _position.TotalAmount;
            _position.LastEntryPrice  = price;
        }

        var msg = isFirstEntry
            ? $"📈 신규 진입 [{direction}] {amount}USDT @ {price}"
            : $"➕ 마틴 {_position.MartinStep}단계 [{direction}] {amount}USDT @ {price} (평균가: {_position.AvgEntryPrice:F2})";

        Log(msg);
        await NotifyAsync(msg);
        OnPositionUpdated?.Invoke(this, _position);
    }

    // ─────────────────────────────────────────────
    // 청산 실행
    // ─────────────────────────────────────────────

    private async Task ClosePositionAsync(decimal currentPrice, bool isStopLoss)
    {
        var result = await _executor.ClosePositionAsync(_config.Symbol, _position.Direction);

        if (!result.Success)
        {
            Log($"❌ 청산 실패: {result.ErrorMessage}");
            return;
        }

        var pnlPct = _position.GetUnrealizedPnlPercent(currentPrice);
        var pnlAmt = _position.TotalAmount * pnlPct / 100 * _config.Leverage;

        _position.Status    = PositionStatus.Closed;
        _position.ClosedAt  = DateTime.UtcNow;
        _position.RealizedPnl = pnlAmt;

        var emoji = isStopLoss ? "🛑" : "✅";
        var type  = isStopLoss ? "손절" : "익절";
        var msg   = $"{emoji} {type} 청산 완료 | {pnlPct:+0.00;-0.00}% ({pnlAmt:+0.00;-0.00} USDT) | 마틴 {_position.MartinStep}단계";

        Log(msg);
        await NotifyAsync(msg);
        OnPositionUpdated?.Invoke(this, _position);

        // 자동반복 모드
        if (_autoRepeat && _running)
        {
            Log("🔄 자동반복: 다음 사이클 시작 대기 중...");
            _position = new Position();
        }
        else
        {
            _running = false;
        }
    }

    // ─────────────────────────────────────────────
    // 헬퍼
    // ─────────────────────────────────────────────

    private void Log(string message)
    {
        _logger.LogInformation(message);
        OnLogMessage?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private async Task NotifyAsync(string message)
    {
        if (_notifier != null)
        {
            try { await _notifier.SendAsync(message); }
            catch (Exception ex) { _logger.LogWarning("텔레그램 알림 실패: {msg}", ex.Message); }
        }
    }
}
