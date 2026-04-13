using System.Net.Http;
using System.Text;
using System.Text.Json;
using OKXTradingBot.Core.Interfaces;

namespace OKXTradingBot.Infrastructure.Notifications;

/// <summary>
/// INotifier 구현 — Telegram Bot API
/// </summary>
public class TelegramNotifier : INotifier
{
    private readonly HttpClient _http = new();
    private readonly string _botToken;
    private readonly string _chatId;

    public TelegramNotifier(string botToken, string chatId)
    {
        _botToken = botToken;
        _chatId   = chatId;
    }

    public async Task SendAsync(string message)
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
