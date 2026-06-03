using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OKXTradingBot.UI.Views;

public class NoWheelNumericUpDown : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    public NoWheelNumericUpDown()
    {
        AddHandler(PointerWheelChangedEvent, OnWheelBlocked,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    private static void OnWheelBlocked(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }
}
