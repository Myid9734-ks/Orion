using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OKXTradingBot.UI.Views;

namespace OKXTradingBot.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 앱 전체 모든 TextBox: 포커스 시 전체 선택
        TextBox.GotFocusEvent.AddClassHandler<TextBox>((tb, _) =>
            Dispatcher.UIThread.Post(tb.SelectAll));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
