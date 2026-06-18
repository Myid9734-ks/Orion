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
    private readonly Action<string>? _logFileSink;           // 로그 파일 저장용 콜백
    private Func<string, DateTime, int>? _getDbTradeCount;   // DB 기록 건수 조회 콜백 (누락 감지용)

    private decimal _takerFeeRate = 0.0005m; // 시작 시 OKX API로 조회
    private decimal _makerFeeRate = 0.0002m; // 시작 시 OKX API로 조회

    private Position _position = new();
    private bool     _running  = false;
    private bool     _autoRepeat = false;
    private int      _cycleCount = 0;        // 완료된 사이클 수
    private decimal  _sessionPnl = 0;        // 세션 누적 손익
    private decimal  _trackedBalance = 0;    // 사이클간 잔고 추적용
    private readonly object _lock = new();              // 경량 상태 보호용 (running 플래그 등)
    private readonly SemaphoreSlim _candleSem = new(1, 1); // 캔들 처리 직렬화 — 이중 진입 방지

    // ── Pre-orders 모드 (실거래) 전용 ─────────────
    private readonly List<string> _activeAlgoIds = new(); // 등록된 마틴 트리거 algoId
    private string?  _activeTpAlgoId;                     // 익절 conditional algoId
    private bool     _preOrderMode;                       // _executor.SupportsServerSidePreOrders 캐시

    // ── 지정가 청산 watchdog (5초×2회 정정 → 시장가 fallback) ──
    private CancellationTokenSource? _watchdogCts;
    private Task?                    _watchdogTask;
    private readonly Dictionary<string, (DateTime FirstSeen, int AmendCount)> _exitOrderTracker = new();

    // 가격 방향 감지 모드 (GPT 미사용 시)
    private decimal  _priceAnchor = 0;       // 기준가 (봇 시작 시 또는 청산 후 재진입 대기 시점 가격)

    // 손절 주문 추적
    private string? _stopLossOrderId = null;

    // 블로그 기록용
    private string  _blogGptDirection   = "";
    private int     _blogGptConfidence  = 0;
    private string  _blogGptReason      = "";
    private Candle? _blogEntryCandle    = null;

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

    /// <summary>봇 완전 종료 — 자동반복 OFF 사이클 완료 시 발사</summary>
    public event EventHandler? OnBotStopped;

    // ─────────────────────────────────────────────
    // 읽기 전용 상태
    // ─────────────────────────────────────────────

    public Position CurrentPosition => _position;
    public bool     IsRunning       => _running;
    public int      CycleCount      => _cycleCount;
    public decimal  SessionPnl      => _sessionPnl;

    // USD → KRW 변환 (환율 0이면 빈 문자열)
    private string Krw(decimal usd) =>
        _config.UsdKrwRate > 0 ? $" (≈₩{usd * _config.UsdKrwRate:+#,##0;-#,##0})" : "";

    private string KrwAbs(decimal usd) =>
        _config.UsdKrwRate > 0 ? $" (≈₩{usd * _config.UsdKrwRate:#,##0})" : "";

    private void LogBlogSection(decimal exitPrice, decimal pnlPct, decimal pnlAmt, decimal fee, decimal pnlNet, bool isStopLoss)
    {
        if (_logFileSink == null || _position == null) return;
        var kst = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Korea Standard Time" : "Asia/Seoul");
        string T(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, kst).ToString("MM-dd HH:mm:ss");

        var openedKst  = TimeZoneInfo.ConvertTimeFromUtc(_position.OpenedAt, kst);
        var closedKst  = TimeZoneInfo.ConvertTimeFromUtc(_position.ClosedAt ?? DateTime.UtcNow, kst);
        var duration   = closedKst - openedKst;
        var durStr     = duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}시간 {duration.Minutes}분 {duration.Seconds}초"
            : $"{duration.Minutes}분 {duration.Seconds}초";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine($"📝 블로그 매매기록 — 사이클 #{_cycleCount}");
        sb.AppendLine("═══════════════════════════════════════════════════");
        sb.AppendLine($"[거래 개요]");
        sb.AppendLine($"심볼: {_config.Symbol} | 방향: {_position.Direction} | 레버리지: {_config.Leverage}x ({_config.MarginModeStr})");
        sb.AppendLine($"시작: {T(_position.OpenedAt)} | 종료: {T(_position.ClosedAt ?? DateTime.UtcNow)} | 소요: {durStr}");

        if (!string.IsNullOrEmpty(_blogGptDirection))
        {
            sb.AppendLine();
            sb.AppendLine($"[GPT 진입 신호]");
            sb.AppendLine($"방향: {_blogGptDirection} | 신뢰도: {_blogGptConfidence}% | 근거: {_blogGptReason}");
        }

        if (_blogEntryCandle != null)
        {
            sb.AppendLine();
            sb.AppendLine($"[진입 시 시장 상황]");
            sb.AppendLine($"진입봉: O:{_blogEntryCandle.Open:N2} H:{_blogEntryCandle.High:N2} L:{_blogEntryCandle.Low:N2} C:{_blogEntryCandle.Close:N2} V:{_blogEntryCandle.Volume:N0}");
        }

        if (_position.StageEntries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"[단계별 진입 내역]");
            foreach (var e in _position.StageEntries)
                sb.AppendLine($"  {e.Step}단계: {e.Price:N2} @ {e.Time:HH:mm:ss} ({e.Amount:F2} USDT)");
            sb.AppendLine($"  평균 진입가: {_position.AvgEntryPrice:N2}");
        }

        sb.AppendLine();
        sb.AppendLine($"[청산 결과]");
        sb.AppendLine($"결과: {(isStopLoss ? "🛑 손절" : "✅ 익절")} | 청산가: {exitPrice:N2}");
        sb.AppendLine($"마틴: {_position.MartinStep}/{_config.MartinCount}단계 | 총 투자금: {_position.TotalAmount:F4} USDT");
        sb.AppendLine($"수익률: {pnlPct:+0.00;-0.00}%");
        sb.AppendLine($"손익(gross): {pnlAmt:+0.0000;-0.0000} USDT{Krw(pnlAmt)}");
        sb.AppendLine($"수수료: -{fee:F4} USDT");
        sb.AppendLine($"순손익: {pnlNet:+0.0000;-0.0000} USDT{Krw(pnlNet)}");
        sb.AppendLine($"세션 누적: {_sessionPnl:+0.0000;-0.0000} USDT{Krw(_sessionPnl)}");
        sb.AppendLine("═══════════════════════════════════════════════════");

        _logFileSink(sb.ToString());
    }

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
        Action<string>? logFileSink = null,
        Func<string, DateTime, int>? getDbTradeCount = null)
    {
        _data         = dataProvider;
        _executor     = orderExecutor;
        _config       = config;
        _logger       = logger;
        _notifier     = notifier;
        _gptAnalyzer      = gptAnalyzer;
        _logFileSink      = logFileSink;
        _getDbTradeCount  = getDbTradeCount;
        _notifyConfig = notificationConfig ?? new NotificationConfig { Enabled = false };

        _data.OnCandleCompleted += OnCandleCompletedAsync;

        // 실거래(pre-orders) 모드: algo 체결 이벤트 구독
        _preOrderMode = _executor.SupportsServerSidePreOrders;
        if (_preOrderMode)
        {
            _executor.OnAlgoOrderFilled += OnAlgoOrderFilledHandler;
            _executor.OnStreamReconnected += OnStreamReconnectedHandler;
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

        // 수수료율 조회 (진입=Maker, 청산=Taker)
        (_takerFeeRate, _makerFeeRate) = await _executor.GetFeeRatesAsync(_config.Symbol);

        // 레버리지 설정 (잔여 algo 주문이 있으면 OKX가 거부할 수 있음 → 이후 재시도)
        var leverageOk = await _executor.SetLeverageAsync(
            _config.Symbol, _config.Leverage, _config.MarginModeStr);

        if (!leverageOk)
            Log("⚠️ 레버리지 설정 실패 — 잔여 algo 주문 정리 후 재시도 예정");

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

                    // algo 주문 정리 후 레버리지 재시도
                    if (!leverageOk)
                    {
                        leverageOk = await _executor.SetLeverageAsync(
                            _config.Symbol, _config.Leverage, _config.MarginModeStr);
                        Log(leverageOk
                            ? "✅ 레버리지 재설정 성공"
                            : "⚠️ 레버리지 재설정 실패 — 기존 설정값으로 진행");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Private WS 시작 실패: {ex.Message} — 캔들 폴링 폴백");
                _preOrderMode = false;
            }
        }

        var balance = await _executor.GetBalanceAsync();
        _trackedBalance = balance;

        // ── OKX 최소 계약 기반 마틴 단계 동적 설정 ──
        var originalMaxSteps = _config.MartinCount;
        try
        {
            decimal initPrice = 0;
            try { initPrice = await _data.GetCurrentPriceAsync(); } catch { }
            if (initPrice > 0)
            {
                var minNotional = await _executor.GetMinStepNotionalAsync(_config.Symbol, initPrice);
                if (minNotional > 0)
                {
                    _config.SetDynamicSteps(minNotional, originalMaxSteps);
                    var dynAmounts = _config.GetAllStepAmounts();
                    Log($"📐 마틴 동적 설정 완료: 최소명목={minNotional:F2} USDT | " +
                        $"예산={_config.TotalBudget:F2} USDT | {_config.MartinCount}단계 확정 " +
                        $"(요청 {originalMaxSteps}단계)");
                    Log($"   단계별 명목금액: {string.Join(", ", dynAmounts.Select((a, i) => $"{i + 1}:{a:F2}"))}");
                }
                else
                    Log("⚠️ 최소 명목금액 조회 실패 — 기존 설정 유지");
            }
            else
                Log("⚠️ 현재가 조회 실패 — 최소 계약 기반 설정 스킵");
        }
        catch (Exception ex)
        {
            Log($"⚠️ 마틴 동적 설정 예외: {ex.Message} — 기존 설정 유지");
        }

        // ── 단계별 금액 / 간격 / 익절 요약 ──
        var amounts     = _config.GetAllStepAmounts();
        var amountStr   = string.Join(", ", amounts.Select((a, i) => $"{i + 1}:{a:F2}"));
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
        Log($"   진입간격: {gapStr} | 익절: {tpStr} | 손절: {slStr} | 수수료: Maker {_makerFeeRate * 100:F4}% / Taker {_takerFeeRate * 100:F4}%");
        Log($"   GPT: {gptStr}");

        await NotifyAsync(
            $"🚀 <b>봇 시작</b>\n" +
            $"심볼: {_config.Symbol}\n" +
            $"잔고: {balance:N2} USDT{KrwAbs(balance)}\n" +
            $"레버리지: {_config.Leverage}x ({_config.MarginModeStr})\n" +
            $"예산: {_config.TotalBudget:F2} USDT{KrwAbs(_config.TotalBudget)}\n" +
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
                Log($"[가격 방향 감지] 기준가 설정: {Px(_priceAnchor)} — 다음 캔들 방향으로 진입");
            }
            catch { _priceAnchor = 0; }
        }

        await _data.StartAsync(ct);
        Log("첫 캔들 완성 대기 중...");

        // 지정가 청산 watchdog 시작 (실거래 모드만)
        if (_preOrderMode) StartExitWatchdog(ct);
    }

    /// <summary>
    /// 현재 사이클 완료 후 종료 — 자동반복 OFF와 동일.
    /// 포지션이 없으면 즉시 종료, 있으면 익절/손절 체결까지 대기 후 자동 종료.
    /// </summary>
    public Task StopAsync()
    {
        _autoRepeat = false;

        if (_position == null || _position.Status != PositionStatus.Open)
        {
            Log($"⏹ 중지 — 열린 포지션 없음, 즉시 종료 | 완료 사이클: {_cycleCount}회 | 세션 손익: {_sessionPnl:+0.0000;-0.0000;0.0000} USDT");
            return Task.CompletedTask;
        }

        Log($"⏹ 중지 예약 — 현재 포지션 익절/손절 후 자동 종료 | 완료 사이클: {_cycleCount}회 | 세션 손익: {_sessionPnl:+0.0000;-0.0000;0.0000} USDT");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 라이센스 만료 시 호출 — 신규 진입 차단, 현재 포지션은 익절/손절까지 유지.
    /// </summary>
    public Task BlockNewEntryAsync()
    {
        _autoRepeat = false;
        Log("🔒 라이센스 만료 — 신규 진입 차단. 현재 포지션 익절/손절 후 자동 종료.");
        return Task.CompletedTask;
    }

    // ═════════════════════════════════════════════
    // 지정가 청산 watchdog
    //   - reduceOnly limit 미체결 5초마다 현재가로 정정
    //   - 2회 정정해도 미체결이면 취소 후 시장가 청산 (총 ~15초)
    //   - 대상: TP가 trigger 후 spawn한 limit, SL로 직접 건 limit
    // ═════════════════════════════════════════════
    private void StartExitWatchdog(CancellationToken parent)
    {
        if (_watchdogTask != null && !_watchdogTask.IsCompleted) return;
        _watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _exitOrderTracker.Clear();
        _watchdogTask = Task.Run(() => ExitWatchdogLoopAsync(_watchdogCts.Token));
        Log("👁 지정가 청산 watchdog 시작 (5초×2 정정 → 시장가 fallback)");
    }

    private async Task StopExitWatchdog()
    {
        try { _watchdogCts?.Cancel(); } catch { }
        if (_watchdogTask != null)
        {
            try { await _watchdogTask; } catch { }
        }
        _watchdogTask = null;
        _watchdogCts  = null;
        _exitOrderTracker.Clear();
    }

    private async Task ExitWatchdogLoopAsync(CancellationToken ct)
    {
        const int   PollMs        = 1000;
        const double AmendAfterSec = 5.0;
        const int   MaxAmends     = 2;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollMs, ct);
                if (_position == null || _position.Status != PositionStatus.Open)
                {
                    if (_exitOrderTracker.Count > 0) _exitOrderTracker.Clear();
                    continue;
                }

                List<PendingOrderInfo> pendings;
                try { pendings = await _executor.GetPendingOrdersAsync(_config.Symbol); }
                catch (Exception ex) { Log($"⚠️ watchdog 조회 실패: {ex.Message}"); continue; }

                var exits = pendings.Where(p => p.ReduceOnly && p.OrdType == "limit").ToList();
                if (exits.Count == 0)
                {
                    if (_exitOrderTracker.Count > 0) _exitOrderTracker.Clear();
                    continue;
                }

                var now     = DateTime.UtcNow;
                var seenIds = new HashSet<string>();

                foreach (var ord in exits)
                {
                    seenIds.Add(ord.OrderId);
                    if (!_exitOrderTracker.TryGetValue(ord.OrderId, out var info))
                    {
                        info = (now, 0);
                        _exitOrderTracker[ord.OrderId] = info;
                        Log($"👁 청산 limit 추적 시작: ordId={ord.OrderId} px={ord.Price}");
                        continue;
                    }

                    var elapsed = (now - info.FirstSeen).TotalSeconds;
                    if (elapsed < AmendAfterSec) continue;

                    if (info.AmendCount < MaxAmends)
                    {
                        // 현재가로 정정
                        decimal currentPx;
                        try { currentPx = await _data.GetCurrentPriceAsync(); }
                        catch { continue; }
                        if (currentPx <= 0) continue;

                        try
                        {
                            var ok = await _executor.AmendOrderAsync(_config.Symbol, ord.OrderId, currentPx);
                            if (ok)
                            {
                                Log($"⏰ 청산 limit 미체결 {elapsed:F0}s → 정정 #{info.AmendCount + 1}: {ord.Price} → {currentPx:N6}");
                                _exitOrderTracker[ord.OrderId] = (now, info.AmendCount + 1);
                            }
                            else
                            {
                                Log($"⚠️ 정정 실패 (응답 not ok): ordId={ord.OrderId} — 다음 tick에 재시도");
                            }
                        }
                        catch (Exception ex) { Log($"⚠️ 정정 예외: {ex.Message}"); }
                    }
                    else
                    {
                        // 시장가 fallback
                        Log($"⏰ 청산 limit {info.AmendCount}회 정정 후도 미체결 ({elapsed:F0}s) → 취소 후 시장가 청산: ordId={ord.OrderId}");
                        try { await _executor.CancelOrderAsync(_config.Symbol, ord.OrderId); } catch (Exception ex) { Log($"⚠️ limit 취소 실패: {ex.Message}"); }
                        try { await _executor.ClosePositionAsync(_config.Symbol, _position.Direction); }
                        catch (Exception ex) { Log($"❌ 시장가 청산 예외: {ex.Message}"); }
                        _exitOrderTracker.Remove(ord.OrderId);
                    }
                }

                // 사라진 ord 정리 (체결/취소됨)
                var stale = _exitOrderTracker.Keys.Where(k => !seenIds.Contains(k)).ToList();
                foreach (var k in stale) _exitOrderTracker.Remove(k);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"⚠️ watchdog 루프 예외: {ex.Message}"); }
        }
    }

    /// <summary>예비주문/익절주문 취소 후 봇 중지 — 포지션은 유지 (매매감지 X)</summary>
    public async Task ForceCloseAsync()
    {
        Log("🔴 포지션 강제 종료 요청 — 예비/익절 주문 취소 + 즉시 시장가 청산");

        // 1) 예비주문 / 익절주문 일괄 취소
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
        }

        // 2) 포지션 강제 청산 (P&L 계산·DB 기록 포함)
        if (_position.Status == PositionStatus.Open)
        {
            // 현재가 조회 (실패 시 평균 진입가 대체)
            decimal currentPrice = _position.AvgEntryPrice;
            try { currentPrice = await _data.GetCurrentPriceAsync(); }
            catch { }

            // ClosePositionAsync(isForceClose: true): 시장가 청산 + P&L 계산 + OnTradeClosed(DB) + _sessionPnl 갱신 + Telegram 알림
            await ClosePositionAsync(currentPrice, isStopLoss: false, isForceClose: true);
        }
        else
        {
            Log("ℹ️ 열린 포지션 없음 — 청산 생략");
            _running = false;
            OnBotStopped?.Invoke(this, EventArgs.Empty);
        }

        // 3) Private WS 중지 (포지션 청산 후)
        if (_preOrderMode)
        {
            try { await _executor.StopPrivateStreamAsync(); }
            catch (Exception ex) { Log($"⚠️ Private WS 중지 실패: {ex.Message}"); }
        }

        await StopExitWatchdog();
        await _data.StopAsync();
    }

    // ═════════════════════════════════════════════
    // 캔들 완성 시 처리 (메인 루프)
    // ═════════════════════════════════════════════

    private async void OnCandleCompletedAsync(object? sender, Candle candle)
    {
        if (!_running) return;

        // 이전 캔들 처리 완료 전 새 캔들 도착 시 건너뜀 (이중 진입 방지)
        if (!await _candleSem.WaitAsync(0))
        {
            Log($"⏭ 캔들 건너뜀 — 이전 캔들 처리 중 ({candle.Timestamp:HH:mm:ss})");
            return;
        }

        try
        {
            if (!_running) return;
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
        finally
        {
            _candleSem.Release();
        }
    }

    // ═════════════════════════════════════════════
    // 신규 진입 (GPT 분석 → 1단계 진입)
    // ═════════════════════════════════════════════

    private async Task TryEnterNewPositionAsync(Candle latestCandle)
    {
        // 진입 제한 시간 체크 (KST 기준)
        if (_config.TradeRestrictEnabled && IsTradeRestrictedNow())
        {
            Log($"⏰ 진입 제한 시간 ({_config.TradeRestrictStart}~{_config.TradeRestrictEnd}) — 신규 진입 스킵");
            return;
        }

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

            var gptCtx = await BuildGptContextAsync();
            var result = await _gptAnalyzer.AnalyzeAsync(candles, _config.GptConfidenceThreshold, gptCtx);
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

            // ── 15분봉 추세 필터 ──
            var m15t = gptCtx.FifteenMinTrend;
            if (!string.IsNullOrEmpty(m15t) && m15t != "Sideways")
            {
                bool conflict = (direction == TradeDirection.Long  && m15t == "Downtrend") ||
                                (direction == TradeDirection.Short && m15t == "Uptrend");
                if (conflict)
                {
                    _consecutiveSkipCount++;
                    Log($"[15m 필터] ❌ 진입 스킵 — GPT:{direction} vs 15m:{m15t} 방향 충돌");
                    return;
                }
                Log($"[15m 필터] ✅ 방향 일치 — {direction} ({m15t})");
            }

            _blogGptDirection  = direction.ToString();
            _blogGptConfidence = result.Confidence;
            _blogGptReason     = result.Reason ?? "";
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

        // ── 신규 진입 직전: 거래소에 잔여 포지션/algo 없는지 최종 확인 ──
        if (!await EnsureCleanStateForNewCycleAsync())
        {
            Log("⏸ 신규 진입 중단 — 거래소 상태 정합성 확인 필요 (다음 캔들에서 재시도)");
            return;
        }

        _blogEntryCandle = latestCandle;
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
            Log($"[가격 방향 감지] 기준가 갱신: {Px(_priceAnchor)} — 다음 캔들 대기");
            return (TradeDirection)(-1); // 대기 신호
        }

        var diff    = currentPrice - _priceAnchor;
        var diffPct = diff / _priceAnchor * 100; // 부호 유지 (양수=상승, 음수=하락)
        var absPct  = Math.Abs(diffPct);

        // 최소 변동폭 0.01% 미만이면 무시 (노이즈 방지)
        if (absPct < 0.01m)
        {
            Log($"[가격 방향 감지] 변동 미미 ({diffPct:+0.0000;-0.0000}%) — 대기");
            return (TradeDirection)(-1);
        }

        var dir = diff > 0 ? TradeDirection.Long : TradeDirection.Short;
        Log($"[가격 방향 감지] 기준가: {Px(_priceAnchor)} → 현재가: {Px(currentPrice)} " +
            $"({diffPct:+0.000;-0.000}%) → {dir} 진입");

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
                $"현재가: {Px(currentPrice)} | 트리거: {Px(triggerPrice)} | 간격: {gap}%");
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
                Log($"💥 강제청산 ({modeLabel} 마진) | 현재가: {Px(currentPrice)} | 청산가: {Px(liqPrice.Value)}");
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
        if (_config.StopLossEnabled && _position.MartinStep >= _config.MartinCount)
        {
            var lastEntry = _position.LastEntryPrice > 0 ? _position.LastEntryPrice : _position.AvgEntryPrice;
            var extraMovePct = _position.Direction == TradeDirection.Long
                ? (lastEntry - currentPrice) / lastEntry * 100
                : (currentPrice - lastEntry) / lastEntry * 100;
            if (extraMovePct >= _config.StopLossPercent)
            {
                Log($"🛑 손절 조건 충족: 마지막진입가({lastEntry:N2}) 기준 -{extraMovePct:F3}% ≤ -{_config.StopLossPercent}% (마틴 {_position.MartinStep}/{_config.MartinCount}단계 완료)");
                await ClosePositionAsync(currentPrice, isStopLoss: true);
            }
        }
    }

    // ═════════════════════════════════════════════
    // 손절 전용 체크 (pre-orders 모드)
    // ═════════════════════════════════════════════

    private async Task CheckStopLossOnlyAsync(decimal currentPrice)
    {
        if (!_config.StopLossEnabled) return;
        if (_position.MartinStep < _config.MartinCount) return;

        // 손절 기준: 마지막 단계 진입가에서 추가로 StopLossPercent% 역방향 이동 시 손절
        var lastEntry = _position.LastEntryPrice > 0 ? _position.LastEntryPrice : _position.AvgEntryPrice;
        var extraMovePct = _position.Direction == TradeDirection.Long
            ? (lastEntry - currentPrice) / lastEntry * 100
            : (currentPrice - lastEntry) / lastEntry * 100;
        if (extraMovePct < _config.StopLossPercent) return;

        Log($"🛑 [pre-orders] 손절 조건 충족: 마지막진입가({lastEntry:N2}) 기준 -{extraMovePct:F3}% ≤ -{_config.StopLossPercent}% (마틴 {_position.MartinStep}/{_config.MartinCount} 완료)");

        // ── 안전 손절 절차 ──
        // 1) TP 먼저 명시적 취소 → 경합 체결(TP와 SL 동시 실행) 방지
        if (!string.IsNullOrEmpty(_activeTpAlgoId))
        {
            bool tpCanceled = false;
            try
            {
                tpCanceled = await _executor.CancelAlgoOrderAsync(_config.Symbol, _activeTpAlgoId);
            }
            catch (Exception ex)
            {
                Log($"⚠️ TP 취소 예외: {ex.Message}");
            }

            if (!tpCanceled)
            {
                // 취소 실패 — TP가 이미 체결됐을 가능성 → 포지션 조회로 확정
                Log($"⚠️ TP 취소 실패 — 포지션 잔존 여부 확인");
                var verifyPos = await SafeGetPositionAsync();
                if (verifyPos == null)
                {
                    Log("ℹ️ 포지션 없음 — TP가 선행 체결됨. 손절 취소 후 서버 청산으로 마무리");
                    _activeTpAlgoId = null;
                    try { await _executor.CancelAllAlgoOrdersAsync(_config.Symbol); } catch { }
                    _activeAlgoIds.Clear();
                    await FinalizeClosedFromServerAsync(currentPrice, isStopLoss: false);
                    return;
                }
                // 포지션 남아있음 → 재시도
                try { await _executor.CancelAlgoOrderAsync(_config.Symbol, _activeTpAlgoId); } catch { }
            }
            _activeTpAlgoId = null;
        }

        // 2) 마틴 트리거 일괄 취소
        try
        {
            await _executor.CancelAllAlgoOrdersAsync(_config.Symbol);
        }
        catch (Exception ex)
        {
            Log($"⚠️ 트리거 일괄 취소 실패 (계속 진행): {ex.Message}");
        }
        _activeAlgoIds.Clear();

        // 3) 포지션 재확인 — 이 사이에 TP가 체결됐을 가능성
        var pos = await SafeGetPositionAsync();
        if (pos == null)
        {
            Log("ℹ️ 청산 직전 포지션 조회 → 이미 없음 (TP 선행 체결로 판단). Finalize만 수행");
            await FinalizeClosedFromServerAsync(currentPrice, isStopLoss: false);
            return;
        }

        // 4) 지정가 청산 (현재가) — 미체결 시 watchdog 이 5초×2 정정 후 시장가 fallback
        try
        {
            var limitRes = await _executor.PlaceLimitReduceOrderAsync(
                _config.Symbol, _position.Direction, _position.TotalAmount, currentPrice);
            if (limitRes.Success)
            {
                _stopLossOrderId = limitRes.OrderId;
                Log($"🛑 손절 지정가 청산 등록: ordId={limitRes.OrderId} @ {currentPrice:N6} — watchdog 감시 시작");
                return; // watchdog 가 fill / amend / fallback 처리
            }
            Log($"⚠️ 손절 지정가 등록 실패 → 시장가 fallback: {limitRes.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Log($"⚠️ 손절 지정가 예외 → 시장가 fallback: {ex.Message}");
        }
        await ClosePositionAsync(currentPrice, isStopLoss: true);
    }

    /// <summary>예외를 삼키고 포지션 조회 (SL 경합 방지 목적).</summary>
    private async Task<ExchangePositionInfo?> SafeGetPositionAsync()
    {
        try { return await _executor.GetPositionAsync(_config.Symbol); }
        catch (Exception ex) { Log($"⚠️ 포지션 조회 실패: {ex.Message}"); return null; }
    }

    /// <summary>
    /// 신규 사이클 진입 전 거래소 상태 검증.
    /// - 잔여 포지션 있음 → 동기화 필요 (false 반환)
    /// - 잔여 algo 주문 있음 → 일괄 취소 후 진행
    /// 이전 사이클 청산 요청이 부분적으로 실패해 좀비 주문이 남은 경우를 방어.
    /// </summary>
    private async Task<bool> EnsureCleanStateForNewCycleAsync()
    {
        if (!_preOrderMode) return true;

        try
        {
            // 1) 거래소 포지션 확인 — 있으면 진입 차단 후 상태 복원
            var pos = await _executor.GetPositionAsync(_config.Symbol);
            if (pos != null)
            {
                Log($"⚠️ 사전 검증: 거래소에 잔여 포지션 감지 ({pos.Direction} 평균가 {pos.AvgEntryPrice:N4}, {pos.NotionalUsd:F2}USDT)");
                Log($"   → 인메모리 상태 재동기화 수행 (신규 진입 건너뜀)");
                await TrySyncPositionFromExchangeAsync();
                return false;
            }

            // 2) 잔여 algo 주문 확인 → 있으면 전부 취소
            var algos = await _executor.GetOpenAlgoOrdersAsync(_config.Symbol);
            if (algos.Count > 0)
            {
                Log($"🧹 사전 검증: 잔여 algo 주문 {algos.Count}건 감지 → 일괄 취소 후 진입");
                var ok = await _executor.CancelAllAlgoOrdersAsync(_config.Symbol);
                if (!ok)
                {
                    Log("⚠️ 일괄 취소 실패 — 신규 진입 건너뜀 (다음 캔들 재시도)");
                    return false;
                }
                _activeAlgoIds.Clear();
                _activeTpAlgoId = null;

                // 취소 반영 대기 후 재확인
                await Task.Delay(500);
                var remaining = await _executor.GetOpenAlgoOrdersAsync(_config.Symbol);
                if (remaining.Count > 0)
                {
                    Log($"⚠️ 취소 후에도 잔여 {remaining.Count}건 — 신규 진입 건너뜀");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"⚠️ 사전 검증 실패: {ex.Message} — 안전을 위해 신규 진입 보류");
            return false;
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
        var failedSteps      = new List<int>();

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
                    Log($"  ❌ 마틴{step} 트리거 등록 실패: {res.ErrorMessage}");
                    failedSteps.Add(step);
                }
            }
            catch (Exception ex)
            {
                Log($"  ❌ 마틴{step} 트리거 예외: {ex.Message}");
                failedSteps.Add(step);
            }

            // 다음 단계 lastEntryPx/avgPrice 추정 (실제 체결 시 갱신됨)
            cumAvgPrice = (cumQty * cumAvgPrice + amount * triggerPrice) / (cumQty + amount);
            cumQty     += amount;
            lastEntryPx = triggerPrice;
        }

        // 일부 단계 등록 실패 → 전략 불완전
        // 포지션은 유지 (이미 수익 중일 수 있음 — 강제 청산 금지)
        // 등록된 트리거만 취소 → 봇 중단 → 사용자가 수동 결정
        if (failedSteps.Count > 0)
        {
            var failMsg = $"🚨 마틴 트리거 등록 실패 (단계: {string.Join(", ", failedSteps)}) — 부분 등록 취소 후 봇 중단. 포지션은 유지됨 — 수동 처리 필요.";
            Log(failMsg);
            await NotifyAsync(
                $"🚨 <b>마틴 트리거 등록 실패</b>\n" +
                $"심볼: {_config.Symbol}\n" +
                $"실패 단계: {string.Join(", ", failedSteps)}\n" +
                $"⚠️ 포지션 유지 중 — 수동으로 익절/손절 처리 필요\n" +
                $"봇은 중단됩니다.",
                NotificationType.Error);

            // 이미 등록된 트리거 주문 취소 (불완전한 마틴 주문 잔류 방지)
            try { await _executor.CancelAllAlgoOrdersAsync(_config.Symbol); }
            catch (Exception ex) { Log($"  ⚠️ 부분 취소 실패: {ex.Message}"); }
            _activeAlgoIds.Clear();

            _running = false;
            return;
        }

        // 2) 익절 conditional (현재 평균가 기준)
        await RegisterTakeProfitAsync();

        // ── [🔍TEST-2] 등록 완료 요약 ──
        Log($"[🔍TEST-2] 예비주문 등록 완료 요약:");
        Log($"  트리거 {_activeAlgoIds.Count}건: [{string.Join(", ", _activeAlgoIds)}]");
        Log($"  TP algoId: {(_activeTpAlgoId ?? "없음")}");
        Log($"  → OKX 앱 '알고 주문' 탭에서 {_activeAlgoIds.Count + (_activeTpAlgoId != null ? 1 : 0)}건 확인 필요");
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
                await HandleTpFailureAsync(tpPrice);
            }
        }
        catch (Exception ex)
        {
            Log($"  ❌ TP 등록 예외: {ex.Message}");
            await HandleTpFailureAsync(tpPrice);
        }
    }

    /// <summary>
    /// TP 등록 실패 시 처리:
    /// 현재가가 이미 이익 구간이면 즉시 시장가 청산,
    /// 아니면 현재가 기준으로 TP 재등록.
    /// </summary>
    private async Task HandleTpFailureAsync(decimal originalTpPrice)
    {
        try
        {
            var currentPrice = await _data.GetCurrentPriceAsync();
            var inProfitZone = _position.Direction == TradeDirection.Long
                ? currentPrice >= originalTpPrice
                : currentPrice <= originalTpPrice;

            if (inProfitZone)
            {
                Log($"  🚨 TP 실패 — 현재가({currentPrice:N4})가 이미 이익 구간. 즉시 시장가 청산");
                await CancelAllPreOrdersAsync();
                await ClosePositionAsync(currentPrice, isStopLoss: false);
            }
            else
            {
                // 현재가 기준으로 TP 재등록 (가격이 아직 목표에 도달 안 함)
                var targetPct   = _config.GetTargetProfitForStep(_position.MartinStep);
                var priceMovePct = targetPct / _config.Leverage;
                var newTpPrice  = _position.Direction == TradeDirection.Long
                    ? currentPrice * (1 + priceMovePct / 100)
                    : currentPrice * (1 - priceMovePct / 100);

                Log($"  🔄 TP 현재가 기준 재등록 시도: {newTpPrice:N4} (현재가: {currentPrice:N4})");
                var retry = await _executor.PlaceTakeProfitOrderAsync(
                    _config.Symbol, _position.Direction, newTpPrice, _config.MarginModeStr);
                if (retry.Success)
                {
                    _activeTpAlgoId = retry.OrderId;
                    Log($"  ✅ TP 재등록 성공: trigger {newTpPrice:N4} | algoId={retry.OrderId}");
                }
                else
                {
                    Log($"  🚨 TP 재등록도 실패: {retry.ErrorMessage} — 수동 처리 필요");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"  ❌ TP 실패 처리 중 예외: {ex.Message} — 수동 처리 필요");
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

    // ═════════════════════════════════════════════
    // Private WS 재연결 → 누락 이벤트 복구
    // ═════════════════════════════════════════════
    private DateTime _lastSyncAt = DateTime.MinValue;

    private async void OnStreamReconnectedHandler(object? sender, EventArgs e)
    {
        if (!_running) return;

        try
        {
            Log("🔄 WS 재연결 감지 — 누락 이벤트 복구 동기화 시작");

            // 1) 현재 거래소 포지션 조회
            var pos = await _executor.GetPositionAsync(_config.Symbol);

            // 2) 인메모리 Open이었는데 거래소 포지션 없음 → TP 체결 누락
            if (_position.Status == PositionStatus.Open && pos == null)
            {
                Log("ℹ️ WS 끊김 구간에 포지션 청산 감지 — 히스토리로 체결가 복원");
                var exitPrice = await ResolveMissedExitPriceAsync();
                await CancelAllPreOrdersAsync();
                await FinalizeClosedFromServerAsync(exitPrice, isStopLoss: false);
                return;
            }

            // 3) 인메모리 Open + 거래소 포지션 있음 → 마틴 추가 체결 가능성
            if (_position.Status == PositionStatus.Open && pos != null)
            {
                // 미체결 algo 조회 (단계 추정·TP 확인 모두 사용)
                var algos        = await _executor.GetOpenAlgoOrdersAsync(_config.Symbol);
                var triggerCount = algos.Count(a => !a.IsClose);
                var stepByTriggers = Math.Max(1, _config.MartinCount - triggerCount);

                // 1순위: 명목금액 기반 추정, 2순위: 잔여 트리거 수 역산
                var newStep = EstimateMartinStepFromNotional(pos.NotionalUsd) ?? stepByTriggers;

                if (newStep > _position.MartinStep)
                {
                    Log($"ℹ️ WS 끊김 구간에 마틴 {_position.MartinStep}→{newStep}단계 체결 감지 — 포지션 갱신");
                    _position.MartinStep     = newStep;
                    _position.TotalAmount    = pos.NotionalUsd;
                    _position.TotalQuantity  = pos.AvgEntryPrice > 0 ? pos.NotionalUsd / pos.AvgEntryPrice : 0;
                    _position.AvgEntryPrice  = pos.AvgEntryPrice;
                    _position.LastEntryPrice = pos.AvgEntryPrice;
                    OnPositionUpdated?.Invoke(this, _position);
                }

                // algo ID 항상 동기화
                _activeAlgoIds.Clear();
                foreach (var a in algos.Where(x => !x.IsClose))
                    _activeAlgoIds.Add(a.AlgoId);
                _activeTpAlgoId = algos.FirstOrDefault(x => x.IsClose)?.AlgoId;

                // 단계 변화 여부와 무관하게 TP 없으면 등록
                if (string.IsNullOrEmpty(_activeTpAlgoId))
                {
                    Log("ℹ️ 재연결 시 TP 주문 없음 → 자동 재등록");
                    await RegisterTakeProfitAsync();
                }
            }

            _lastSyncAt = DateTime.UtcNow;
            var posState = _position?.Status == PositionStatus.Open
                ? $"포지션 Open ({_position.MartinStep}단계 @ {_position.AvgEntryPrice:N2})"
                : "포지션 없음";
            Log($"✅ 재연결 동기화 완료 | {posState}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "재연결 동기화 중 오류");
            Log($"⚠️ 재연결 동기화 실패: {ex.Message}");
        }
    }

    /// <summary>WS 누락 구간에 체결된 TP 가격을 algo 히스토리에서 복원 (실패 시 현재가 사용)</summary>
    private async Task<decimal> ResolveMissedExitPriceAsync()
    {
        try
        {
            var history = await _executor.GetAlgoOrderHistoryAsync(_config.Symbol, 20);
            var recentTp = history
                .Where(h => h.IsClose && h.TpTriggerPx > 0)
                .OrderByDescending(h => h.UpdatedAtMs)
                .FirstOrDefault();
            if (recentTp != null && recentTp.TpTriggerPx > 0)
            {
                Log($"   ↳ 히스토리에서 TP trigger 가격 복원: {recentTp.TpTriggerPx:N4} (algoId={recentTp.AlgoId})");
                return recentTp.TpTriggerPx;
            }
        }
        catch (Exception ex) { Log($"   ⚠️ TP 히스토리 조회 실패: {ex.Message}"); }

        try { return await _data.GetCurrentPriceAsync(); }
        catch { return _position.AvgEntryPrice; }
    }

    private async void OnAlgoOrderFilledHandler(object? sender, AlgoOrderFillEvent e)
    {
        if (!_running) return;

        try
        {
            // 1) 청산(reduceOnly) 체결 → 익절 또는 손절 처리
            if (e.IsClose)
            {
                var isSl = !string.IsNullOrEmpty(_stopLossOrderId) && e.OrderId == _stopLossOrderId;
                _stopLossOrderId = null;
                if (isSl)
                {
                    Log($"🛑 [pre-orders] 손절 체결 수신: {e.Direction} @ {e.FilledPrice:N4} (size={e.FilledSize}, ordId={e.OrderId})");
                    await HandleSlFillAsync(e.FilledPrice);
                }
                else
                {
                    Log($"🎯 [pre-orders] 익절 체결 수신: {e.Direction} @ {e.FilledPrice:N4} (size={e.FilledSize}, algoId={e.AlgoId})");
                    await HandleTpFillAsync(e.FilledPrice);
                }
                return;
            }

            // 2) 마틴 추가 진입 체결 → 포지션 업데이트
            // algoId 없는 체결은 첫 진입 지정가 주문 — EnterAsync 폴링에서 이미 처리됨, 중복 방지
            if (string.IsNullOrEmpty(e.AlgoId))
            {
                Log($"⏭ [pre-orders] algoId 없는 체결 무시 (첫 진입 주문 중복): ordId={e.OrderId} @ {e.FilledPrice:N4}");
                return;
            }
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
        Log($"[🔍TEST-3] 마틴 체결 처리 시작: algoId={e.AlgoId} fillPx={e.FilledPrice:N4} fillSz={e.FilledSize} notional={e.NotionalUsd:F2}USDT");

        // 최대 단계 초과 방지
        if (_position.MartinStep >= _config.MartinCount)
        {
            Log($"⚠️ 마틴 체결 수신이나 이미 최대 단계({_config.MartinCount}) 도달 — 중복 이벤트로 판단, 무시");
            return;
        }

        // 체결가 기반 평균가 재계산 (계약수 기반 가중평균)
        var notional = e.NotionalUsd > 0 ? e.NotionalUsd : (e.FilledSize * e.FilledPrice);
        var qty      = e.FilledPrice > 0 ? notional / e.FilledPrice : 0;

        _position.MartinStep++;
        _position.TotalAmount   += notional;
        _position.TotalQuantity += qty;
        _position.AvgEntryPrice  = _position.TotalQuantity > 0
            ? _position.TotalAmount / _position.TotalQuantity
            : e.FilledPrice;
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
            $"투입금액(합계): {_position.TotalAmount:F2} USDT{Krw(_position.TotalAmount)}\n" +
            $"단계: {_position.MartinStep}/{_config.MartinCount}\n" +
            $"세션 수익: {_sessionPnl:+0.0000;-0.0000} USDT{Krw(_sessionPnl)}",
            NotificationType.Martin);

        OnPositionUpdated?.Invoke(this, _position);

        // 평균가 변경 → TP 재등록
        await RegisterTakeProfitAsync();

        // GPT 재분석: MartinCount의 1/3 단계 도달 시 (보류 - 손실 중 재분석은 항상 반대 신호가 나와 의미 없음)
        // var reanalysisStep = (int)Math.Ceiling(_config.MartinCount / 3.0);
        // if (_gptAnalyzer != null && _position.MartinStep == reanalysisStep)
        //     await GptReanalysisAsync(e.FilledPrice);
    }

    private bool IsTradeRestrictedNow()
    {
        try
        {
            var kst  = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Korea Standard Time" : "Asia/Seoul");
            var now  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, kst).TimeOfDay;
            var start = TimeSpan.Parse(_config.TradeRestrictStart);
            var end   = TimeSpan.Parse(_config.TradeRestrictEnd);
            return start <= end
                ? now >= start && now < end           // 같은 날 (예: 02:00~06:00)
                : now >= start || now < end;           // 자정 걸침 (예: 22:00~06:00)
        }
        catch { return false; }
    }

    private async Task<GptAnalysisContext> BuildGptContextAsync(
        string? posDir = null, int posStep = 0, decimal avgEntry = 0)
    {
        // 4H 캔들 추세 요약
        string? higherTf = null;
        try
        {
            var h4 = await _data.GetRecentCandlesAsync(10, "4H");
            if (h4.Count >= 2)
            {
                var h4First = h4.First().Close;
                var h4Last  = h4.Last().Close;
                var h4Pct   = (h4Last - h4First) / h4First * 100;
                var h4High  = h4.Max(c => c.High);
                var h4Low   = h4.Min(c => c.Low);
                var trend   = h4Pct > 0.5m ? "Uptrend" : h4Pct < -0.5m ? "Downtrend" : "Sideways";
                higherTf    = $"Trend: {trend} ({h4Pct:+0.00;-0.00}% over 10 x 4H candles) | High: {h4High:N2} | Low: {h4Low:N2}";
            }
        }
        catch { }

        // 15분봉 추세 (EMA5 vs EMA20)
        string? m15Summary = null;
        string? m15Trend   = null;
        try
        {
            var m15 = await _data.GetRecentCandlesAsync(25, "15m");
            if (m15.Count >= 20)
            {
                var closes = m15.Select(c => c.Close).ToList();
                var ema5   = CalcEmaLast(closes, 5);
                var ema20  = CalcEmaLast(closes, 20);
                var last   = closes.Last();
                var m15Pct = (last - closes.First()) / closes.First() * 100;
                m15Trend   = ema5 > ema20 * 1.001m ? "Uptrend"
                           : ema5 < ema20 * 0.999m ? "Downtrend"
                           : "Sideways";
                m15Summary = $"Trend: {m15Trend} | EMA5={ema5:N2} | EMA20={ema20:N2} | Change={m15Pct:+0.00;-0.00}% (25 x 15m)";
            }
        }
        catch { }

        return new GptAnalysisContext
        {
            Symbol            = _config.Symbol,
            Leverage          = _config.Leverage,
            MartinCount       = _config.MartinCount,
            PositionDirection = posDir,
            PositionStep      = posStep,
            AvgEntryPrice     = avgEntry,
            HigherTfSummary   = higherTf,
            FifteenMinSummary = m15Summary,
            FifteenMinTrend   = m15Trend,
        };
    }

    private async Task GptReanalysisAsync(decimal currentPrice)
    {
        try
        {
            Log($"🔄 [GPT 재분석] {_position.MartinStep}/{_config.MartinCount}단계 도달 — 4H봉 기준 방향 재확인 중...");
            // 단기 노이즈 배제: 1분봉 대신 4H봉 20개 사용
            var candles = await _data.GetRecentCandlesAsync(20, "4H");
            var reCtx   = await BuildGptContextAsync(
                posDir:   _position.Direction.ToString(),
                posStep:  _position.MartinStep,
                avgEntry: _position.AvgEntryPrice);
            var result  = await _gptAnalyzer.AnalyzeAsync(candles, _config.GptConfidenceThreshold, reCtx);

            if (result.IsError)
            {
                Log($"⚠️ [GPT 재분석] 실패: {result.ErrorMessage}");
                return;
            }

            Log($"[GPT 재분석] {result.Direction} {result.Confidence}% | {result.Reason}");

            // 반대 방향 + 신뢰도 기준 이상 → 조기 청산
            var isOpposite = result.Direction != _position.Direction;
            if (isOpposite && result.ShouldEnter(_config.GptConfidenceThreshold))
            {
                Log($"🔄 [GPT 재분석] 반대 신호 ({result.Direction} {result.Confidence}%) → 조기 청산 실행");
                await NotifyAsync(
                    $"🔄 <b>GPT 재분석 조기 청산</b>\n" +
                    $"심볼: {_config.Symbol}\n" +
                    $"현재: {_position.Direction} {_position.MartinStep}단계\n" +
                    $"재분석: {result.Direction} {result.Confidence}%\n" +
                    $"→ 조기 청산 후 다음 캔들 방향 전환",
                    NotificationType.BotStartStop);
                await ClosePositionAsync(currentPrice, isStopLoss: false);
            }
            else
            {
                Log($"[GPT 재분석] 방향 유지 — 기존 포지션 계속");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ [GPT 재분석] 예외: {ex.Message}");
        }
    }

    private async Task HandleTpFillAsync(decimal exitPrice)
    {
        if (_position.Status != PositionStatus.Open)
        {
            Log("⚠️ 포지션 미오픈 상태에서 TP 체결 수신 (무시)");
            return;
        }
        Log($"[🔍TEST-4] 익절 체결 처리 시작: exitPrice={exitPrice:N4} | 평균가={_position.AvgEntryPrice:N4} | 누적={_position.TotalAmount:F2}USDT");
        await CancelAllPreOrdersAsync();
        await FinalizeClosedFromServerAsync(exitPrice, isStopLoss: false);
    }

    private async Task HandleSlFillAsync(decimal exitPrice)
    {
        if (_position.Status != PositionStatus.Open)
        {
            Log("⚠️ 포지션 미오픈 상태에서 SL 체결 수신 (무시)");
            return;
        }
        Log($"🛑 손절 체결 처리 시작: exitPrice={exitPrice:N4} | 평균가={_position.AvgEntryPrice:N4} | 누적={_position.TotalAmount:F2}USDT");
        await CancelAllPreOrdersAsync();
        await FinalizeClosedFromServerAsync(exitPrice, isStopLoss: true);
    }

    /// <summary>서버가 이미 청산한 상태에서 메모리/이벤트만 마무리</summary>
    private async Task FinalizeClosedFromServerAsync(decimal exitPrice, bool isStopLoss)
    {
        var pricePct = _position.GetUnrealizedPnlPercent(exitPrice);
        var pnlPct   = pricePct * _config.Leverage;          // 레버리지 포함 수익률 % (표시용)
        var pnlAmt   = _position.TotalAmount * pricePct / 100; // 실제 손익금액 (명목×가격변화율)

        // 수수료: 선물 명목금액 기준 Maker+Taker
        var fee      = _position.TotalAmount * (_makerFeeRate + _takerFeeRate);

        // 펀딩비: 포지션 기간 동안 실제 수령/지급액 조회
        _position.Status   = PositionStatus.Closed;
        _position.ClosedAt = DateTime.UtcNow;
        var fundingFee = 0m;
        try
        {
            fundingFee = await _executor.GetFundingFeeAsync(
                _config.Symbol, _position.OpenedAt, _position.ClosedAt.Value);
            if (fundingFee != 0)
                Log($"  💱 펀딩비: {fundingFee:+0.0000;-0.0000} USDT{Krw(fundingFee)}");
        }
        catch { }

        var pnlNet   = pnlAmt - fee + fundingFee;

        _position.RealizedPnl = pnlNet;

        _cycleCount++;
        _sessionPnl += pnlNet;

        var balBefore = _trackedBalance;
        var balAfter  = balBefore + pnlNet;
        _trackedBalance = balAfter;

        if (_config.ReinvestProfit && pnlNet != 0)
        {
            var newBudget = Math.Max(_config.TotalBudget + pnlNet, 1m);
            Log($"💰 재투자: 예산 {_config.TotalBudget:F2} → {newBudget:F2} USDT ({pnlNet:+0.0000;-0.0000} USDT)");
            _config.TotalBudget = newBudget;
        }

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
            PnlAmount     = pnlNet,
            Fee           = fee,
            IsStopLoss    = isStopLoss,
            Leverage      = _config.Leverage,
            OpenedAt      = _position.OpenedAt,
            ClosedAt      = _position.ClosedAt ?? DateTime.UtcNow,
            AmountMode    = _config.AmountMode.ToString(),
            BalanceBefore = balBefore,
            BalanceAfter  = balAfter
        });

        var emoji     = isStopLoss ? "🛑" : "✅";
        var typeLabel = isStopLoss ? "손절" : "익절";
        Log($"{emoji} [pre-orders] {typeLabel} 청산 완료 | {pnlPct:+0.00;-0.00}% ({pnlAmt:+0.0000;-0.0000} USDT) | 수수료: -{fee:F4} USDT | 순손익: {pnlNet:+0.0000;-0.0000} USDT | " +
            $"마틴 {_position.MartinStep}/{_config.MartinCount} | 사이클 #{_cycleCount} | 세션: {_sessionPnl:+0.0000;-0.0000}");
        LogBlogSection(exitPrice, pnlPct, pnlAmt, fee, pnlNet, isStopLoss);

        await NotifyAsync(
            $"{emoji} <b>{typeLabel} 청산 (서버)</b>\n" +
            $"심볼: {_config.Symbol}\n" +
            $"수익률: {pnlPct:+0.00;-0.00}%\n" +
            $"손익: {pnlAmt:+0.0000;-0.0000} USDT{Krw(pnlAmt)}\n" +
            $"수수료: -{fee:F4} USDT\n" +
            $"순손익: {pnlNet:+0.0000;-0.0000} USDT{Krw(pnlNet)}\n" +
            $"마틴: {_position.MartinStep}/{_config.MartinCount}\n" +
            $"사이클: #{_cycleCount}\n" +
            $"세션 누적: {_sessionPnl:+0.0000;-0.0000} USDT{Krw(_sessionPnl)}",
            isStopLoss ? NotificationType.StopLoss : NotificationType.TakeProfit);

        OnPositionUpdated?.Invoke(this, _position);

        // 자동반복
        if (_autoRepeat && _running)
        {
            if (_gptAnalyzer == null)
            {
                _priceAnchor = exitPrice;
                Log($"🔄 자동반복: 기준가 → {Px(_priceAnchor)} | 다음 캔들 방향 감지 대기...");
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
            OnBotStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    // ═════════════════════════════════════════════
    // 진입 실행
    // ═════════════════════════════════════════════

    private async Task EnterAsync(TradeDirection direction, decimal price, bool isFirstEntry)
    {
        var currentStep = isFirstEntry ? 1 : (_position!.MartinStep + 1);
        var amount = _config.GetAmountForStep(currentStep);

        // 지정가 진입: 현재가 조회 (실시간 ticker 우선)
        decimal limitPx;
        try { limitPx = await _data.GetCurrentPriceAsync(); }
        catch { limitPx = price; }
        if (limitPx <= 0) limitPx = price;

        var result = await _executor.PlaceOrderAsync(new OrderRequest
        {
            Symbol     = _config.Symbol,
            Side       = direction == TradeDirection.Long ? OrderSide.Buy : OrderSide.Sell,
            Direction  = direction,
            Amount     = amount,
            Type       = OrderType.Limit,
            Price      = limitPx,
            MarginMode = _config.MarginModeStr
        });

        if (!result.Success)
        {
            Log($"❌ 주문 실패: {result.ErrorMessage}");
            await NotifyAsync($"❌ <b>주문 실패</b>\n{result.ErrorMessage}", NotificationType.Error);
            return;
        }

        // 체결가: OrderResult에서 반환된 값(API 조회) 우선, 없으면 캔들 Close
        var filledPrice = result.FilledPrice > 0 ? result.FilledPrice : price;
        if (result.FilledPrice <= 0)
            Log($"[🔍TEST-1] ⚠️ 체결가 API 미반환 — 캔들 종가 대체 사용: {price:N4} (실제 체결가와 다를 수 있음)");
        else
            Log($"[🔍TEST-1] 체결가 확인: {filledPrice:N4} | 계약수: {result.FilledSize} | 상태: {result.State}");

        // 실제 명목금액 (계약수 기반). FilledNotional 없으면 입력금액 사용
        var actualAmount = result.FilledNotional > 0 ? result.FilledNotional : amount;
        if (result.FilledNotional > 0 && Math.Abs(result.FilledNotional - amount) > 0.5m)
            Log($"[명목금액] 입력 {amount:F4} USDT → 실제 {actualAmount:F4} USDT (계약 반올림)");

        var qty = filledPrice > 0 ? actualAmount / filledPrice : 0;

        if (isFirstEntry)
        {
            _position = new Position
            {
                Direction      = direction,
                Status         = PositionStatus.Open,
                MartinStep     = 1,
                TotalAmount    = actualAmount,
                TotalQuantity  = qty,
                AvgEntryPrice  = filledPrice,
                LastEntryPrice = filledPrice,
                OpenedAt       = DateTime.UtcNow
            };
            _position.StageEntries.Add(new StageEntry(1, filledPrice, actualAmount, DateTime.Now));

            var liqInfo = FormatLiquidationLog();
            var msg = $"📈 신규 진입 [{direction}] | " +
                      $"{actualAmount:F2} USDT @ {Px(filledPrice)} | " +
                      $"1/{_config.MartinCount}단계{liqInfo}";
            Log(msg);
            await NotifyAsync(
                $"📈 <b>신규 진입</b>\n" +
                $"심볼: {_config.Symbol}\n" +
                $"방향: {direction}\n" +
                $"투입금액: {actualAmount:F2} USDT{KrwAbs(actualAmount)}\n" +
                $"진입가: {Px(filledPrice)}\n" +
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
            // 최대 단계 초과 방지
            if (_position.MartinStep >= _config.MartinCount)
            {
                Log($"⚠️ 마틴 진입 시도이나 이미 최대 단계({_config.MartinCount}) 도달 — 무시");
                return;
            }

            // 계약수 기반 가중 평균 진입가 재계산
            _position.MartinStep++;
            _position.TotalAmount   += actualAmount;
            _position.TotalQuantity += qty;
            _position.AvgEntryPrice  = _position.TotalQuantity > 0
                ? _position.TotalAmount / _position.TotalQuantity
                : filledPrice;
            _position.LastEntryPrice = filledPrice;
            _position.StageEntries.Add(new StageEntry(_position.MartinStep, filledPrice, actualAmount, DateTime.Now));

            var liqInfo = FormatLiquidationLog();
            var msg = $"➕ 마틴 {_position.MartinStep}단계 [{direction}] | " +
                      $"{amount:F2} USDT @ {Px(filledPrice)} | " +
                      $"평균가: {Px(_position.AvgEntryPrice)} | " +
                      $"누적: {_position.TotalAmount:F2} USDT{liqInfo}";
            Log(msg);
            await NotifyAsync(
                $"➕ <b>마틴 {_position.MartinStep}단계</b>\n" +
                $"심볼: {_config.Symbol}\n" +
                $"진입가: {Px(filledPrice)}\n" +
                $"평균가: {Px(_position.AvgEntryPrice)}\n" +
                $"투입금액(합계): {_position.TotalAmount:F2} USDT{KrwAbs(_position.TotalAmount)}\n" +
                $"단계: {_position.MartinStep}/{_config.MartinCount}\n" +
                $"세션 수익: {_sessionPnl:+0.0000;-0.0000} USDT{Krw(_sessionPnl)}",
                NotificationType.Martin);
        }

        OnPositionUpdated?.Invoke(this, _position);
    }

    private string FormatLiquidationLog()
    {
        var liq = _executor.GetLiquidationPrice();
        if (liq == null) return "";
        var modeLabel = _config.MarginModeStr == "cross" ? "교차" : "격리";
        return $" | 청산가: {Px(liq.Value)} ({modeLabel})";
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
        var pnlPct   = pricePct * _config.Leverage;          // 레버리지 포함 수익률 % (표시용)
        var pnlAmt   = _position.TotalAmount * pricePct / 100; // 실제 손익금액 (명목×가격변화율, leverage 불필요)

        // 수수료: 선물 명목금액 기준 Maker+Taker
        var fee      = _position.TotalAmount * (_makerFeeRate + _takerFeeRate);

        _position.Status   = PositionStatus.Closed;
        _position.ClosedAt = DateTime.UtcNow;
        var fundingFee = 0m;
        try
        {
            fundingFee = await _executor.GetFundingFeeAsync(
                _config.Symbol, _position.OpenedAt, _position.ClosedAt.Value);
            if (fundingFee != 0)
                Log($"  💱 펀딩비: {fundingFee:+0.0000;-0.0000} USDT{Krw(fundingFee)}");
        }
        catch { }

        var pnlNet   = pnlAmt - fee + fundingFee;

        _position.RealizedPnl = pnlNet;

        _cycleCount++;
        _sessionPnl += pnlNet;

        var balBefore = _trackedBalance;
        var balAfter  = balBefore + pnlNet;
        _trackedBalance = balAfter;

        if (_config.ReinvestProfit && pnlNet != 0)
        {
            var newBudget = Math.Max(_config.TotalBudget + pnlNet, 1m);
            Log($"💰 재투자: 예산 {_config.TotalBudget:F2} → {newBudget:F2} USDT ({pnlNet:+0.0000;-0.0000} USDT)");
            _config.TotalBudget = newBudget;
        }

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
            PnlAmount     = pnlNet,
            Fee           = fee,
            IsStopLoss    = isStopLoss,
            Leverage      = _config.Leverage,
            OpenedAt      = _position.OpenedAt,
            ClosedAt      = _position.ClosedAt ?? DateTime.UtcNow,
            AmountMode    = _config.AmountMode.ToString(),
            BalanceBefore = balBefore,
            BalanceAfter  = balAfter
        });

        // 로그 + 알림
        var emoji     = isStopLoss ? "🛑" : isForceClose ? "🔴" : "✅";
        var typeLabel = isStopLoss ? "손절" : isForceClose ? "강제청산" : "익절";
        var logMsg    = $"{emoji} {typeLabel} | {pnlPct:+0.00;-0.00}% ({pnlAmt:+0.0000;-0.0000} USDT) | 수수료: -{fee:F4} USDT | 순손익: {pnlNet:+0.0000;-0.0000} USDT | " +
                        $"마틴 {_position.MartinStep}/{_config.MartinCount} | " +
                        $"사이클 #{_cycleCount} | 세션 누적: {_sessionPnl:+0.0000;-0.0000} USDT";
        Log(logMsg);
        LogBlogSection(exitPrice, pnlPct, pnlAmt, fee, pnlNet, isStopLoss);

        var notifyType = isStopLoss ? NotificationType.StopLoss : NotificationType.TakeProfit;
        await NotifyAsync(
            $"{emoji} <b>{typeLabel} 청산</b>\n" +
            $"심볼: {_config.Symbol}\n" +
            $"수익률: {pnlPct:+0.00;-0.00}%\n" +
            $"손익(gross): {pnlAmt:+0.0000;-0.0000} USDT{Krw(pnlAmt)}\n" +
            $"수수료: -{fee:F4} USDT\n" +
            $"순손익: {pnlNet:+0.0000;-0.0000} USDT{Krw(pnlNet)}\n" +
            $"마틴: {_position.MartinStep}/{_config.MartinCount}\n" +
            $"사이클: #{_cycleCount}\n" +
            $"세션 누적: {_sessionPnl:+0.0000;-0.0000} USDT{Krw(_sessionPnl)}",
            notifyType);

        OnPositionUpdated?.Invoke(this, _position);

        // ── 사이클 관리: 자동반복 ──
        if (_autoRepeat && _running && !isForceClose)
        {
            // GPT 미사용 모드: 청산 가격을 다음 사이클 기준가로 설정
            if (_gptAnalyzer == null)
            {
                _priceAnchor = exitPrice;
                Log($"🔄 자동반복: 기준가 → {Px(_priceAnchor)} | 다음 캔들 방향 감지 대기...");
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
            OnBotStopped?.Invoke(this, EventArgs.Empty);
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
                // 포지션은 없지만 앱 꺼진 동안 TP가 체결됐을 수 있음 → 최근 히스토리 확인
                await CheckMissedTpOnStartupAsync();
                Log("🔍 재시작 동기화: 거래소에 열린 포지션 없음 — 새 사이클 시작");
                return false;
            }

            // 미체결 algo 주문 조회
            var algoOrders   = await _executor.GetOpenAlgoOrdersAsync(_config.Symbol);
            var triggerOrders = algoOrders.Where(a => !a.IsClose).ToList();
            var tpOrder       = algoOrders.FirstOrDefault(a => a.IsClose);

            // ── 마틴 단계 정밀 추정 ──
            // 1순위: 실제 포지션 명목가를 각 단계 누적 금액과 매칭
            // 2순위: 남은 트리거 개수로 역산 (백업)
            var martinStep = EstimateMartinStepFromNotional(pos.NotionalUsd)
                             ?? Math.Max(1, _config.MartinCount - triggerOrders.Count);

            // 트리거 개수와 notional 추정 결과가 불일치 → 경고
            var stepByTriggers = Math.Max(1, _config.MartinCount - triggerOrders.Count);
            if (martinStep != stepByTriggers)
            {
                Log($"⚠️ 마틴 단계 추정 불일치: notional={martinStep}, triggers={stepByTriggers} — notional 우선 채택");
            }

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

            // TP 없으면 자동 등록
            if (_activeTpAlgoId == null)
            {
                Log("ℹ️ 거래소에 TP 주문 없음 → 자동 재등록");
                await RegisterTakeProfitAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"⚠️ 재시작 동기화 실패: {ex.Message} — 잔여 주문 정리 후 새 사이클 시작");
            return false;
        }
    }

    /// <summary>
    /// 앱 꺼진 동안 TP가 발동되었는지 최근 algo 히스토리로 확인.
    /// DB 레코드 자동 생성은 불가 (평균가/진입시각 등 상세 정보 부재) — 사용자에게 알림.
    /// 최근 24시간 이내 effective 상태 conditional 주문이 있으면 경고.
    /// </summary>
    private async Task CheckMissedTpOnStartupAsync()
    {
        try
        {
            var since    = DateTime.UtcNow.AddHours(-24);
            var cutoffMs = new DateTimeOffset(since).ToUnixTimeMilliseconds();

            var history = await _executor.GetAlgoOrderHistoryAsync(_config.Symbol, 50);
            var recentTps = history
                .Where(h => h.IsClose && h.UpdatedAtMs >= cutoffMs)
                .OrderByDescending(h => h.UpdatedAtMs)
                .ToList();

            if (recentTps.Count == 0) return;

            // DB 전체 기록이 0건 → 의도적 초기화 상태, 누락 경고 불필요
            var totalDbCount = _getDbTradeCount?.Invoke(_config.Symbol, DateTime.MinValue) ?? -1;
            if (totalDbCount == 0)
            {
                Log("📋 DB 초기화 상태 (전체 기록 없음) — 누락 검사 스킵");
                return;
            }

            // DB 기록 건수와 비교해 실제 누락 건만 경고
            var dbCount    = _getDbTradeCount?.Invoke(_config.Symbol, since) ?? -1;
            var missedCount = dbCount >= 0 ? Math.Max(0, recentTps.Count - dbCount) : recentTps.Count;

            var latest = recentTps[0];
            var when   = DateTimeOffset.FromUnixTimeMilliseconds(latest.UpdatedAtMs).LocalDateTime;

            if (dbCount >= 0)
                Log($"🔍 익절 히스토리: OKX {recentTps.Count}건 / DB {dbCount}건 → 누락 추정 {missedCount}건");
            else
                Log($"🔍 익절 히스토리: OKX {recentTps.Count}건 (DB 미확인)");

            if (missedCount == 0)
            {
                Log($"✅ DB 기록 정상 — 누락 없음");
                return;
            }

            Log($"⚠️ DB 미기록 {missedCount}건 추정 | 마지막: {when:MM-dd HH:mm} @ {latest.TpTriggerPx:N4}");
            Log($"   ↳ OKX 거래 내역에서 직접 확인 권장");

            await NotifyAsync(
                $"⚠️ <b>DB 미기록 거래 감지</b>\n" +
                $"심볼: {_config.Symbol}\n" +
                $"OKX 24h 익절: {recentTps.Count}건\n" +
                $"DB 기록: {(dbCount >= 0 ? dbCount.ToString() : "미확인")}건\n" +
                $"누락 추정: {missedCount}건\n" +
                $"마지막: {when:MM-dd HH:mm} @ {latest.TpTriggerPx:N4}",
                NotificationType.BotStartStop);
        }
        catch (Exception ex)
        {
            Log($"⚠️ 누락 TP 확인 실패: {ex.Message}");
        }
    }

    // ═════════════════════════════════════════════
    // 헬퍼
    // ═════════════════════════════════════════════

    private int? EstimateMartinStepFromNotional(decimal actualNotional)
    {
        if (actualNotional <= 0) return null;

        // 앵커 없음 → 추정 불가 (호출자의 트리거 수 기반 폴백 사용)
        if (_position.MartinStep < 1 || _position.TotalAmount <= 0)
        {
            Log($"[마틴 추정] 앵커 없음 (MartinStep={_position.MartinStep}, TotalAmount={_position.TotalAmount:F2}) → 추정 불가");
            return null;
        }

        // 단계 변화 없음 우선 감지 (5% 허용)
        var noChangeDiff = Math.Abs(actualNotional - _position.TotalAmount) / _position.TotalAmount * 100;
        if (noChangeDiff <= 5m)
        {
            Log($"[마틴 추정] 실제 명목 {actualNotional:F2} ≈ 현재 포지션 {_position.TotalAmount:F2} (오차 {noChangeDiff:F1}%) → {_position.MartinStep}단계 유지");
            return _position.MartinStep;
        }

        // 단계당 평균 명목금액 단위 (동적 계산, 하드코딩 없음)
        // _position.TotalAmount / MartinStep = 실제 체결 기반 단위 (심볼·가격 무관하게 동작)
        var perUnit = _position.TotalAmount / _position.MartinStep;

        int bestStep = 0;
        decimal bestDiff = decimal.MaxValue;
        var sb = new System.Text.StringBuilder();
        sb.Append($"[마틴 추정] 실제 명목 {actualNotional:F2} USDT (단위={perUnit:F2}, 앵커={_position.MartinStep}단계/{_position.TotalAmount:F2}) → ");

        for (int s = 1; s <= _config.MartinCount; s++)
        {
            var expected = perUnit * s;
            var diffPct  = expected > 0 ? Math.Abs(actualNotional - expected) / expected * 100 : decimal.MaxValue;
            if (diffPct < bestDiff) { bestDiff = diffPct; bestStep = s; }
        }

        var result = bestDiff <= 10m ? bestStep : (int?)null;
        sb.Append(result.HasValue
            ? $"{result}단계 (오차 {bestDiff:F1}%)"
            : $"추정 불가 (최소오차 {bestDiff:F1}% > 10%)");
        Log(sb.ToString());
        return result;
    }

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

    /// <summary>가격 자릿수 자동 포맷 — 저가 토큰(0.097 등)도 유효숫자 4자리 이상 표시</summary>
    private static string Px(decimal price)
    {
        if (price <= 0) return "0";
        var digits = Math.Max(2, (int)Math.Ceiling(-Math.Log10((double)price)) + 3);
        return price.ToString($"N{Math.Min(digits, 8)}");
    }

    // EMA 마지막 값만 계산 (15분봉 추세 필터용)
    private static decimal CalcEmaLast(List<decimal> closes, int period)
    {
        if (closes.Count < period) return closes.Last();
        var k   = 2m / (period + 1);
        var ema = closes.Take(period).Average();
        for (int i = period; i < closes.Count; i++)
            ema = closes[i] * k + ema * (1 - k);
        return ema;
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
