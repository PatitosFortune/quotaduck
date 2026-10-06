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
