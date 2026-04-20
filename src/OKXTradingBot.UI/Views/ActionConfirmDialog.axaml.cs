using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OKXTradingBot.UI.Views;

public partial class ActionConfirmDialog : Window
{
    public ActionConfirmDialog(string icon, string title, string body, string confirmText, string confirmColor)
    {
        InitializeComponent();

        IconText.Text        = icon;
        TitleText.Text       = title;
        BodyText.Text        = body;
        ConfirmButton.Content    = confirmText;
        ConfirmButton.Background = SolidColorBrush.Parse(confirmColor);
        ConfirmButton.Foreground = Brushes.White;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e)  => Close(false);
}
