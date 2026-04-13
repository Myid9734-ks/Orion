using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OKXTradingBot.UI.ViewModels;

namespace OKXTradingBot.UI.Views;

public partial class SymbolTabView : UserControl
{
    public SymbolTabView()
    {
        InitializeComponent();
    }

    private SymbolTabViewModel? Vm => DataContext as SymbolTabViewModel;

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
            Vm.MartinGapSteps = new List<decimal>();
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
            new List<decimal>(vm.MartinGapSteps));

        var result = await dialog.ShowDialog<StepParamsResult?>(parentWindow);
        if (result?.Confirmed == true)
            vm.MartinGapSteps = result.GapSteps;
    }
}
