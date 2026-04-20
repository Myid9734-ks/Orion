using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OKXTradingBot.UI.Views;

public partial class ConfirmClearDialog : Window
{
    public ConfirmClearDialog()
    {
        InitializeComponent();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e)  => Close(false);
}
