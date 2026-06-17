using Avalonia.Data.Converters;
using FH6RB.Assets;

namespace FH6RB.Converters;

public static class Conv
{
    public static readonly FuncValueConverter<bool, double> EnabledOpacity =
        new(enabled => enabled ? 1.0 : 0.42);
    
    public static readonly IValueConverter ToggleToolTip =
        new FuncValueConverter<bool, string>(enabled => enabled ? Str.TipDisable : Str.TipEnable);
}
