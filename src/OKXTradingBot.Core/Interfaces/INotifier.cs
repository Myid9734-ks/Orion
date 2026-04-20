using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Core.Interfaces;

public interface INotifier
{
    /// <summary>알림 유형 지정 전송 (NotificationConfig에서 필터링)</summary>
    Task SendAsync(string message, NotificationType type);

    /// <summary>유형 없이 전송 (하위호환 — 항상 전송 시도)</summary>
    Task SendAsync(string message);
}
