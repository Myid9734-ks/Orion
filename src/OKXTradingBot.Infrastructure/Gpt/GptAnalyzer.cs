using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.Gpt;

/// <summary>
/// IGptAnalyzer 구현 — OpenAI API 호출
/// 최근 캔들 데이터를 분석하여 롱/숏 방향 + 신뢰도 반환
/// </summary>
public class GptAnalyzer : IGptAnalyzer
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<GptAnalyzer> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GptAnalyzer(string apiKey, string model, ILogger<GptAnalyzer> logger)
    {
        _apiKey = apiKey;
        _model  = string.IsNullOrEmpty(model) ? "gpt-4o-mini" : model;
        _logger = logger;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public async Task<GptAnalysisResult> AnalyzeAsync(List<Candle> candles, int confidenceThreshold)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            return new GptAnalysisResult
            {
                IsError      = true,
                ErrorMessage = "GPT API Key가 설정되지 않았습니다"
            };
        }

        if (candles.Count == 0)
        {
            return new GptAnalysisResult
            {
                IsError      = true,
                ErrorMessage = "캔들 데이터가 없습니다"
            };
        }

        try
        {
            var prompt = BuildPrompt(candles);
            var response = await CallOpenAiAsync(prompt);
            return ParseResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GPT 분석 실패");
            return new GptAnalysisResult
            {
                IsError      = true,
                ErrorMessage = ex.Message
            };
        }
    }

    // ─────────────────────────────────────────────
    // 프롬프트 생성
    // ─────────────────────────────────────────────

    private static string BuildPrompt(List<Candle> candles)
    {
        var sb = new StringBuilder();

        var lastClose = candles.Last().Close;
        var first     = candles.First().Close;
        var highMax   = candles.Max(c => c.High);
        var lowMin    = candles.Min(c => c.Low);
        var avgVol    = candles.Average(c => (double)c.Volume);
        var pct       = (lastClose - first) / first * 100;

        // 토큰 절약: 전달된 캔들 수 그대로 사용 (GptCandleCount 설정 반영)
        sb.AppendLine("OHLCV data (1-minute candles, oldest → newest):");
        sb.AppendLine("Time(UTC), Open, High, Low, Close, Volume");
        foreach (var c in candles)
        {
            sb.AppendLine($"{c.Timestamp:HH:mm}, {c.Open}, {c.High}, {c.Low}, {c.Close}, {c.Volume:F2}");
        }

        sb.AppendLine();
        sb.AppendLine($"Summary ({candles.Count} candles):");
        sb.AppendLine($"- Price change: {pct:+0.00;-0.00}%");
        sb.AppendLine($"- High: {highMax:N2}, Low: {lowMin:N2}");
        sb.AppendLine($"- Current: {lastClose:N2}");
        sb.AppendLine($"- Avg Volume: {avgVol:F2}");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────
    // OpenAI API 호출
    // ─────────────────────────────────────────────

    private async Task<string> CallOpenAiAsync(string userPrompt)
    {
        var systemPrompt =
            "You are a professional crypto futures trading analyst specializing in short-term momentum analysis. " +
            "Analyze the provided 1-minute OHLCV candlestick data and determine the best trading direction. " +
            "Consider: trend direction, momentum, volume patterns, support/resistance levels, and volatility. " +
            "You MUST respond with ONLY a valid JSON object — no markdown, no explanation outside JSON. " +
            "Response format: {\"direction\": \"long\" or \"short\", \"confidence\": <integer 0-100>, \"reason\": \"<brief reason in Korean, max 100 chars>\"}";

        var payload = new
        {
            model    = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userPrompt   }
            },
            max_tokens  = 150,
            temperature = 0.3
        };

        var body    = JsonSerializer.Serialize(payload);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("https://api.openai.com/v1/chat/completions", content);
        var json     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var hint = statusCode switch
            {
                401 => "API 키 오류",
                429 => "Rate Limit 초과 — 잠시 후 재시도",
                500 or 503 => "OpenAI 서버 일시 오류",
                _   => "알 수 없는 오류"
            };
            _logger.LogError("OpenAI API 오류 {code} ({hint}): {body}", statusCode, hint, json);
            throw new Exception($"OpenAI API {statusCode} ({hint})");
        }

        // choices[0].message.content 추출
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    // ─────────────────────────────────────────────
    // 응답 파싱
    // ─────────────────────────────────────────────

    private GptAnalysisResult ParseResponse(string raw)
    {
        // JSON 블록 추출 (```json ... ``` 감싸는 경우 대비)
        var text = raw.Trim();
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        if (start < 0 || end < 0)
            throw new Exception($"JSON 파싱 실패: {text}");

        text = text[start..(end + 1)];

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var directionStr = root.TryGetProperty("direction", out var d) ? d.GetString() ?? "" : "";
        var reason       = root.TryGetProperty("reason",    out var r) ? r.GetString() ?? "" : "";

        // confidence: 숫자 또는 문자열 모두 허용 (GPT 응답 형식 불일치 방어)
        int confidence = 0;
        if (root.TryGetProperty("confidence", out var c))
        {
            if (c.ValueKind == JsonValueKind.Number)
                confidence = c.GetInt32();
            else if (c.ValueKind == JsonValueKind.String)
                int.TryParse(c.GetString(), out confidence);
        }

        // "neutral" 등 예상 외 방향 → 오류가 아닌 신뢰도 0으로 처리 (스킵)
        var dirLower = directionStr.ToLower().Trim();
        if (dirLower != "long" && dirLower != "short")
        {
            _logger.LogInformation("GPT 방향 미확정: '{dir}' — 신뢰도 0으로 처리 (스킵)", directionStr);
            return new GptAnalysisResult
            {
                Direction  = TradeDirection.Long, // 기본값 (어차피 ShouldEnter=false)
                Confidence = 0,
                Reason     = $"방향 미확정: {directionStr}",
                AnalyzedAt = DateTime.UtcNow
            };
        }

        var direction = dirLower == "long" ? TradeDirection.Long : TradeDirection.Short;

        _logger.LogInformation("GPT 분석 결과: {dir} {conf}% — {reason}",
            direction, confidence, reason);

        return new GptAnalysisResult
        {
            Direction  = direction,
            Confidence = Math.Clamp(confidence, 0, 100),
            Reason     = reason,
            AnalyzedAt = DateTime.UtcNow
        };
    }
}
