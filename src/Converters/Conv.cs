using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FH6RB.Converters;

public static class Conv
{
    public static readonly FuncValueConverter<bool, double> EnabledOpacity =
        new(enabled => enabled ? 1.0 : 0.42);

    public static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#23a55a"));
    public static readonly IBrush InterBrush = new SolidColorBrush(Color.Parse("#b5bac1"));
    
    public static readonly FuncValueConverter<bool, IBrush> PowerStroke =
        new(enabled => enabled ? GreenBrush : InterBrush);
}
