using Avalonia.Controls;
using Avalonia.Input;

namespace OKXTradingBot.UI.Views;

public class NoWheelNumericUpDown : NumericUpDown
{
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        e.Handled = true;
    }
}
