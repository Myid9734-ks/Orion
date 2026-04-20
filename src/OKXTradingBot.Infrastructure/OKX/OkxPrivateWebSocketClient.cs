using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.OKX;

/// <summary>
/// OKX Private WebSocket — 주문 체결/algo 발동 알림 수신
/// wss://ws.okx.com:8443/ws/v5/private
///
/// 채널:
///   - orders-algo : trigger/conditional 주문 발동 시 알림
///   - orders      : 일반 주문 체결 알림 (시장가 진입 + algo 트리거 후 시장가 체결 모두 포함)
/// </summary>
public class OkxPrivateWebSocketClient : IAsyncDisposable
{
    private const string WsPrivate = "wss://ws.okx.com:8443/ws/v5/private";

    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _passphrase;
    private readonly ILogger<OkxPrivateWebSocketClient> _logger;

    private ClientWebSocket?       _ws;
    private CancellationTokenSource? _cts;
    private Task?                  _receiveTask;
    private bool                   _loggedIn; // 디버그/상태 추적용
    private string                 _instId = "";
    private string                 _instType = "SWAP";

    /// <summary>algo 주문 발동/체결 이벤트</summary>
    public event EventHandler<AlgoOrderFillEvent>? OnAlgoOrderFilled;

    /// <summary>raw 메시지 (디버그용)</summary>
    public event EventHandler<string>? OnRawMessage;

    public OkxPrivateWebSocketClient(
        string apiKey, string apiSecret, string passphrase,
        ILogger<OkxPrivateWebSocketClient> logger)
    {
        _apiKey     = apiKey;
        _apiSecret  = apiSecret;
        _passphrase = passphrase;
        _logger     = logger;
    }

    public async Task StartAsync(string instId, CancellationToken ct, string instType = "SWAP")
    {
        _instId   = instId;
        _instType = instType;
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws       = new ClientWebSocket();
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        _logger.LogInformation("[PrivWS] 연결 중: {url}", WsPrivate);
        await _ws.ConnectAsync(new Uri(WsPrivate), _cts.Token);
        _logger.LogInformation("[PrivWS] 연결 완료");

        await LoginAsync();

        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_receiveTask != null)
        {
            try { await _receiveTask; } catch { /* swallow */ }
        }
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None);
            }
            catch { /* swallow */ }
        }
        _logger.LogInformation("[PrivWS] 중지");
    }

    // ─────────────────────────────────────────────
    // Login
    // ─────────────────────────────────────────────
    private async Task LoginAsync()
    {
        var ts        = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var timestamp = ts.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var prehash   = timestamp + "GET" + "/users/self/verify";
        var sign      = SignBase64(prehash);

        var loginMsg = JsonSerializer.Serialize(new
        {
            op   = "login",
            args = new[]
            {
                new
                {
                    apiKey     = _apiKey,
                    passphrase = _passphrase,
                    timestamp,
                    sign
                }
            }
        });

        _logger.LogDebug("[PrivWS] login 요청 (timestamp={ts})", timestamp);
        await SendAsync(loginMsg);
    }

    private async Task SubscribeAsync()
    {
        // orders-algo + orders 동시 구독
        var subMsg = JsonSerializer.Serialize(new
        {
            op   = "subscribe",
            args = new object[]
            {
                new { channel = "orders-algo", instType = _instType, instId = _instId },
                new { channel = "orders",      instType = _instType, instId = _instId }
            }
        });
        _logger.LogInformation("[PrivWS] 구독 요청: orders-algo + orders | {instType}/{instId}",
            _instType, _instId);
        await SendAsync(subMsg);
    }

    // ─────────────────────────────────────────────
    // Receive Loop
    // ─────────────────────────────────────────────
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        _ = PingLoopAsync(ct);

        while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
        {
            try
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                var raw = sb.ToString();
                OnRawMessage?.Invoke(this, raw);
                ProcessMessage(raw);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                _logger.LogWarning("[PrivWS] WS 오류: {msg} — 5초 후 재연결", ex.Message);
                await Task.Delay(5000, ct);
                await ReconnectAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PrivWS] 수신 루프 예외");
            }
        }
    }

    private void ProcessMessage(string raw)
    {
        // pong 무시
        if (raw == "pong") return;

        try
        {
            var doc  = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // 이벤트 응답 (login / subscribe 결과)
            if (root.TryGetProperty("event", out var ev))
            {
                var evStr = ev.GetString();
                if (evStr == "login")
                {
                    var code = root.TryGetProperty("code", out var c) ? c.GetString() : "?";
                    if (code == "0")
                    {
                        _loggedIn = true;
                        _logger.LogInformation("[PrivWS] login 성공 — 채널 구독 시작");
                        _ = SubscribeAsync();
                    }
                    else
                    {
                        var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : "?";
                        _logger.LogError("[PrivWS] login 실패: code={code} msg={msg}", code, msg);
                    }
                }
                else if (evStr == "subscribe")
                {
                    var arg = root.TryGetProperty("arg", out var a) ? a.ToString() : "?";
                    _logger.LogInformation("[PrivWS] 구독 확인: {arg}", arg);
                }
                else if (evStr == "error")
                {
                    _logger.LogError("[PrivWS] 서버 에러: {raw}", raw);
                }
                return;
            }

            // 데이터 메시지
            if (!root.TryGetProperty("arg", out var argEl)) return;
            if (!root.TryGetProperty("data", out var data)) return;

            var channel = argEl.TryGetProperty("channel", out var ch) ? ch.GetString() : "";

            foreach (var item in data.EnumerateArray())
            {
                if (channel == "orders-algo")
                    HandleAlgoOrderUpdate(item);
                else if (channel == "orders")
                    HandleOrderUpdate(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[PrivWS] 메시지 파싱 실패: {msg} | raw={raw}", ex.Message, raw);
        }
    }

    private void HandleAlgoOrderUpdate(JsonElement item)
    {
        var state    = item.TryGetProperty("state", out var s) ? s.GetString() : "";
        var algoId   = item.TryGetProperty("algoId", out var a) ? a.GetString() : "";
        var ordType  = item.TryGetProperty("ordType", out var o) ? o.GetString() : "";
        var posSide  = item.TryGetProperty("posSide", out var p) ? p.GetString() : "";

        _logger.LogInformation("[PrivWS] orders-algo 업데이트: algoId={id} type={t} state={s} posSide={ps}",
            algoId, ordType, state, posSide);

        // state: "live" (등록), "effective" (발동→일반주문 생성), "canceled", "order_failed"
        // 실제 체결 정보는 'orders' 채널에서 전달됨
    }

    private void HandleOrderUpdate(JsonElement item)
    {
        var state    = item.TryGetProperty("state", out var s) ? s.GetString() : "";
        var ordId    = item.TryGetProperty("ordId", out var o) ? o.GetString() : "";
        var algoId   = item.TryGetProperty("algoId", out var a) ? a.GetString() : "";
        var posSide  = item.TryGetProperty("posSide", out var ps) ? ps.GetString() : "";
        var side     = item.TryGetProperty("side", out var sd) ? sd.GetString() : "";
        var fillPx   = item.TryGetProperty("fillPx", out var fp) ? fp.GetString() : "";
        var fillSz   = item.TryGetProperty("fillSz", out var fs) ? fs.GetString() : "";
        var instId   = item.TryGetProperty("instId", out var ii) ? ii.GetString() : "";
        var reduceOnly = item.TryGetProperty("reduceOnly", out var ro) && ro.GetString() == "true";

        _logger.LogInformation("[PrivWS] orders 업데이트: ordId={oid} algoId={aid} state={st} side={sd} posSide={ps} fillPx={fp} fillSz={fs} reduceOnly={ro}",
            ordId, algoId, state, side, posSide, fillPx, fillSz, reduceOnly);

        // filled 상태일 때만 이벤트 발사
        if (state != "filled" && state != "partially_filled") return;

        if (!decimal.TryParse(fillPx, out var pxDec)) pxDec = 0;
        if (!decimal.TryParse(fillSz, out var szDec)) szDec = 0;

        var direction = posSide == "long" ? TradeDirection.Long : TradeDirection.Short;

        var evt = new AlgoOrderFillEvent
        {
            AlgoId      = algoId ?? "",
            OrderId     = ordId  ?? "",
            Symbol      = instId ?? _instId,
            Direction   = direction,
            FilledPrice = pxDec,
            FilledSize  = szDec,
            IsClose     = reduceOnly,
            Timestamp   = DateTime.UtcNow
        };

        _logger.LogInformation("[PrivWS] ▶ FILL 이벤트 발사: {dir} {sz}@{px} (close={close}, algoId={aid})",
            direction, szDec, pxDec, reduceOnly, algoId);

        try { OnAlgoOrderFilled?.Invoke(this, evt); }
        catch (Exception ex) { _logger.LogError(ex, "[PrivWS] FILL 이벤트 핸들러 예외"); }
    }

    // ─────────────────────────────────────────────
    // Reconnect / Ping
    // ─────────────────────────────────────────────
    private async Task ReconnectAsync(CancellationToken ct)
    {
        try { _ws?.Dispose(); } catch { }
        _ws       = new ClientWebSocket();
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        _loggedIn = false;

        await _ws.ConnectAsync(new Uri(WsPrivate), ct);
        _logger.LogInformation("[PrivWS] 재연결 완료 — 재로그인");
        await LoginAsync();
        // SubscribeAsync는 login 성공 이벤트 후 자동 호출됨
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(25), ct);
            if (_ws?.State == WebSocketState.Open)
            {
                try { await SendAsync("ping"); }
                catch (Exception ex) { _logger.LogDebug("[PrivWS] ping 실패 (loggedIn={l}): {msg}", _loggedIn, ex.Message); }
            }
        }
    }

    private async Task SendAsync(string message)
    {
        var data = Encoding.UTF8.GetBytes(message);
        await _ws!.SendAsync(new ArraySegment<byte>(data),
            WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private string SignBase64(string prehash)
    {
        var key = Encoding.UTF8.GetBytes(_apiSecret);
        var msg = Encoding.UTF8.GetBytes(prehash);
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(msg));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { _ws?.Dispose(); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}
