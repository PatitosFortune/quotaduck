using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using QuotaDuck.App.Presentation;

namespace QuotaDuck.App.Views;

/// <summary>Tone -> brush. Honours Windows High Contrast (plain system text colour). Colour is decoration only: every state is also text.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    private static readonly Brush Good = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush Warn = Frozen(0xB2, 0x6A, 0x00);
    private static readonly Brush Error = Frozen(0xC6, 0x28, 0x28);
    private static readonly Brush Muted = Frozen(0x75, 0x75, 0x75);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (SystemParameters.HighContrast)
        {
            return SystemColors.WindowTextBrush;
        }

        return value is Tone t
            ? t switch { Tone.Good => Good, Tone.Warn => Warn, Tone.Error => Error, Tone.Muted => Muted, _ => SystemColors.WindowTextBrush }
            : SystemColors.WindowTextBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

/// <summary>null / empty string -> Collapsed (or false with parameter "bool").</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value is not null && !(value is string s && s.Length == 0);
        if (parameter is "bool")
        {
            return has;
        }

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The small vector icons of the action buttons (16 x 16, stroked in the button's own foreground so disabled and High Contrast colours apply
/// automatically). Code-only: no image files, no font dependency.
/// </summary>
public static class ButtonIcons
{
    public static readonly Geometry Copy = Make("M6.8,5.5 H12.2 A1.3,1.3 0 0 1 13.5,6.8 V12.7 A1.3,1.3 0 0 1 12.2,14 H6.8 A1.3,1.3 0 0 1 5.5,12.7 V6.8 A1.3,1.3 0 0 1 6.8,5.5 Z M10.5,3.5 V3.2 A1.2,1.2 0 0 0 9.3,2 H3.7 A1.2,1.2 0 0 0 2.5,3.2 V10.3 A1.2,1.2 0 0 0 3.7,11.5 H4.5");

    public static readonly Geometry CopyBoth = Make("M7.7,6.5 H12.8 A1.2,1.2 0 0 1 14,7.7 V12.8 A1.2,1.2 0 0 1 12.8,14 H7.7 A1.2,1.2 0 0 1 6.5,12.8 V7.7 A1.2,1.2 0 0 1 7.7,6.5 Z M9.5,4.5 V4.2 A1.2,1.2 0 0 0 8.3,3 H4.2 A1.2,1.2 0 0 0 3,4.2 V8.3 A1.2,1.2 0 0 0 4.2,9.5 H4.5 M6.5,2 V1.8 A1.2,1.2 0 0 0 5.3,0.6 H2.2 A1.2,1.2 0 0 0 1,1.8 V4.9");

    public static readonly Geometry Refresh = Make("M13.2,8 A5.2,5.2 0 1 1 11.6,4.3 M13.4,2.2 V5.4 H10.2");

    public static readonly Geometry Check = Make("M3,8.5 L6.2,11.7 L13,4.8");

    public static readonly Geometry Warn = Make("M8,3 V9.2 M8,12 V12.6");

    private static Geometry Make(string data)
    {
        Geometry g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }
}

/// <summary>
/// The icon for a button whose Content is its status text. Parameter "copy" / "both" / "refresh" picks the resting icon;
/// "Copied" shows a check and "Not copied" a warning mark (shape, not colour, carries the result).
/// </summary>
public sealed class ButtonIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? string.Empty;
        return text switch
        {
            "Copied" => ButtonIcons.Check,
            "Not copied" => ButtonIcons.Warn,
            _ => (parameter as string) switch { "both" => ButtonIcons.CopyBoth, "refresh" => ButtonIcons.Refresh, _ => ButtonIcons.Copy },
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The visible text beside an icon. Icon-only at rest ("Copy", "Refresh", "Copied", "Not copied": the result is announced by the polite
/// notice); the cooldown keeps its seconds visible ("Refresh in 41 s" -> "41 s"); "Copy both" keeps its label ("both").
/// </summary>
public sealed class ButtonLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? string.Empty;
        return (parameter as string) switch
        {
            "both" => text,
            "refresh" => text == "Refresh" ? string.Empty : text.StartsWith("Refresh in ", StringComparison.Ordinal) ? text["Refresh in ".Length..] : text,
            _ => string.Empty,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
