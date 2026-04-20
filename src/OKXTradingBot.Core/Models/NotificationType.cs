namespace OKXTradingBot.Core.Models;

/// <summary>
/// 텔레그램 알림 유형 — 설정에서 항목별 on/off 가능
/// </summary>
public enum NotificationType
{
    BotStartStop,   // 봇 시작/중지
    Entry,          // 신규 진입
    Martin,         // 마틴게일 추가 진입
    TakeProfit,     // 익절 청산
    StopLoss,       // 손절 청산
    Error           // 오류 발생
}
