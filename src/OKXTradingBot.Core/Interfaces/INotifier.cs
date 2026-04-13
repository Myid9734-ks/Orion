namespace OKXTradingBot.Core.Interfaces;

public interface INotifier
{
    Task SendAsync(string message);
}
