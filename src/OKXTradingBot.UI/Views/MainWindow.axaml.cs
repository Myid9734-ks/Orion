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
        if (!ReferenceEquals(e.Source, MainTabControl)) return;  // ComboBox 등 자식 이벤트 버블업 무시
        if (DataContext is not MainWindowViewModel vm) return;

        var fromIndex = _previousTabIndex;
        _previousTabIndex = MainTabControl.SelectedIndex;

        // 설정 탭(index 2)에서 나가는 경우만 체크
        if (fromIndex != 2) return;

        // 전역 설정 변경사항이 없으면 다이얼로그 생략
        if (!vm.HasGlobalUnsavedChanges) return;

        // 다이얼로그 표시 동안 설정 탭으로 복귀
        _isSwitchingTab = true;
        MainTabControl.SelectedIndex = fromIndex;
        _previousTabIndex = fromIndex;
        _isSwitchingTab = false;

        var dialog = new UnsavedChangesDialog();
        var result = await dialog.ShowDialog<UnsavedChangesResult>(this);

        var targetIndex = GetAddedIndex(e);
        switch (result)
        {
            case UnsavedChangesResult.Save:
                vm.SaveSettingsCommand.Execute().Subscribe();
                _isSwitchingTab = true;
                MainTabControl.SelectedIndex = targetIndex;
                _previousTabIndex = targetIndex;
                _isSwitchingTab = false;
                break;

            case UnsavedChangesResult.Discard:
                vm.DiscardChanges();
                _isSwitchingTab = true;
                MainTabControl.SelectedIndex = targetIndex;
                _previousTabIndex = targetIndex;
                _isSwitchingTab = false;
                break;

            case UnsavedChangesResult.Cancel:
            default:
                // 설정 탭에 머무름 (_previousTabIndex 이미 fromIndex로 복귀됨)
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

    private async void OnClearDataClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var dialog = new ConfirmClearDialog();
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (confirmed) vm.ClearData();
    }
}
