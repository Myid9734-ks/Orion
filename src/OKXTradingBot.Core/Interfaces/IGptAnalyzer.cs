using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Core.Interfaces;

/// <summary>
/// GPT 시장 분석 인터페이스
/// </summary>
public interface IGptAnalyzer
{
    /// <summary>
    /// 최근 캔들 데이터를 분석하여 매매 방향 및 신뢰도 반환
    /// </summary>
    Task<GptAnalysisResult> AnalyzeAsync(List<Candle> candles, int confidenceThreshold);
}
