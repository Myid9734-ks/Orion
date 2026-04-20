using System.Net.Http;
using System.Text;
using System.Text.Json;
using OKXTradingBot.Core.Interfaces;
using OKXTradingBot.Core.Models;

namespace OKXTradingBot.Infrastructure.Notifications;

/// <summary>
/// INotifier 구현 — Telegram Bot API
/// 설정 탭의 알림 항목별 on/off + 수신 제한 시간(quiet hours) 반영
/// </summary>
public class TelegramNotifier : INotifier
{
    private readonly HttpClient _http = new();
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly NotificationConfig _config;

    public TelegramNotifier(string botToken, string chatId, NotificationConfig? config = null)
    {
        _botToken = botToken;
        _chatId   = chatId;
        _config   = config ?? new NotificationConfig { Enabled = true };
    }

    /// <summary>알림 유형 기반 전송 — 설정에 따라 필터링</summary>
    public async Task SendAsync(string message, NotificationType type)
    {
        if (!_config.ShouldSend(type))
            return;

        await SendInternalAsync(message);
    }

    /// <summary>유형 없이 전송 — 마스터 스위치 + quiet hours만 체크</summary>
    public async Task SendAsync(string message)
    {
        if (!_config.Enabled)
            return;

        if (_config.IsInQuietHours(DateTime.Now))
            return;

        await SendInternalAsync(message);
    }

    private async Task SendInternalAsync(string message)
    {
        if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_chatId))
            return;

        var url  = $"https://api.telegram.org/bot{_botToken}/sendMessage";
        var body = JsonSerializer.Serialize(new
        {
            chat_id    = _chatId,
            text       = message,
            parse_mode = "HTML"
        });

        var content = new StringContent(body, Encoding.UTF8, "application/json");
        await _http.PostAsync(url, content);
    }
}
