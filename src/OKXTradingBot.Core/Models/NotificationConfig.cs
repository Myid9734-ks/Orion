namespace OKXTradingBot.Core.Models;

/// <summary>
/// 텔레그램 알림 설정 — 항목별 on/off + 수신 제한 시간(quiet hours)
/// </summary>
public class NotificationConfig
{
    public bool Enabled { get; set; } = false;

    // 항목별 알림 on/off
    public bool NotifyBotStartStop { get; set; } = true;
    public bool NotifyEntry        { get; set; } = true;
    public bool NotifyMartin       { get; set; } = true;
    public bool NotifyTakeProfit   { get; set; } = true;
    public bool NotifyStopLoss     { get; set; } = true;
    public bool NotifyError        { get; set; } = true;

    // 수신 제한 시간
    public bool   QuietHoursEnabled { get; set; } = false;
    public string QuietStart        { get; set; } = "23:00";
    public string QuietEnd          { get; set; } = "07:00";

    /// <summary>
    /// 해당 알림 유형이 활성화되어 있는지 확인
    /// </summary>
    public bool IsTypeEnabled(NotificationType type) => type switch
    {
        NotificationType.BotStartStop => NotifyBotStartStop,
        NotificationType.Entry        => NotifyEntry,
        NotificationType.Martin       => NotifyMartin,
        NotificationType.TakeProfit   => NotifyTakeProfit,
        NotificationType.StopLoss     => NotifyStopLoss,
        NotificationType.Error        => NotifyError,
        _                             => true
    };

    /// <summary>
    /// 현재 시각이 수신 제한 시간(quiet hours) 내인지 확인
    /// </summary>
    public bool IsInQuietHours(DateTime now)
    {
        if (!QuietHoursEnabled) return false;

        if (!TimeSpan.TryParse(QuietStart, out var start) ||
            !TimeSpan.TryParse(QuietEnd,   out var end))
            return false;

        var current = now.TimeOfDay;

        // 자정을 걸치는 경우 (예: 23:00 ~ 07:00)
        if (start > end)
            return current >= start || current < end;

        // 같은 날 (예: 01:00 ~ 06:00)
        return current >= start && current < end;
    }

    /// <summary>
    /// 해당 알림을 전송해야 하는지 종합 판단 (마스터 스위치 + 유형 + quiet hours)
    /// </summary>
    public bool ShouldSend(NotificationType type, DateTime? now = null)
    {
        if (!Enabled) return false;
        if (!IsTypeEnabled(type)) return false;
        if (IsInQuietHours(now ?? DateTime.Now)) return false;
        return true;
    }
}
