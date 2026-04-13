using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.OKX;

/// <summary>
/// OKX WebSocket 클라이언트 — 1분봉 실시간 수신
/// Public WS: wss://ws.okx.com:8443/ws/v5/public
/// </summary>
public class OkxWebSocketClient : IAsyncDisposable
{
    private const string WsPublic   = "wss://ws.okx.com:8443/ws/v5/public";
    private const string WsBusiness = "wss://ws.okx.com:8443/ws/v5/business";

    private ClientWebSocket? _ws;
    private readonly ILogger<OkxWebSocketClient> _logger;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // 완성된 캔들 이벤트
    public event EventHandler<Candle>? OnCandleCompleted;
    // 미완성 캔들 실시간 업데이트 이벤트
    public event EventHandler<Candle>? OnCandleUpdated;
    // 실시간 마지막 가격 이벤트
    public event EventHandler<decimal>? OnPriceUpdated;

    // 현재 수신 중인 미완성 봉 (봉 완성 감지용)
    private Candle? _currentCandle;
    private string  _bar = "1m";

    public OkxWebSocketClient(ILogger<OkxWebSocketClient> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(string instId, CancellationToken ct, string bar = "1m")
    {
        _bar = bar;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ws  = new ClientWebSocket();
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        // 캔들 채널은 business 엔드포인트, 나머지는 public
        var wsUrl = bar != null && bar != "" ? WsBusiness : WsPublic;
        _logger.LogInformation("WebSocket 연결 중: {url}", wsUrl);
        await _ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
        _logger.LogInformation("WebSocket 연결 완료");

        // 봉 채널 구독
        var channel      = $"candle{bar}";
        var subscribeMsg = JsonSerializer.Serialize(new
        {
            op   = "subscribe",
            args = new[] { new { channel, instId } }
        });
        await SendAsync(subscribeMsg);

        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_receiveTask != null)
            await _receiveTask;
        if (_ws?.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        // Ping 유지 — 루프 밖에서 딱 한 번만 시작
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
                ProcessMessage(raw);
            }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex)
            {
                _logger.LogWarning("WebSocket 오류: {msg} — 5초 후 재연결", ex.Message);
                await Task.Delay(5000, ct);
                await ReconnectAsync(ct);
            }
        }
    }

    public event EventHandler<string>? OnRawMessage;

    private void ProcessMessage(string raw)
    {
        OnRawMessage?.Invoke(this, raw); // 디버그용 raw 메시지 전달
        if (raw.Contains("\"event\"")) return; // subscribe 확인 메시지 무시

        try
        {
            var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;

            foreach (var item in data.EnumerateArray())
            {
                // OKX candle1m 응답: [ts, open, high, low, close, vol, volCcy, volCcyQuote, confirm]
                var ts      = long.Parse(item[0].GetString()!);
                var confirm = item[8].GetString(); // "1" = 완성된 봉

                var candle = new Candle
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                    Open      = decimal.Parse(item[1].GetString()!),
                    High      = decimal.Parse(item[2].GetString()!),
                    Low       = decimal.Parse(item[3].GetString()!),
                    Close     = decimal.Parse(item[4].GetString()!),
                    Volume    = decimal.Parse(item[5].GetString()!)
                };

                // 현재가 업데이트
                OnPriceUpdated?.Invoke(this, candle.Close);
                // 미완성 캔들 실시간 이벤트 (차트 실시간 업데이트용)
                OnCandleUpdated?.Invoke(this, candle);

                // 봉 완성 시 이벤트 발생
                if (confirm == "1")
                {
                    _logger.LogDebug("봉 완성: {candle}", candle);
                    OnCandleCompleted?.Invoke(this, candle);
                }

                _currentCandle = candle;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("메시지 파싱 오류: {msg}", ex.Message);
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await _ws.ConnectAsync(new Uri(WsBusiness), ct);
        _logger.LogInformation("WebSocket 재연결 완료");
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(25), ct);
            if (_ws?.State == WebSocketState.Open)
                await SendAsync("ping");
        }
    }

    private async Task SendAsync(string message)
    {
        var data = Encoding.UTF8.GetBytes(message);
        await _ws!.SendAsync(new ArraySegment<byte>(data),
            WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
