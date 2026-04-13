namespace OKXTradingBot.Core.Models;

public enum MarginMode { Cross, Isolated }

/// <summary>
/// 매매 전략 설정값
/// </summary>
public class TradeConfig
{
    // OKX API 인증
    public string ApiKey       { get; set; } = string.Empty;
    public string ApiSecret    { get; set; } = string.Empty;
    public string Passphrase   { get; set; } = string.Empty;

    // GPT API
    public string GptApiKey    { get; set; } = string.Empty;
    public string GptModel     { get; set; } = "gpt-4o-mini";
    public int    GptCandleCount { get; set; } = 30;       // GPT에 전달할 봉 개수
    public int    GptConfidenceThreshold { get; set; } = 60; // 신뢰도 필터 (기본 60)

    // 매매 기본 설정
    public string     Symbol      { get; set; } = "BTC-USDT-SWAP"; // OKX 무기한 선물 심볼
    public decimal    TotalBudget { get; set; } = 100m;   // 총 투자금액 (USDT) — 최대 투입 한도
    public int        Leverage    { get; set; } = 10;
    public MarginMode MarginMode  { get; set; } = MarginMode.Cross;

    /// <summary>OKX API에서 사용하는 마진 모드 문자열 (cross / isolated)</summary>
    public string MarginModeStr => MarginMode == MarginMode.Cross ? "cross" : "isolated";

    // 마틴게일 설정
    public int     MartinCount   { get; set; } = 9;     // 최대 분할 횟수
    public decimal MartinGap     { get; set; } = 0.5m;  // 추가 진입 트리거 가격 변동폭 (%) — 균등값
    public decimal TargetProfit  { get; set; } = 0.5m;  // 익절 기준 (%) — 균등값

    // 단계별 커스텀 파라미터 (비어있으면 균등값 사용)
    public List<decimal> MartinGapSteps    { get; set; } = new();  // 각 마틴 단계 진입 간격
    public List<decimal> TargetProfitSteps { get; set; } = new();  // 각 마틴 단계 목표 수익

    // 손절 설정
    public bool    StopLossEnabled { get; set; } = false;
    public decimal StopLossPercent { get; set; } = 3.0m; // 손절 기준 (%)

    // Telegram
    public string TelegramBotToken { get; set; } = string.Empty;
    public string TelegramChatId   { get; set; } = string.Empty;

    /// <summary>
    /// 1회 진입금액 자동 계산 (총 투자금 / 분할횟수)
    /// </summary>
    public decimal SingleOrderAmount => Math.Round(TotalBudget / MartinCount, 2);

    /// <summary>
    /// currentStep 단계에서 다음 진입을 트리거할 간격 반환 (1-based).
    /// 커스텀 목록이 있으면 해당 값, 없으면 균등값.
    /// </summary>
    public decimal GetMartinGapForStep(int currentStep)
    {
        if (MartinGapSteps.Count > 0)
        {
            var idx = Math.Clamp(currentStep - 1, 0, MartinGapSteps.Count - 1);
            return MartinGapSteps[idx];
        }
        return MartinGap;
    }

    /// <summary>
    /// currentStep 단계에서 적용할 목표수익 반환 (1-based).
    /// 커스텀 목록이 있으면 해당 값, 없으면 균등값.
    /// </summary>
    public decimal GetTargetProfitForStep(int currentStep)
    {
        if (TargetProfitSteps.Count > 0)
        {
            var idx = Math.Clamp(currentStep - 1, 0, TargetProfitSteps.Count - 1);
            return TargetProfitSteps[idx];
        }
        return TargetProfit;
    }
}
