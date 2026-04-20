namespace OKXTradingBot.Core.Models;

/// <summary>
/// GPT API 분석 응답 모델
/// </summary>
public class GptAnalysisResult
{
    public TradeDirection Direction  { get; set; }
    public int            Confidence { get; set; } // 0~100
    public string         Reason     { get; set; } = string.Empty;
    public DateTime       AnalyzedAt { get; set; } = DateTime.UtcNow;
    public bool           IsError    { get; set; } = false;
    public string         ErrorMessage { get; set; } = string.Empty;

    /// <summary>신뢰도가 임계값 이상인지 (진입 여부 판단)</summary>
    public bool ShouldEnter(int threshold = 60) => !IsError && Confidence >= threshold;
}
