using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QuotaLens.Core;
using Windows.UI;

namespace QuotaLens.Helpers;

/// <summary>
/// Fluent-system severity colors keyed by AVAILABLE %, matching the original
/// CSS thresholds (&lt;=5 danger, &lt;=25 warning, &lt;=50 notice/busy, else success).
/// We use Windows system palette colors so the result feels native.
/// </summary>
public static class SeverityColors
{
    // Fallbacks (used only if the Fluent theme brushes can't be resolved).
    public static readonly Color Critical = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C); // red
    public static readonly Color Warning = Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00);  // amber
    public static readonly Color Busy = Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8);     // accent blue
    public static readonly Color Good = Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F);     // green

    public static Color For(Severity s) => s switch
    {
        Severity.Critical => Critical,
        Severity.Warning => Warning,
        Severity.Busy => Busy,
        _ => Good,
    };
}

/// <summary>Severity → SolidColorBrush (for percent text + status dot).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var sev = value is Severity s ? s : Severity.Good;
        // A natural quota-health ramp using the native Fluent status palette:
        // plenty → green, moderate → accent, low → amber, critical → red.
        // All are theme-aware brushes pulled from the system, with literal fallbacks.
        switch (sev)
        {
            case Severity.Critical:
                return Resolve("SystemFillColorCriticalBrush", SeverityColors.Critical);
            case Severity.Warning:
                return Resolve("SystemFillColorCautionBrush", SeverityColors.Warning);
            case Severity.Busy:
                // Moderate headroom — informational accent (text token is tuned for
                // contrast on card surfaces in both light and dark themes).
                return Resolve("AccentTextFillColorPrimaryBrush", SeverityColors.Busy);
            default: // Good → plenty of headroom.
                return Resolve("SystemFillColorSuccessBrush", SeverityColors.Good);
        }
    }

    /// <summary>Resolve a theme brush by key, falling back to a literal color.</summary>
    private static Brush Resolve(string key, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Available % (0–100) → a GridLength star width for the filled portion of a
/// custom progress track. Used with a paired "remainder" converter to draw a
/// severity-colored determinate bar. (We also keep the ProgressBar foreground in
/// sync via SeverityToBrushConverter.)
/// </summary>
public sealed class PercentToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var pct = ToDouble(value);
        // "remainder" inverts to draw the empty side of the track.
        var remainder = parameter is string p && p == "remainder";
        var v = remainder ? 100.0 - pct : pct;
        return new GridLength(Math.Clamp(v, 0.0, 100.0), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static double ToDouble(object v) => v switch
    {
        double d => d,
        float f => f,
        int i => i,
        _ => 0.0,
    };
}

/// <summary>bool → Visibility. Pass "invert" as parameter to flip.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool flag && flag;
        if (parameter is string p && p == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var vis = value is Visibility v && v == Visibility.Visible;
        if (parameter is string p && p == "invert") vis = !vis;
        return vis;
    }
}

/// <summary>null/empty string → Collapsed, otherwise Visible. "invert" flips.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var has = value is string s && !string.IsNullOrWhiteSpace(s);
        if (parameter is string p && p == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>null → Collapsed, non-null → Visible. "invert" flips.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var has = value != null;
        if (parameter is string p && p == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Uri/string → image source for file and package image bindings.</summary>
public sealed class UriToImageSourceConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var uri = value switch
        {
            Uri existingUri => existingUri,
            string s when !string.IsNullOrWhiteSpace(s) => new Uri(s),
            _ => null,
        };

        if (uri == null)
            return null;

        var key = uri.AbsoluteUri;
        if (!ImageCache.TryGetValue(key, out var image))
        {
            image = key.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? new SvgImageSource(uri)
                : new BitmapImage(uri);
            ImageCache[key] = image;
        }

        return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
