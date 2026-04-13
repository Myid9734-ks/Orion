using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.OKX;

/// <summary>
/// OKX REST API 클라이언트
/// 공식 문서: https://www.okx.com/docs-v5/en/
/// </summary>
public class OkxRestClient
{
    private const string BaseUrl = "https://www.okx.com";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _passphrase;
    private readonly ILogger<OkxRestClient> _logger;

    public OkxRestClient(string apiKey, string apiSecret, string passphrase,
                         ILogger<OkxRestClient> logger)
    {
        _apiKey     = apiKey;
        _apiSecret  = apiSecret;
        _passphrase = passphrase;
        _logger     = logger;
        _http       = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ─────────────────────────────────────────────
    // Public Market Data (인증 불필요)
    // ─────────────────────────────────────────────

    /// <summary>최근 N개 캔들 조회 (bar: 1m, 5m, 15m, 1H, 4H 등)</summary>
    public async Task<List<Candle>> GetCandlesAsync(string instId, int limit = 30, string bar = "1m")
    {
        var url = $"/api/v5/market/candles?instId={instId}&bar={bar}&limit={limit}";
        var json = await GetPublicAsync(url);

        var candles = new List<Candle>();
        var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");

        foreach (var item in data.EnumerateArray())
        {
            // OKX 응답: [ts, open, high, low, close, vol, volCcy, volCcyQuote, confirm]
            var ts    = long.Parse(item[0].GetString()!);
            candles.Add(new Candle
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                Open      = decimal.Parse(item[1].GetString()!),
                High      = decimal.Parse(item[2].GetString()!),
                Low       = decimal.Parse(item[3].GetString()!),
                Close     = decimal.Parse(item[4].GetString()!),
                Volume    = decimal.Parse(item[5].GetString()!)
            });
        }

        // OKX는 최신봉이 앞에 오므로 역순 정렬 (오래된 봉 → 최신 봉)
        candles.Reverse();
        return candles;
    }

    /// <summary>현재 티커 가격 조회</summary>
    public async Task<decimal> GetTickerPriceAsync(string instId)
    {
        var url  = $"/api/v5/market/ticker?instId={instId}";
        var json = await GetPublicAsync(url);
        var doc  = JsonDocument.Parse(json);
        var last = doc.RootElement.GetProperty("data")[0].GetProperty("last").GetString()!;
        return decimal.Parse(last);
    }

    // ─────────────────────────────────────────────
    // Private Account (인증 필요)
    // ─────────────────────────────────────────────

    /// <summary>USDT 잔고 조회</summary>
    public async Task<decimal> GetBalanceAsync()
    {
        var json = await GetPrivateAsync("/api/v5/account/balance?ccy=USDT");
        var doc  = JsonDocument.Parse(json);
        var details = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("details");

        foreach (var item in details.EnumerateArray())
        {
            if (item.GetProperty("ccy").GetString() == "USDT")
                return decimal.Parse(item.GetProperty("availBal").GetString()!);
        }
        return 0m;
    }

    /// <summary>레버리지 및 마진 모드 설정 (mgnMode: "cross" | "isolated")</summary>
    public async Task<bool> SetLeverageAsync(string instId, int leverage, string mgnMode = "cross")
    {
        var body = JsonSerializer.Serialize(new
        {
            instId,
            lever   = leverage.ToString(),
            mgnMode
        });

        var json = await PostPrivateAsync("/api/v5/account/set-leverage", body);
        var doc  = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("code").GetString() == "0";
    }

    /// <summary>시장가 주문 (무기한 선물)</summary>
    public async Task<OrderResult> PlaceMarketOrderAsync(
        string instId, string side, string posSide, decimal sz, string mgnMode = "cross")
    {
        // side: buy / sell
        // posSide: long / short (양방향 포지션 모드 기준)
        var body = JsonSerializer.Serialize(new
        {
            instId,
            tdMode  = mgnMode,
            side,
            posSide,
            ordType = "market",
            sz      = sz.ToString("F4")
        });

        var json   = await PostPrivateAsync("/api/v5/trade/order", body);
        var doc    = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("data")[0];
        var code   = doc.RootElement.GetProperty("code").GetString();

        if (code != "0")
        {
            var msg = result.GetProperty("sMsg").GetString();
            _logger.LogError("주문 실패: {msg}", msg);
            return new OrderResult { Success = false, ErrorMessage = msg };
        }

        return new OrderResult
        {
            Success = true,
            OrderId = result.GetProperty("ordId").GetString()!
        };
    }

    /// <summary>전체 포지션 청산 (시장가)</summary>
    public async Task<OrderResult> ClosePositionAsync(string instId, string posSide, string mgnMode = "cross")
    {
        var body = JsonSerializer.Serialize(new
        {
            instId,
            mgnMode,
            posSide
        });

        var json = await PostPrivateAsync("/api/v5/trade/close-position", body);
        var doc  = JsonDocument.Parse(json);
        var code = doc.RootElement.GetProperty("code").GetString();

        return new OrderResult { Success = code == "0" };
    }

    // ─────────────────────────────────────────────
    // HTTP 헬퍼
    // ─────────────────────────────────────────────

    private async Task<string> GetPublicAsync(string path)
    {
        var resp = await _http.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> GetPrivateAsync(string path)
    {
        var req = BuildPrivateRequest(HttpMethod.Get, path, "");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private async Task<string> PostPrivateAsync(string path, string body)
    {
        var req = BuildPrivateRequest(HttpMethod.Post, path, body);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// OKX 인증 헤더 생성
    /// 서명: Base64(HMAC-SHA256(timestamp + method + path + body, secretKey))
    /// </summary>
    private HttpRequestMessage BuildPrivateRequest(HttpMethod method, string path, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var methodStr = method.Method.ToUpper();
        var message   = timestamp + methodStr + path + body;
        var sign      = Sign(message);

        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("OK-ACCESS-KEY",       _apiKey);
        req.Headers.Add("OK-ACCESS-SIGN",      sign);
        req.Headers.Add("OK-ACCESS-TIMESTAMP", timestamp);
        req.Headers.Add("OK-ACCESS-PASSPHRASE", _passphrase);
        req.Headers.Add("x-simulated-trading", "0"); // 1이면 모의거래
        return req;
    }

    private string Sign(string message)
    {
        var key  = Encoding.UTF8.GetBytes(_apiSecret);
        var data = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }
}
