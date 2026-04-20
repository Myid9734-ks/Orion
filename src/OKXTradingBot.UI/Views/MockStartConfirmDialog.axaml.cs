using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OKXTradingBot.UI.Views;

public partial class MockStartConfirmDialog : Window
{
    public MockStartConfirmDialog()
    {
        InitializeComponent();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
        => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(false);
}
