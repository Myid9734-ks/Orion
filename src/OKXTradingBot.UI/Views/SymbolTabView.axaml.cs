using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OKXTradingBot.UI.ViewModels;

namespace OKXTradingBot.UI.Views;

public partial class SymbolTabView : UserControl
{
    public SymbolTabView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SymbolTabViewModel vm)
            {
                vm.ConfirmMockStart  = ShowMockStartConfirmAsync;
                vm.ConfirmStop       = ShowStopConfirmAsync;
                vm.ConfirmForceClose = ShowForceCloseConfirmAsync;
            }
        };

        var symbolCb = this.FindControl<ComboBox>("SymbolComboBox");
        if (symbolCb != null)
        {
            symbolCb.AddHandler(InputElement.TextInputEvent, OnSymbolTextInput, RoutingStrategies.Tunnel);
            symbolCb.AddHandler(InputElement.KeyDownEvent,   OnSymbolKeyDown,   RoutingStrategies.Tunnel);
            symbolCb.DropDownOpened += (_, _) => { if (Vm != null) Vm.SymbolSearch = ""; };
            symbolCb.DropDownClosed += (_, _) => { if (Vm != null) Vm.SymbolSearch = ""; };
        }
    }

    private void OnSymbolTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not ComboBox cb || !cb.IsDropDownOpen || Vm == null) return;
        if (!string.IsNullOrEmpty(e.Text))
            Vm.SymbolSearch += e.Text;
    }

    private void OnSymbolKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ComboBox cb || !cb.IsDropDownOpen || Vm == null) return;
        if (e.Key == Key.Back && Vm.SymbolSearch.Length > 0)
            Vm.SymbolSearch = Vm.SymbolSearch[..^1];
    }

    private SymbolTabViewModel? Vm => DataContext as SymbolTabViewModel;

    private async Task<bool> ShowMockStartConfirmAsync()
    {
        var parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (parentWindow == null) return true;

        var dialog = new MockStartConfirmDialog();
        var result = await dialog.ShowDialog<bool?>(parentWindow);
        return result == true;
    }

    private async Task<bool> ShowStopConfirmAsync()
    {
        var parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (parentWindow == null) return true;

        var dialog = new ActionConfirmDialog(
            icon:         "⏹",
            title:        "봇을 중지합니다",
            body:         "봇 루프가 멈추지만 예비주문 · 익절주문은 OKX 서버에 그대로 유지됩니다.\n\n서버에 등록된 주문이 계속 체결될 수 있습니다.",
            confirmText:  "중지",
            confirmColor: "#FF9800");
        var result = await dialog.ShowDialog<bool?>(parentWindow);
        return result == true;
    }

    private async Task<bool> ShowForceCloseConfirmAsync()
    {
        var parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (parentWindow == null) return true;

        var dialog = new ActionConfirmDialog(
            icon:         "🔴",
            title:        "포지션 강제 종료",
            body:         "예비주문 · 익절주문을 모두 취소한 뒤 봇을 중지합니다.\n\n포지션은 유지되며 자동 매매감지는 중단됩니다.",
            confirmText:  "강제 종료",
            confirmColor: "#E53935");
        var result = await dialog.ShowDialog<bool?>(parentWindow);
        return result == true;
    }

    private void OnMarginModeCross(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.MarginMode = "Cross";
    }

    private void OnMarginModeIsolated(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.MarginMode = "Isolated";
    }

    private async void OnOpenStepParams(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        await OpenStepParamsDialogAsync(Vm);
    }

    private async void OnModeIndicatorClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (Vm.HasCustomSteps)
            Vm.ResetCustomSteps();
        else
            await OpenStepParamsDialogAsync(Vm);
    }

    private async Task OpenStepParamsDialogAsync(SymbolTabViewModel vm)
    {
        var parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (parentWindow == null) return;

        var dialog = new StepParamsDialog(
            vm.MartinCount,
            vm.MartinGap,
            new List<decimal>(vm.MartinGapSteps),
            vm.TotalBudget,
            vm.AmountMode,
            new List<decimal>(vm.MartinAmountWeights));

        var result = await dialog.ShowDialog<StepParamsResult?>(parentWindow);
        if (result?.Confirmed == true)
        {
            vm.MartinGapSteps      = result.GapSteps;
            vm.MartinAmountWeights = result.AmountWeights;
            vm.AmountMode          = result.AmountMode;
        }
    }
}
