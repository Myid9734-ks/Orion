using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OKXTradingBot.UI.Views;
using ReactiveUI;

namespace OKXTradingBot.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        RxApp.DefaultExceptionHandler = new ReactiveExceptionHandler();

        WriteAppLog("앱 시작");

        // 앱 전체 모든 TextBox: 포커스 시 전체 선택
        TextBox.GotFocusEvent.AddClassHandler<TextBox>((tb, _) =>
            Dispatcher.UIThread.Post(tb.SelectAll));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownRequested += (_, _) => WriteAppLog("앱 종료");
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void WriteAppLog(string label)
    {
        try
        {
            var logDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".okxtradingbot", "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "app.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {label}" + Environment.NewLine);
        }
        catch { }
    }

    private sealed class ReactiveExceptionHandler : IObserver<Exception>
    {
        public void OnNext(Exception ex)      => Program.WriteCrashLog("ReactiveUI", ex);
        public void OnError(Exception ex)     => Program.WriteCrashLog("ReactiveUI.OnError", ex);
        public void OnCompleted()             { }
    }
}
