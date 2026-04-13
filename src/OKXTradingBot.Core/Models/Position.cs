namespace OKXTradingBot.Core.Models;

public enum TradeDirection { Long, Short }
public enum PositionStatus { None, Open, Closed }

/// <summary>
/// 현재 진행 중인 포지션 (마틴게일 사이클 단위)
/// </summary>
public class Position
{
    public TradeDirection Direction     { get; set; }
    public PositionStatus Status        { get; set; } = PositionStatus.None;
    public int            MartinStep    { get; set; } = 0; // 현재 마틴 단계 (0 = 미진입)
    public decimal        TotalAmount   { get; set; } = 0; // 누적 진입 금액 (USDT)
    public decimal        TotalQuantity { get; set; } = 0; // 누적 수량 (계약)
    public decimal        AvgEntryPrice { get; set; } = 0; // 평균 진입가
    public decimal        LastEntryPrice { get; set; } = 0; // 직전 진입가 (마틴 간격 계산용)
    public DateTime       OpenedAt      { get; set; }
    public DateTime?      ClosedAt      { get; set; }
    public decimal        RealizedPnl   { get; set; } = 0; // 실현 손익 (USDT)

    /// <summary>
    /// 현재 가격 기준 미실현 손익률 (%)
    /// </summary>
    public decimal GetUnrealizedPnlPercent(decimal currentPrice)
    {
        if (AvgEntryPrice == 0) return 0;
        return Direction == TradeDirection.Long
            ? (currentPrice - AvgEntryPrice) / AvgEntryPrice * 100
            : (AvgEntryPrice - currentPrice) / AvgEntryPrice * 100;
    }

    /// <summary>
    /// 다음 마틴 진입 트리거 가격 계산
    /// </summary>
    public decimal GetNextMartinTriggerPrice(decimal martinGapPercent)
    {
        if (LastEntryPrice == 0) return 0;
        return Direction == TradeDirection.Long
            ? LastEntryPrice * (1 - martinGapPercent / 100)
            : LastEntryPrice * (1 + martinGapPercent / 100);
    }
}
