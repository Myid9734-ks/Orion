using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OKXTradingBot.UI.Views;

public enum UnsavedChangesResult { Save, Discard, Cancel }

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
        => Close(UnsavedChangesResult.Save);

    private void OnDiscard(object? sender, RoutedEventArgs e)
        => Close(UnsavedChangesResult.Discard);

    private void OnCancel(object? sender, RoutedEventArgs e)
        => Close(UnsavedChangesResult.Cancel);
}
