using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;
using System.Linq;

namespace OKXTradingBot.Core.Trading;

/// <summary>
/// 매매 로직 코어 — 마틴게일 전략
///
/// 사이클: 진입 → 감시(마틴/익절/손절) → 청산 → (자동반복 시) 재진입
///
/// 가상매매/실거래 동일 코드:
///   - 가상매매: IDataProvider=OkxDataProvider(실시간) + IOrderExecutor=VirtualOrderExecutor(가상)
///   - 실거래:   IDataProvider=OkxDataProvider(실시간) + IOrderExecutor=OkxOrderExecutor(실제)
/// </summary>
public class TradingCore
{
    private readonly IDataProvider  _data;
    private readonly IOrderExecutor _executor;
    private readonly INotifier?     _notifier;
    private readonly IGptAnalyzer?  _gptAnalyzer;
    private readonly TradeConfig    _config;
    private readonly NotificationConfig _notifyConfig;
    private readonly ILogger<TradingCore> _logger;
    private readonly Action<string>? _logFileSink; // 로그 파일 저장용 콜백

    private Position _position = new();
    private bool     _running  = false;
    private bool     _autoRepeat = false;
    private int      _cycleCount = 0;        // 완료된 사이클 수
    private decimal  _sessionPnl = 0;        // 세션 누적 손익
    private readonly object _lock = new();   // 동시 캔들 처리 방지

    // ── Pre-orders 모드 (실거래) 전용 ─────────────
    private readonly List<string> _activeAlgoIds = new(); // 등록된 마틴 트리거 algoId
    private string?  _activeTpAlgoId;                     // 익절 conditional algoId
    private bool     _preOrderMode;                       // _executor.SupportsServerSidePreOrders 캐시

    // 가격 방향 감지 모드 (GPT 미사용 시)
    private decimal  _priceAnchor = 0;       // 기준가 (봇 시작 시 또는 청산 후 재진입 대기 시점 가격)

    // GPT 간격 관리
    private DateTime _lastGptAnalysisTime = DateTime.MinValue; // 마지막 GPT 분석 시각
    private int      _consecutiveSkipCount = 0;                // 연속 스킵 횟수 (신뢰도 부족)

    // ─────────────────────────────────────────────
    // 이벤트 (UI 바인딩 + 외부 기록용)
    // ─────────────────────────────────────────────

    /// <summary>포지션 상태 업데이트 (진입/추가/청산)</summary>
    public event EventHandler<Position>? OnPositionUpdated;

    /// <summary>로그 메시지</summary>
    public event EventHandler<string>? OnLogMessage;

    /// <summary>거래(사이클) 완료 — UI에서 TradeRecord 생성, DB 저장 등에 사용</summary>
    public event EventHandler<TradeClosedEventArgs>? OnTradeClosed;

    // ─────────────────────────────────────────────
    // 읽기 전용 상태
    // ─────────────────────────────────────────────

    public Position CurrentPosition => _position;
    public bool     IsRunning       => _running;
    public int      CycleCount      => _cycleCount;
    public decimal  SessionPnl      => _sessionPnl;

    // ─────────────────────────────────────────────
    // 생성자
    // ─────────────────────────────────────────────

    public TradingCore(
        IDataProvider dataProvider,
        IOrderExecutor orderExecutor,
        TradeConfig config,
        ILogger<TradingCore> logger,
        INotifier? notifier = null,
        NotificationConfig? notificationConfig = null,
        IGptAnalyzer? gptAnalyzer = null,
        Action<string>? logFileSink = null)
    {
        _data         = dataProvider;
        _executor     = orderExecutor;
        _config       = config;
        _logger       = logger;
        _notifier     = notifier;
        _gptAnalyzer  = gptAnalyzer;
        _logFileSink  = logFileSink;
        _notifyConfig = notificationConfig ?? new NotificationConfig { Enabled = false };

        _data.OnCandleCompleted += OnCandleCompletedAsync;

        // 실거래(pre-orders) 모드: algo 체결 이벤트 구독
        _preOrderMode = _executor.SupportsServerSidePreOrders;
        if (_preOrderMode)
        {
            _executor.OnAlgoOrderFilled += OnAlgoOrderFilledHandler;
            _logger.LogInformation("[TradingCore] Pre-orders 모드 활성화 (서버 사이드 트리거)");
        }
        else
        {
            _logger.LogInformation("[TradingCore] 캔들 폴링 모드 (모의거래)");
        }
    }

    // ═════════════════════════════════════════════
    // 시작 / 중지
    // ═════════════════════════════════════════════

    public async Task StartAsync(bool autoRepeat, CancellationToken ct)
    {
        _running    = true;
        _autoRepeat = autoRepeat;
        _cycleCount = 0;
        _sessionPnl = 0;

        // 레버리지 설정
        var leverageOk = await _executor.SetLeverageAsync(
            _config.Symbol, _config.Leverage, _config.MarginModeStr);

        if (!leverageOk)
            Log("⚠️ 레버리지 설정 실패 — 기본값으로 진행");

        // Pre-orders 모드: Private WS 시작 + 재시작 동기화 OR 잔여 algo 정리
        if (_preOrderMode)
        {
            try
            {
                await _executor.StartPrivateStreamAsync(ct);
                Log("📡 Private WS 시작 — 주문 체결 알림 수신 대기");

                var restored = await TrySyncPositionFromExchangeAsync();
                if (!restored)
                {
                    var cleared = await _executor.CancelAllAlgoOrdersAsync(_config.Symbol);
                    Log(cleared
                        ? "🧹 시작 시 잔여 algo 주문 정리 완료"
                        : "⚠️ 잔여 algo 주문 정리 실패 (계속 진행)");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Private WS 시작 실패: {ex.Message} — 캔들 폴링 폴백");
                _preOrderMode = false;
            }
        }

        var balance = await _executor.GetBalanceAsync();

        // ── 단계별 금액 / 간격 / 익절 요약 ──
        var amounts     = _config.GetAllStepAmounts();
        var amountStr   = string.Join(", ", amounts.Select((a, i) => $"{i + 1}:{a:F0}"));
        var gapStr      = _config.MartinGapSteps.Count > 0
            ? string.Join(", ", _config.MartinGapSteps.Select((g, i) => $"{i + 1}:{g}%"))
            : $"{_config.MartinGap}% (균등)";
        var tpStr       = _config.TargetProfitSteps.Count > 0
            ? string.Join(", ", _config.TargetProfitSteps.Select((t, i) => $"{i + 1}:{t}%"))
            : $"{_config.TargetProfit}% (균등)";
        var slStr       = _config.StopLossEnabled
            ? $"ON ({_config.StopLossPercent}%)"
            : "OFF";
        var gptStr      = _gptAnalyzer != null
            ? $"{_config.GptModel} | 봉:{_config.GptCandleCount} | 신뢰도:{_config.GptConfidenceThreshold}% | 간격:{_config.GptAnalysisInterval}분"
            : "미사용 (가격방향 감지)";

        Log($"🚀 매매 시작 | {_config.Symbol} | 잔고: {balance:N2} USDT | 레버리지: {_config.Leverage}x | {_config.MarginModeStr} | 자동반복: {(_autoRepeat ? "ON" : "OFF")}");
        Log($"   예산: {_config.TotalBudget:F2} USDT | 마틴: {_config.MartinCount}단계 | 배분: {_config.AmountMode} [{amountStr}]");
        Log($"   진입간격: {gapStr} | 익절: {tpStr} | 손절: {slStr}");
        Log($"   GPT: {gptStr}");

        await NotifyAsync(
            $"🚀 <b>봇 시작</b>\n" +
            $"심볼: {_config.Symbol}\n" +
            $"잔고: {balance:N2} USDT\n" +
            $"레버리지: {_config.Leverage}x ({_config.MarginModeStr})\n" +
            $"예산: {_config.TotalBudget:F2} USDT\n" +
            $"마틴: {_config.MartinCount}단계 ({_config.AmountMode})\n" +
            $"진입간격: {gapStr}\n" +
            $"익절: {tpStr}\n" +
            $"손절: {slStr}\n" +
            $"GPT: {gptStr}\n" +
            $"자동반복: {(_autoRepeat ? "ON" : "OFF")}",
            NotificationType.BotStartStop);

        // GPT 미사용 시: 시작 시점 가격을 기준가로 설정
        if (_gptAnalyzer == null)
        {
            try
            {
                _priceAnchor = await _data.GetCurrentPriceAsync();
                Log($"[가격 방향 감지] 기준가 설정: {_priceAnchor:N2} — 다음 캔들 방향으로 진입");
            }
            catch { _priceAnchor = 0; }
        }

        await _data.StartAsync(ct);
        Log("첫 캔들 완성 대기 중...");
    }

    public async Task StopAsync()
    {
        _running = false;
        await _data.StopAsync();

        // 예비주문/익절주문은 취소하지 않음 — OKX 서버에서 사이클 유지
        var msg = $"⏹ 봇 중지 | 완료 사이클: {_cycleCount}회 | 세션 손익: {_sessionPnl:+0.00;-0.00;0.00} USDT";
        Log(msg);
        await NotifyAsync(msg, NotificationType.BotStartStop);
    }

    /// <summary>예비주문/익절주문 취소 후 봇 중지 — 포지션은 유지 (매매감지 X)</summary>
    public async Task ForceCloseAsync()
    {
        Log("🔴 포지션 강제 종료 요청 — 예비/익절 주문 취소 후 포지션 유지");

        if (_preOrderMode)
        {
            try
            {
                await _executor.CancelAllAlgoOrdersAsync(_config.Symbol);
                Log("🧹 예비주문 / 익절주문 일괄 취소");
            }
            catch (Exception ex)
            {
                Log($"⚠️ algo 취소 실패: {ex.Message}");
            }

            try { await _executor.StopPrivateStreamAsync(); }
            catch (Exception ex) { Log($"⚠️ Private WS 중지 실패: {ex.Message}"); }
        }

        _running = false;
        await _data.StopAsync();

        var msg = $"🔴 강제 종료 | 포지션 유지 중 | 완료 사이클: {_cycleCount}회 | 세션 손익: {_sessionPnl:+0.00;-0.00;0.00} USDT";
        Log(msg);
        await NotifyAsync(msg, NotificationType.BotStartStop);
    }

    // ═════════════════════════════════════════════
    // 캔들 완성 시 처리 (메인 루프)
    // ═════════════════════════════════════════════

    private async void OnCandleCompletedAsync(object? sender, Candle candle)
    {
        if (!_running) return;

        // 동시 처리 방지 (이전 캔들 처리 중 새 캔들 도착 시)
        bool entered = false;
        lock (_lock)
        {
            if (!_running) return;
            entered = true;
        }

        if (!entered) return;

        try
        {
            var currentPrice = candle.Close;

            switch (_position.Status)
            {
                // ── 포지션 없음 → GPT 분석 후 신규 진입 ──
                case PositionStatus.None:
                    await TryEnterNewPositionAsync(candle);
                    break;

                // ── 포지션 보유 중 → 익절/손절 체크 → 마틴 체크 ──
                case PositionStatus.Open:
                    // 미실현 손익 업데이트 (UI 실시간 반영)
                    _position.UpdateUnrealizedPnl(currentPrice, _config.Leverage);
                    OnPositionUpdated?.Invoke(this, _position);

                    if (_preOrderMode)
                    {
                        // 실거래(pre-orders): 익절/마틴은 서버가 처리. 봇은 손절만 감시.
                        await CheckStopLossOnlyAsync(currentPrice);
                    }
                    else
                    {
                        // 모의거래: 기존 로직 유지
                        await CheckExitConditionsAsync(currentPrice);
                        if (_position.Status == PositionStatus.Open)
                            await CheckMartinEntryAsync(currentPrice);
                    }
                    break;

                // ── 청산 완료 후 자동반복 대기 → 다음 사이클은 None에서 시작 ──
                case PositionStatus.Closed:
                    // 자동반복 모드에서 청산 후 자동 리셋은 ClosePositionAsync에서 처리
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "캔들 처리 중 오류");
            Log($"⚠️ 오류: {ex.Message}");
            await NotifyAsync($"⚠️ <b>오류 발생</b>\n{ex.Message}", NotificationType.Error);
        }
    }

    // ═════════════════════════════════════════════
    // 신규 진입 (GPT 분석 → 1단계 진입)
    // ═════════════════════════════════════════════

    private async Task TryEnterNewPositionAsync(Candle latestCandle)
    {
        TradeDirection direction;

        if (_gptAnalyzer != null)
        {
            // ── 적응형 간격 계산 ──
            // 연속 실패 횟수에 따라 간격 자동 확대
            var baseInterval = _config.GptAnalysisInterval > 0 ? _config.GptAnalysisInterval : 5;
            var effectiveInterval = GetAdaptiveInterval(baseInterval);

            // ── GPT 분석 간격 체크 ──
            var elapsed = DateTime.UtcNow - _lastGptAnalysisTime;
            if (_lastGptAnalysisTime != DateTime.MinValue && elapsed.TotalMinutes < effectiveInterval)
            {
                var remaining = (int)(effectiveInterval - elapsed.TotalMinutes) + 1;
                Log($"[GPT] 대기 중... ({remaining}분 후 재분석 | 현재 간격: {effectiveInterval}분" +
                    (_consecutiveSkipCount > 0 ? $" | {_consecutiveSkipCount}연속 스킵)" : ")"));
                return;
            }

            // ── GPT 분석 실행 ──
            var candles = await _data.GetRecentCandlesAsync(_config.GptCandleCount);
            Log($"[GPT] {_config.GptCandleCount}개 봉 분석 중... (모델: {_config.GptModel}" +
                (_consecutiveSkipCount > 0 ? $" | {_consecutiveSkipCount}연속 스킵 후 재시도)" : ")"));

            var result = await _gptAnalyzer.AnalyzeAsync(candles, _config.GptConfidenceThreshold);
            _lastGptAnalysisTime = DateTime.UtcNow;

            if (result.IsError)
            {
                _consecutiveSkipCount++;
                var nextInterval = GetAdaptiveInterval(baseInterval);
                Log($"⚠️ GPT 분석 실패 ({_consecutiveSkipCount}연속): {result.ErrorMessage}");
                Log($"[GPT] 다음 재시도: {nextInterval}분 후");
                await NotifyAsync($"⚠️ <b>GPT 분석 실패</b>\n{result.ErrorMessage}", NotificationType.Error);
                return;
            }

            if (!result.ShouldEnter(_config.GptConfidenceThreshold))
            {
                _consecutiveSkipCount++;
                var nextInterval = GetAdaptiveInterval(baseInterval);

                Log($"[GPT] 신뢰도 부족 ({_consecutiveSkipCount}연속): " +
                    $"{result.Confidence}% < {_config.GptConfidenceThreshold}%");
                Log($"[GPT] 이유: {result.Reason}");
                Log($"[GPT] 다음 재분석: {nextInterval}분 후" +
                    (nextInterval > baseInterval ? $" (간격 자동 확대: {baseInterval}→{nextInterval}분)" : ""));

                // 7번 이상 연속 실패 시 텔레그램 알림
                if (_consecutiveSkipCount == 7)
                {
                    await NotifyAsync(
                        $"⚠️ <b>GPT 신뢰도 부족 지속</b>\n" +
                        $"심볼: {_config.Symbol}\n" +
                        $"{_consecutiveSkipCount}번 연속 신뢰도 미달\n" +
                        $"현재 간격: {nextInterval}분으로 자동 조정됨\n" +
                        $"마지막 신뢰도: {result.Confidence}%",
                        NotificationType.Error);
                }
                return;
            }

            // ── 진입 확정 → 스킵 카운터 초기화 ──
            if (_consecutiveSkipCount > 0)
                Log($"[GPT] ✅ {_consecutiveSkipCount}번 스킵 후 진입 신호 포착");

            _consecutiveSkipCount = 0;
            direction = result.Direction;
            Log($"[GPT] ✅ {direction} | 신뢰도: {result.Confidence}% | {result.Reason}");
        }
        else
        {
            // ── 가격 방향 감지 모드 (GPT 미사용) ──
            direction = DetectDirectionByPrice(latestCandle.Close);
            if (direction == (TradeDirection)(-1))
            {
                // 기준가와 동일 — 다음 캔들 대기
                return;
            }
        }

        await EnterAsync(direction, latestCandle.Close, isFirstEntry: true);
    }

    /// <summary>
    /// GPT 미사용 시 가격 방향 감지
    /// 기준가(_priceAnchor) 대비 현재 캔들 종가 방향으로 Long/Short 결정
    /// </summary>
    private TradeDirection DetectDirectionByPrice(decimal currentPrice)
    {
        // 기준가 미설정 시 현재가를 기준가로 세팅 후 대기
        if (_priceAnchor <= 0)
        {
            _priceAnchor = currentPrice;
            Log($"[가격 방향 감지] 기준가 갱신: {_priceAnchor:N2} — 다음 캔들 대기");
            return (TradeDirection)(-1); // 대기 신호
        }

        var diff    = currentPrice - _priceAnchor;
        var diffPct = Math.Abs(diff) / _priceAnchor * 100;

        // 최소 변동폭 0.01% 미만이면 무시 (노이즈 방지)
        if (diffPct < 0.01m)
        {
            Log($"[가격 방향 감지] 변동 미미 ({diffPct:F4}%) — 대기");
            return (TradeDirection)(-1);
        }

        var dir = diff > 0 ? TradeDirection.Long : TradeDirection.Short;
        Log($"[가격 방향 감지] 기준가: {_priceAnchor:N2} → 현재가: {currentPrice:N2} " +
            $"({(diff > 0 ? "+" : "")}{diffPct:F3}%) → {dir} 진입");

        // 진입 후 기준가 초기화 (다음 사이클을 위해 청산 후 재설정)
        _priceAnchor = 0;
        return dir;
    }

    // ═════════════════════════════════════════════
    // 마틴게일 추가 진입 체크
    // ═════════════════════════════════════════════

    private async Task CheckMartinEntryAsync(decimal currentPrice)
    {
        if (_position.MartinStep >= _config.MartinCount)
            return; // 최대 단계 도달

        var gap          = _config.GetMartinGapForStep(_position.MartinStep + 1);
        var triggerPrice = _position.GetNextMartinTriggerPrice(gap);

        bool triggered = _position.Direction == TradeDirection.Long
            ? currentPrice <= triggerPrice
            : currentPrice >= triggerPrice;

        if (triggered)
        {
            Log($"🔁 마틴 {_position.MartinStep + 1}단계 트리거 | " +
                $"현재가: {currentPrice:N2} | 트리거: {triggerPrice:N2} | 간격: {gap}%");
            await EnterAsync(_position.Direction, currentPrice, isFirstEntry: false);
        }
    }

    // ═════════════════════════════════════════════
    // 익절 / 손절 체크
    // ═════════════════════════════════════════════

    private async Task CheckExitConditionsAsync(decimal currentPrice)
    {
        // 청산가 도달 체크 (모의거래 전용 — 실거래는 OKX 서버가 처리)
        var liqPrice = _executor.GetLiquidationPrice();
        if (liqPrice.HasValue)
        {
            var liquidated = _position.Direction == TradeDirection.Long
                ? currentPrice <= liqPrice.Value
                : currentPrice >= liqPrice.Value;

            if (liquidated)
            {
                var modeLabel = _config.MarginModeStr == "cross" ? "교차" : "격리";
                Log($"💥 강제청산 ({modeLabel} 마진) | 현재가: {currentPrice:N2} | 청산가: {liqPrice.Value:N2}");
                await ClosePositionAsync(currentPrice, isStopLoss: false, isForceClose: true);
                return;
            }
        }

        // 레버리지 포함 수익률(%) — targetProfit / StopLossPercent 와 동일 단위
        var pnlPct = _position.GetUnrealizedPnlPercent(currentPrice) * _config.Leverage;

        // 익절 체크
        var targetProfit = _config.GetTargetProfitForStep(_position.MartinStep);
        if (pnlPct >= targetProfit)
        {
            Log($"✅ 익절 조건 충족: {pnlPct:F2}% ≥ 목표 {targetProfit}%");
            await ClosePositionAsync(currentPrice, isStopLoss: false);
            return;
        }

        // 손절 체크 (활성화된 경우, 마지막 마틴 단계 이후에만)
        if (_config.StopLossEnabled
            && _position.MartinStep >= _config.MartinCount
            && pnlPct <= -_config.StopLossPercent)
        {
            Log($"🛑 손절 조건 충족: {pnlPct:F2}% ≤ 기준 -{_config.StopLossPercent}% (마틴 {_position.MartinStep}/{_config.MartinCount}단계 완료)");
            await ClosePositionAsync(currentPrice, isStopLoss: true);
        }
    }

    // ═════════════════════════════════════════════
    // 손절 전용 체크 (pre-orders 모드)
    // ═════════════════════════════════════════════

    private async Task CheckStopLossOnlyAsync(decimal currentPrice)
    {
        if (!_config.StopLossEnabled) return;
        if (_position.MartinStep < _config.MartinCount) return;

        // 레버리지 포함 수익률(%) — StopLossPercent 와 동일 단위
        var pnlPct = _position.GetUnrealizedPnlPercent(currentPrice) * _config.Leverage;
        if (pnlPct <= -_config.StopLossPercent)
        {
            Log($"🛑 [pre-orders] 손절 조건 충족: {pnlPct:F2}% ≤ -{_config.StopLossPercent}% (마틴 {_position.MartinStep}/{_config.MartinCount} 완료)");

            // 모든 algo 취소 후 시장가 청산
            await CancelAllPreOrdersAsync();
            await ClosePositionAsync(currentPrice, isStopLoss: true);
        }
    }

    // ═════════════════════════════════════════════
    // Pre-orders 등록 / 취소
    // ═════════════════════════════════════════════

    /// <summary>
    /// 1단계 시장가 진입 직후 호출 — 마틴 2~N단계 트리거 + TP conditional 일괄 등록
    /// </summary>
    private async Task RegisterPreOrdersAsync()
    {
        if (!_preOrderMode) return;

        Log($"📋 Pre-orders 등록 시작 — 마틴 2~{_config.MartinCount}단계 트리거 + 익절");

        // 1) 마틴 트리거 주문들 (현재 단계 = 1, 다음부터 N단계까지)
        decimal cumQty       = _position.TotalAmount;
        decimal cumAvgPrice  = _position.AvgEntryPrice;
        decimal lastEntryPx  = _position.LastEntryPrice;

        for (int step = _position.MartinStep + 1; step <= _config.MartinCount; step++)
        {
            var gap          = _config.GetMartinGapForStep(step);
            var triggerPrice = _position.Direction == TradeDirection.Long
                ? lastEntryPx * (1 - gap / 100)
                : lastEntryPx * (1 + gap / 100);

            var amount = _config.GetAmountForStep(step);

            var req = new TriggerOrderRequest
            {
                Symbol       = _config.Symbol,
                Direction    = _position.Direction,
                Amount       = amount,
                TriggerPrice = triggerPrice,
                MarginMode   = _config.MarginModeStr,
                Step         = step,
                IsClose      = false
            };

            try
            {
                var res = await _executor.PlaceTriggerOrderAsync(req);
                if (res.Success)
                {
                    _activeAlgoIds.Add(res.OrderId);
                    Log($"  ↳ 마틴{step}: {amount:F2} USDT @ trigger {triggerPrice:N4} (gap {gap}%) | algoId={res.OrderId}");
                }
                else
                {
                    Log($"  ⚠️ 마틴{step} 트리거 등록 실패: {res.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Log($"  ❌ 마틴{step} 트리거 예외: {ex.Message}");
            }

            // 다음 단계 lastEntryPx/avgPrice 추정 (실제 체결 시 갱신됨)
            cumAvgPrice = (cumQty * cumAvgPrice + amount * triggerPrice) / (cumQty + amount);
            cumQty     += amount;
            lastEntryPx = triggerPrice;
        }

        // 2) 익절 conditional (현재 평균가 기준)
        await RegisterTakeProfitAsync();
    }

    /// <summary>현재 평균가 + targetProfit% 기준 TP conditional 등록 (기존 TP 있으면 취소 후 재등록)</summary>
    private async Task RegisterTakeProfitAsync()
    {
        if (!_preOrderMode) return;
        if (_position.AvgEntryPrice <= 0) return;

        // 기존 TP 취소
        if (!string.IsNullOrEmpty(_activeTpAlgoId))
        {
            try
            {
                await _executor.CancelAlgoOrderAsync(_config.Symbol, _activeTpAlgoId);
                Log($"  🗑 기존 TP 취소: algoId={_activeTpAlgoId}");
            }
            catch (Exception ex) { Log($"  ⚠️ 기존 TP 취소 실패: {ex.Message}"); }
            _activeTpAlgoId = null;
        }

        var targetPct = _config.GetTargetProfitForStep(_position.MartinStep);
        // 목표 PnL%은 leverage 미적용 가격기준 → 가격 변동 = targetPct/leverage
        var priceMovePct = targetPct / _config.Leverage;
        var tpPrice = _position.Direction == TradeDirection.Long
            ? _position.AvgEntryPrice * (1 + priceMovePct / 100)
            : _position.AvgEntryPrice * (1 - priceMovePct / 100);

        try
        {
            var res = await _executor.PlaceTakeProfitOrderAsync(
                _config.Symbol, _position.Direction, tpPrice, _config.MarginModeStr);
            if (res.Success)
            {
                _activeTpAlgoId = res.OrderId;
                Log($"  🎯 TP 등록: 평균가 {_position.AvgEntryPrice:N4} → trigger {tpPrice:N4} (목표 {targetPct}% / 가격 {priceMovePct:F3}%) | algoId={res.OrderId}");
            }
            else
            {
                Log($"  ⚠️ TP 등록 실패: {res.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            Log($"  ❌ TP 등록 예외: {ex.Message}");
        }
    }

    /// <summary>등록된 모든 algo 취소 + 메모리 클리어</summary>
    private async Task CancelAllPreOrdersAsync()
    {
        if (!_preOrderMode) return;

        try
        {
            await _executor.CancelAllAlgoOrdersAsync(_config.Symbol);
            Log($"🧹 모든 pre-orders 취소 (트리거 {_activeAlgoIds.Count}건 + TP {(_activeTpAlgoId != null ? "1" : "0")}건)");
        }
        catch (Exception ex)
        {
            Log($"⚠️ algo 취소 실패: {ex.Message}");
        }
        _activeAlgoIds.Clear();
        _activeTpAlgoId = null;
    }

    // ═════════════════════════════════════════════
    // Private WS → Algo 체결 알림 핸들러
    // ═════════════════════════════════════════════

    private async void OnAlgoOrderFilledHandler(object? sender, AlgoOrderFillEvent e)
    {
        if (!_running) return;

        try
        {
            // 1) 익절(reduceOnly) 체결 → 청산 처리
            if (e.IsClose)
            {
                Log($"🎯 [pre-orders] 익절 체결 수신: {e.Direction} @ {e.FilledPrice:N4} (size={e.FilledSize}, algoId={e.AlgoId})");
                await HandleTpFillAsync(e.FilledPrice);
                return;
            }

            // 2) 마틴 추가 진입 체결 → 포지션 업데이트
            Log($"➕ [pre-orders] 마틴 트리거 체결 수신: {e.Direction} @ {e.FilledPrice:N4} (size={e.FilledSize}, algoId={e.AlgoId})");
            await HandleMartinFillAsync(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TradingCore] AlgoFill 처리 중 오류");
            Log($"⚠️ AlgoFill 처리 오류: {ex.Message}");
        }
    }

    private async Task HandleMartinFillAsync(AlgoOrderFillEvent e)
    {
        if (_position.Status != PositionStatus.Open)
        {
            Log($"⚠️ 포지션 미오픈 상태에서 마틴 체결 수신 (무시): algoId={e.AlgoId}");
            return;
        }

        // 체결가 기반 평균가 재계산
        // 명목 USDT 금액 추정: FilledSize × FilledPrice (계약수 기반이면 정확하지 않을 수 있음 → 보수적 처리)
        var notional = e.NotionalUsd > 0 ? e.NotionalUsd : (e.FilledSize * e.FilledPrice);

        var prevTotal = _position.TotalAmount;
        _position.MartinStep++;
        _position.TotalAmount   += notional;
        _position.AvgEntryPrice  = (prevTotal * _position.AvgEntryPrice + notional * e.FilledPrice)
                                   / _position.TotalAmount;
        _position.LastEntryPrice = e.FilledPrice;

        // algoId 추적 정리
        _activeAlgoIds.Remove(e.AlgoId);

        var msg = $"➕ 마틴 {_position.MartinStep}단계 체결 [{_position.Direction}] | " +
                  $"{notional:F2} USDT @ {e.FilledPrice:N4} | " +
                  $"평균가: {_position.AvgEntryPrice:N4} | " +
                  $"누적: {_position.TotalAmount:F2} USDT";
        Log(msg);

        await NotifyAsync(
            $"➕ <b>마틴 {_position.MartinStep}단계 (서버 트리거)</b>\n" +
            $"심볼: {_config.Symbol}\n" +
            $"진입가: {e.FilledPrice:N4}\n" +
            $"평균가: {_position.AvgEntryPrice:N4}\n" +
            $"누적금: {_position.TotalAmount:F2} USDT\n" +
            $"단계: {_position.MartinStep}/{_config.MartinCount}",
            NotificationType.Martin);

        OnPositionUpdated?.Invoke(this, _position);

        // 평균가 변경 → TP 재등록
        await RegisterTakeProfitAsync();
    }

    private async Task HandleTpFillAsync(decimal exitPrice)
    {
        if (_position.Status != PositionStatus.Open)
        {
            Log("⚠️ 포지션 미오픈 상태에서 TP 체결 수신 (무시)");
            return;
        }

        // 잔여 마틴 트리거 모두 취소
        await CancelAllPreOrdersAsync();

        // 익절 청산 처리 (실제 청산은 이미 서버가 했음 → ClosePositionAsync 의 시장가 호출 스킵하고 상태만 갱신)
        await FinalizeClosedFromServerAsync(exitPrice, isStopLoss: false);
    }

    /// <summary>서버가 이미 청산한 상태에서 메모리/이벤트만 마무리</summary>
    private async Task FinalizeClosedFromServerAsync(decimal exitPrice, bool isStopLoss)
    {
        var pricePct = _position.GetUnrealizedPnlPercent(exitPrice);
        var pnlPct   = pricePct * _config.Leverage;                      // 레버리지 포함 수익률 (로그·이벤트용)
        var pnlAmt   = _position.TotalAmount * pricePct / 100 * _config.Leverage;

        _position.Status      = PositionStatus.Closed;
        _position.ClosedAt    = DateTime.UtcNow;
        _position.RealizedPnl = pnlAmt;

        _cycleCount++;
        _sessionPnl += pnlAmt;

        OnTradeClosed?.Invoke(this, new TradeClosedEventArgs
        {
            Symbol        = _config.Symbol,
            Direction     = _position.Direction,
            AvgEntryPrice = _position.AvgEntryPrice,
            ExitPrice     = exitPrice,
            TotalAmount   = _position.TotalAmount,
            MartinStep    = _position.MartinStep,
            MartinMax     = _config.MartinCount,
            PnlPercent    = pnlPct,
            PnlAmount     = pnlAmt,
            IsStopLoss    = isStopLoss,
            Leverage      = _config.Leverage,
            OpenedAt      = _position.OpenedAt,
            ClosedAt      = _position.ClosedAt ?? DateTime.UtcNow
        });

        var emoji     = isStopLoss ? "🛑" : "✅";
        var typeLabel = isStopLoss ? "손절" : "익절";
        Log($"{emoji} [pre-orders] {typeLabel} 청산 완료 | {pnlPct:+0.00;-0.00}% ({pnlAmt:+0.00;-0.00} USDT) | " +
            $"마틴 {_position.MartinStep}/{_config.MartinCount} | 사이클 #{_cycleCount} | 세션: {_sessionPnl:+0.00;-0.00}");

        await NotifyAsync(
            $"{emoji} <b>{typeLabel} 청산 (서버)</b>\n" +
            $"심볼: {_config.Symbol}\n" +
            $"수익률: {pnlPct:+0.00;-0.00}%\n" +
            $"손익: {pnlAmt:+0.00;-0.00} USDT\n" +
            $"마틴: {_position.MartinStep}/{_config.MartinCount}\n" +
            $"사이클: #{_cycleCount}\n" +
            $"세션 누적: {_sessionPnl:+0.00;-0.00} USDT",
            isStopLoss ? NotificationType.StopLoss : NotificationType.TakeProfit);

        OnPositionUpdated?.Invoke(this, _position);

        // 자동반복
        if (_autoRepeat && _running)
        {
            if (_gptAnalyzer == null)
            {
                _priceAnchor = exitPrice;
                Log($"🔄 자동반복: 기준가 → {_priceAnchor:N2} | 다음 캔들 방향 감지 대기...");
            }
            else
            {
                Log("🔄 자동반복: 다음 캔들에서 GPT 분석 시작...");
            }
            _consecutiveSkipCount = 0;
            _position = new Position();
        }
        else
        {
            Log(_autoRepeat ? "⏸ 매매 종료" : "⏸ 자동반복 OFF — 매매 종료");
            _running = false;
        }
    }

    // ═════════════════════════════════════════════
    // 진입 실행
    // ═════════════════════════════════════════════

    private async Task EnterAsync(TradeDirection direction, decimal price, bool isFirstEntry)
    {
        var currentStep = isFirstEntry ? 1 : (_position!.MartinStep + 1);
        var amount = _config.GetAmountForStep(currentStep);

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
            await NotifyAsync($"❌ <b>주문 실패</b>\n{result.ErrorMessage}", NotificationType.Error);
            return;
        }

        // 체결가: OrderResult에서 반환된 값 우선, 없으면 캔들 Close
        var filledPrice = result.FilledPrice > 0 ? result.FilledPrice : price;

        // 포지션 업데이트
        if (isFirstEntry)
        {
            _position = new Position
            {
                Direction      = direction,
                Status         = PositionStatus.Open,
                MartinStep     = 1,
                TotalAmount    = amount,
                AvgEntryPrice  = filledPrice,
                LastEntryPrice = filledPrice,
                OpenedAt       = DateTime.UtcNow
            };

            var liqInfo = FormatLiquidationLog();
            var msg = $"📈 신규 진입 [{direction}] | " +
                      $"{amount:F2} USDT @ {filledPrice:N2} | " +
                      $"1/{_config.MartinCount}단계{liqInfo}";
            Log(msg);
            await NotifyAsync(
                $"📈 <b>신규 진입</b>\n" +
                $"심볼: {_config.Symbol}\n" +
                $"방향: {direction}\n" +
                $"금액: {amount:F2} USDT\n" +
                $"진입가: {filledPrice:N2}\n" +
                $"단계: 1/{_config.MartinCount}",
                NotificationType.Entry);

            // ── Pre-orders 모드: 마틴 트리거 + TP 일괄 등록 ──
            if (_preOrderMode)
            {
                await RegisterPreOrdersAsync();
            }
        }
        else
        {
            // 가중 평균 진입가 재계산
            var prevTotal = _position.TotalAmount;
            _position.MartinStep++;
            _position.TotalAmount   += amount;
            _position.AvgEntryPrice  = (prevTotal * _position.AvgEntryPrice + amount * filledPrice)
                                       / _position.TotalAmount;
            _position.LastEntryPrice = filledPrice;

            var liqInfo = FormatLiquidationLog();
            var msg = $"➕ 마틴 {_position.MartinStep}단계 [{direction}] | " +
                      $"{amount:F2} USDT @ {filledPrice:N2} | " +
                      $"평균가: {_position.AvgEntryPrice:N2} | " +
                      $"누적: {_position.TotalAmount:F2} USDT{liqInfo}";
            Log(msg);
            await NotifyAsync(
                $"➕ <b>마틴 {_position.MartinStep}단계</b>\n" +
                $"심볼: {_config.Symbol}\n" +
                $"진입가: {filledPrice:N2}\n" +
                $"평균가: {_position.AvgEntryPrice:N2}\n" +
                $"누적금: {_position.TotalAmount:F2} USDT\n" +
                $"단계: {_position.MartinStep}/{_config.MartinCount}",
                NotificationType.Martin);
        }

        OnPositionUpdated?.Invoke(this, _position);
    }

    private string FormatLiquidationLog()
    {
        var liq = _executor.GetLiquidationPrice();
        if (liq == null) return "";
        var modeLabel = _config.MarginModeStr == "cross" ? "교차" : "격리";
        return $" | 청산가: {liq.Value:N2} ({modeLabel})";
    }

    // ═════════════════════════════════════════════
    // 청산 실행
    // ═════════════════════════════════════════════

    private async Task ClosePositionAsync(decimal currentPrice, bool isStopLoss, bool isForceClose = false)
    {
        // Pre-orders 모드: 잔여 algo 먼저 취소
        if (_preOrderMode)
            await CancelAllPreOrdersAsync();

        var result = await _executor.ClosePositionAsync(_config.Symbol, _position.Direction);

        if (!result.Success)
        {
            Log($"❌ 청산 실패: {result.ErrorMessage}");
            await NotifyAsync($"❌ <b>청산 실패</b>\n{result.ErrorMessage}", NotificationType.Error);
            return;
        }

        // 체결가: OrderResult 우선, 없으면 currentPrice
        var exitPrice = result.FilledPrice > 0 ? result.FilledPrice : currentPrice;

        var pricePct = _position.GetUnrealizedPnlPercent(exitPrice);
        var pnlPct   = pricePct * _config.Leverage;                      // 레버리지 포함 수익률 (로그·이벤트용)
        var pnlAmt   = _position.TotalAmount * pricePct / 100 * _config.Leverage;

        _position.Status      = PositionStatus.Closed;
        _position.ClosedAt    = DateTime.UtcNow;
        _position.RealizedPnl = pnlAmt;

        _cycleCount++;
        _sessionPnl += pnlAmt;

        // 거래 완료 이벤트 발행
        OnTradeClosed?.Invoke(this, new TradeClosedEventArgs
        {
            Symbol        = _config.Symbol,
            Direction     = _position.Direction,
            AvgEntryPrice = _position.AvgEntryPrice,
            ExitPrice     = exitPrice,
            TotalAmount   = _position.TotalAmount,
            MartinStep    = _position.MartinStep,
            MartinMax     = _config.MartinCount,
            PnlPercent    = pnlPct,
            PnlAmount     = pnlAmt,
            IsStopLoss    = isStopLoss,
            Leverage      = _config.Leverage,
            OpenedAt      = _position.OpenedAt,
            ClosedAt      = _position.ClosedAt ?? DateTime.UtcNow
        });

        // 로그 + 알림
        var emoji     = isStopLoss ? "🛑" : isForceClose ? "🔴" : "✅";
        var typeLabel = isStopLoss ? "손절" : isForceClose ? "강제청산" : "익절";
        var logMsg    = $"{emoji} {typeLabel} | {pnlPct:+0.00;-0.00}% ({pnlAmt:+0.00;-0.00} USDT) | " +
                        $"마틴 {_position.MartinStep}/{_config.MartinCount} | " +
                        $"사이클 #{_cycleCount} | 세션 누적: {_sessionPnl:+0.00;-0.00} USDT";
        Log(logMsg);

        var notifyType = isStopLoss ? NotificationType.StopLoss : NotificationType.TakeProfit;
        await NotifyAsync(
            $"{emoji} <b>{typeLabel} 청산</b>\n" +
            $"심볼: {_config.Symbol}\n" +
            $"수익률: {pnlPct:+0.00;-0.00}%\n" +
            $"손익: {pnlAmt:+0.00;-0.00} USDT\n" +
            $"마틴: {_position.MartinStep}/{_config.MartinCount}\n" +
            $"사이클: #{_cycleCount}\n" +
            $"세션 누적: {_sessionPnl:+0.00;-0.00} USDT",
            notifyType);

        OnPositionUpdated?.Invoke(this, _position);

        // ── 사이클 관리: 자동반복 ──
        if (_autoRepeat && _running && !isForceClose)
        {
            // GPT 미사용 모드: 청산 가격을 다음 사이클 기준가로 설정
            if (_gptAnalyzer == null)
            {
                _priceAnchor = exitPrice;
                Log($"🔄 자동반복: 기준가 → {_priceAnchor:N2} | 다음 캔들 방향 감지 대기...");
            }
            else
            {
                Log("🔄 자동반복: 다음 캔들에서 GPT 분석 시작...");
            }
            _consecutiveSkipCount = 0;
            _position = new Position(); // 상태 초기화 → 다음 캔들에서 None으로 진입 시도
        }
        else
        {
            if (isForceClose)
                Log("🔴 강제 청산으로 사이클 종료");
            else if (!_autoRepeat)
                Log("⏸ 자동반복 OFF — 매매 종료");

            _running = false;
        }
    }

    // ═════════════════════════════════════════════
    // 재시작 동기화
    // ═════════════════════════════════════════════

    /// <summary>
    /// 봇 시작 시 거래소 실제 포지션을 조회해 인메모리 상태를 복원.
    /// 포지션이 있으면 true 반환 (algo 주문 취소 스킵), 없으면 false.
    /// </summary>
    private async Task<bool> TrySyncPositionFromExchangeAsync()
    {
        try
        {
            var pos = await _executor.GetPositionAsync(_config.Symbol);
            if (pos == null)
            {
                Log("🔍 재시작 동기화: 거래소에 열린 포지션 없음 — 새 사이클 시작");
                return false;
            }

            // 미체결 algo 주문 조회
            var algoOrders   = await _executor.GetOpenAlgoOrdersAsync(_config.Symbol);
            var triggerOrders = algoOrders.Where(a => !a.IsClose).ToList();
            var tpOrder       = algoOrders.FirstOrDefault(a => a.IsClose);

            // 마틴 단계 추정: 전체 마틴 수 - 남은 트리거 수
            var martinStep = Math.Max(1, _config.MartinCount - triggerOrders.Count);

            _position = new Position
            {
                Direction      = pos.Direction,
                Status         = PositionStatus.Open,
                MartinStep     = martinStep,
                TotalAmount    = pos.NotionalUsd,
                TotalQuantity  = pos.TotalQuantity,
                AvgEntryPrice  = pos.AvgEntryPrice,
                LastEntryPrice = pos.AvgEntryPrice,
                OpenedAt       = pos.OpenedAt
            };

            _activeAlgoIds.Clear();
            foreach (var t in triggerOrders)
                _activeAlgoIds.Add(t.AlgoId);

            _activeTpAlgoId = tpOrder?.AlgoId;

            var logMsg = $"♻️ 재시작 동기화 완료 | {pos.Direction} 포지션 복원 | " +
                         $"평균가: {pos.AvgEntryPrice:N4} | 마틴 {martinStep}/{_config.MartinCount}단계 | " +
                         $"트리거: {triggerOrders.Count}건 | TP: {(_activeTpAlgoId != null ? "유지" : "없음")}";
            Log(logMsg);

            await NotifyAsync(
                $"♻️ <b>재시작 동기화</b>\n" +
                $"심볼: {_config.Symbol}\n" +
                $"방향: {pos.Direction}\n" +
                $"평균가: {pos.AvgEntryPrice:N4}\n" +
                $"마틴: {martinStep}/{_config.MartinCount}단계\n" +
                $"잔여 트리거: {triggerOrders.Count}건\n" +
                $"익절 주문: {(_activeTpAlgoId != null ? "유지" : "없음")}",
                NotificationType.BotStartStop);

            OnPositionUpdated?.Invoke(this, _position);
            return true;
        }
        catch (Exception ex)
        {
            Log($"⚠️ 재시작 동기화 실패: {ex.Message} — 잔여 주문 정리 후 새 사이클 시작");
            return false;
        }
    }

    // ═════════════════════════════════════════════
    // 헬퍼
    // ═════════════════════════════════════════════

    /// <summary>
    /// 연속 스킵 횟수에 따른 적응형 GPT 분석 간격 반환
    /// 1~3회: 기본 간격 유지
    /// 4~6회: 기본 × 2 (비용 절감 + 시장 관망)
    /// 7회 이상: 기본 × 3 (과열 방지 + 알림 발송)
    /// </summary>
    private int GetAdaptiveInterval(int baseInterval)
    {
        return _consecutiveSkipCount switch
        {
            <= 3 => baseInterval,
            <= 6 => baseInterval * 2,
            _    => baseInterval * 3
        };
    }

    private void Log(string message)
    {
        var timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logger.LogInformation(message);
        OnLogMessage?.Invoke(this, timestamped);
        _logFileSink?.Invoke(timestamped); // 파일 저장
    }

    private async Task NotifyAsync(string message, NotificationType type)
    {
        if (_notifier != null)
        {
            try { await _notifier.SendAsync(message, type); }
            catch (Exception ex) { _logger.LogWarning("텔레그램 알림 실패: {msg}", ex.Message); }
        }
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
