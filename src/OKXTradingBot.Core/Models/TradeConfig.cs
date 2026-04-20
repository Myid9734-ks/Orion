namespace OKXTradingBot.Core.Models;

public enum MarginMode { Cross, Isolated }

/// <summary>마틴게일 금액 배분 모드</summary>
public enum MartinAmountMode
{
    /// <summary>균등 분할 (총예산 / 단계수)</summary>
    Equal,
    /// <summary>배수 증가 (1×, 2×, 4×, 8× …)</summary>
    Multiplier,
    /// <summary>피보나치 비율 (1, 1, 2, 3, 5, 8, 13 …)</summary>
    Fibonacci
}

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
    public int    GptCandleCount { get; set; } = 30;        // GPT에 전달할 봉 개수
    public int    GptConfidenceThreshold { get; set; } = 60; // 신뢰도 필터 (기본 60)
    public int    GptAnalysisInterval { get; set; } = 5;    // GPT 분석 간격 (분), 0=매캔들

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

    // 금액 배분
    public MartinAmountMode AmountMode { get; set; } = MartinAmountMode.Equal;

    // 단계별 커스텀 파라미터 (비어있으면 균등값 사용)
    public List<decimal> MartinGapSteps    { get; set; } = new();  // 각 마틴 단계 진입 간격
    public List<decimal> TargetProfitSteps { get; set; } = new();  // 각 마틴 단계 목표 수익

    /// <summary>
    /// 단계별 가중치 (배수 모드: 1,2,4,8… / 피보나치 모드: 1,1,2,3,5…).
    /// 비어있으면 균등 모드. 금액은 가중치 비율로 총 투자금 배분.
    /// </summary>
    public List<decimal> MartinAmountWeights { get; set; } = new();

    // 손절 설정
    public bool    StopLossEnabled { get; set; } = false;
    public decimal StopLossPercent { get; set; } = 3.0m; // 손절 기준 (%)

    // Telegram
    public string TelegramBotToken { get; set; } = string.Empty;
    public string TelegramChatId   { get; set; } = string.Empty;

    /// <summary>
    /// 1회 진입금액 자동 계산 (총 투자금 / 분할횟수) — 균등 모드 전용
    /// </summary>
    public decimal SingleOrderAmount => Math.Round(TotalBudget / MartinCount, 2);

    /// <summary>
    /// step 단계의 진입 금액 반환 (1-based).
    /// 가중치가 있으면 비율 배분, 없으면 균등.
    /// </summary>
    public decimal GetAmountForStep(int step)
    {
        var amounts = GetAllStepAmounts();
        var idx = Math.Clamp(step - 1, 0, amounts.Count - 1);
        return amounts[idx];
    }

    /// <summary>
    /// 전체 단계별 금액 리스트.
    /// 가중치가 있으면 가중치 비율로 총 투자금 배분, 없으면 균등.
    /// </summary>
    public List<decimal> GetAllStepAmounts()
    {
        if (MartinAmountWeights.Count > 0)
            return WeightsToAmounts(MartinAmountWeights);
        return Enumerable.Repeat(SingleOrderAmount, MartinCount).ToList();
    }

    /// <summary>가중치 리스트 → USDT 금액 리스트 변환</summary>
    public List<decimal> WeightsToAmounts(List<decimal> weights)
    {
        var totalW = weights.Sum();
        if (totalW <= 0) return Enumerable.Repeat(SingleOrderAmount, MartinCount).ToList();
        return weights.Select(w => Math.Round(TotalBudget * w / totalW, 2)).ToList();
    }

    /// <summary>
    /// 배수 리스트(이전 단계 대비 배수) → 절대 가중치 변환
    /// 예) [1, 2, 3] → [1, 2, 6]
    /// </summary>
    public static List<decimal> MultipliersToAbsoluteWeights(List<decimal> multipliers)
    {
        var result = new List<decimal>();
        decimal cum = multipliers.Count > 0 ? multipliers[0] : 1m;
        result.Add(Math.Round(cum, 4));
        for (int i = 1; i < multipliers.Count; i++)
        {
            cum *= multipliers[i];
            result.Add(Math.Round(cum, 4));
        }
        return result;
    }

    /// <summary>
    /// 절대 가중치 → 배수 리스트(이전 단계 대비 배수) 변환
    /// 예) [1, 2, 6] → [1, 2, 3]
    /// </summary>
    public static List<decimal> AbsoluteWeightsToMultipliers(List<decimal> absWeights)
    {
        var result = new List<decimal>();
        for (int i = 0; i < absWeights.Count; i++)
        {
            if (i == 0)
                result.Add(1m); // 1회차는 기준값
            else
                result.Add(absWeights[i - 1] == 0 ? 1m : Math.Round(absWeights[i] / absWeights[i - 1], 1));
        }
        return result;
    }

    /// <summary>
    /// 프리셋 기본 가중치 생성 (다이얼로그 자동 채움용)
    /// </summary>
    public static List<decimal> GeneratePresetWeights(MartinAmountMode mode, int count)
    {
        switch (mode)
        {
            case MartinAmountMode.Multiplier:
            {
                // 기본값: 1회차 기준(1), 이후 단계별 2배씩
                var weights = new List<decimal> { 1m };
                for (int i = 1; i < count; i++)
                    weights.Add(2m);
                return weights;
            }
            case MartinAmountMode.Fibonacci:
            {
                var fibs = new List<decimal>();
                decimal a = 1, b = 1;
                for (int i = 0; i < count; i++)
                {
                    fibs.Add(a);
                    var next = a + b;
                    a = b;
                    b = next;
                }
                return fibs;
            }
            default:
                return Enumerable.Repeat(1m, count).ToList();
        }
    }

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
