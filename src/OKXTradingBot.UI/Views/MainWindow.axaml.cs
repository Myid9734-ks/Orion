using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OKXTradingBot.UI.ViewModels;

namespace OKXTradingBot.UI.Views;

public partial class MainWindow : Window
{
    private int  _previousTabIndex = 0;
    private bool _isSwitchingTab   = false;
    private string? _textBoxSnapshot;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();

        this.AddHandler(TextBox.LostFocusEvent, OnTextBoxLostFocus, RoutingStrategies.Bubble);
        this.AddHandler(TextBox.GotFocusEvent,  OnTextBoxGotFocus,  RoutingStrategies.Bubble);
    }

    private void OnTextBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is not TextBox tb) return;
        _textBoxSnapshot = tb.Text;
    }

    private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TextBox tb) return;
        if (string.IsNullOrEmpty(tb.Text))
            tb.Text = _textBoxSnapshot;
        _textBoxSnapshot = null;
    }

    // ── 메인 탭 변경 (트레이딩/수익률/설정) ───────────────────────────

    private async void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isSwitchingTab) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.HasUnsavedChanges) return;

        // 설정 탭(index 2)에서 나가는 경우만 체크
        if (_previousTabIndex != 2)
        {
            _previousTabIndex = MainTabControl.SelectedIndex;
            return;
        }

        _isSwitchingTab = true;
        MainTabControl.SelectedIndex = _previousTabIndex;
        _isSwitchingTab = false;

        var dialog = new UnsavedChangesDialog();
        var result = await dialog.ShowDialog<UnsavedChangesResult>(this);

        switch (result)
        {
            case UnsavedChangesResult.Save:
                vm.SaveSettingsCommand.Execute().Subscribe();
                _isSwitchingTab = true;
                MainTabControl.SelectedIndex = GetAddedIndex(e);
                _previousTabIndex = MainTabControl.SelectedIndex;
                _isSwitchingTab = false;
                break;

            case UnsavedChangesResult.Discard:
                vm.DiscardChanges();
                _isSwitchingTab = true;
                MainTabControl.SelectedIndex = GetAddedIndex(e);
                _previousTabIndex = MainTabControl.SelectedIndex;
                _isSwitchingTab = false;
                break;

            case UnsavedChangesResult.Cancel:
            default:
                break;
        }
    }

    private int GetAddedIndex(SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is TabItem tab)
            return MainTabControl.Items.IndexOf(tab);
        return 0;
    }

    // ── 심볼 탭 선택 / 닫기 ──────────────────────────────────────────

    private void OnSymbolTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not SymbolTabViewModel tab) return;
        if (DataContext is MainWindowViewModel vm)
            vm.SelectedSymbolTab = tab;
    }

    private void OnSymbolTabClose(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not SymbolTabViewModel tab) return;
        if (DataContext is MainWindowViewModel vm)
            vm.RemoveSymbolTabCommand.Execute(tab).Subscribe();
    }
}
