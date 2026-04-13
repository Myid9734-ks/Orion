namespace OKXTradingBot.Core.Models;

/// <summary>
/// GPT API 분석 응답 모델
/// </summary>
public class GptAnalysisResult
{
    public TradeDirection Direction  { get; set; }
    public int            Confidence { get; set; } // 0~100
    public string         Reason     { get; set; } = string.Empty;
    public bool           ShouldEnter => Confidence >= 60; // 신뢰도 필터
    public DateTime       AnalyzedAt { get; set; } = DateTime.UtcNow;
}
