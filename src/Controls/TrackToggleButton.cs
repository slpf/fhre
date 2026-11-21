using Avalonia.Controls;
using Avalonia.Input;

namespace FH6RB.Controls;

public sealed class TrackToggleButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public KeyModifiers ClickModifiers { get; private set; }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ClickModifiers = e.KeyModifiers;
        try
        {
            base.OnPointerReleased(e);
        }
        finally
        {
            ClickModifiers = KeyModifiers.None;
        }
    }
}
