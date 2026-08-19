using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace QuotaLens.Helpers;

/// <summary>
/// Per-provider visual identity: a short monogram + a brand accent color used for the
/// card/hero avatar chip. These are IDENTITY colors (for scannability), kept distinct
/// from the status-severity palette so a provider's brand isn't confused with its health.
/// </summary>
public static class Brand
{
    public const byte AmbientTintLeadAlpha = 0x38;
    public const byte AmbientTintMidAlpha = 0x1A;

    private static readonly Dictionary<string, (string Mono, uint Hex)> Map = new()
    {
        ["codex-lb"]    = ("lb", 0x7FC8D3), // pooled Codex teal
        ["codex"]       = ("Cx", 0x49A3B0), // Codex teal
        ["copilot"]     = ("Cp", 0x8534F3), // Copilot purple
        ["gemini"]      = ("Gm", 0x4285F4), // Google blue
        ["bedrock"]     = ("Br", 0xFF9900), // AWS orange
        ["vertexai"]    = ("Vx", 0xAECBFA), // Vertex AI light blue
        ["claude"]      = ("Cl", 0xD97757), // Claude clay
        ["deepseek"]    = ("DS", 0x4D6BFE), // blue
        ["kiro"]        = ("K", 0x9046FF), // Kiro purple
        ["alibaba"]     = ("Al", 0xFF6A00), // Alibaba Cloud orange
        ["alibabacloud"] = ("Ac", 0xFF6A00), // Alibaba Cloud orange
        ["alibabatokenplan"] = ("At", 0xFF6A00), // Alibaba Cloud orange
        ["antigravity"] = ("Ag", 0x749BFF), // Antigravity periwinkle
        ["bayesdl"]     = ("Bd", 0x3273F9), // BayesDL portal blue
        ["mimo"]        = ("Mi", 0xFB8147), // MiMo orange
        ["qoder"]       = ("Q", 0x2ADB5C), // Qoder green
        ["kimi"]        = ("Ki", 0x1166C7), // Kimi KMBlue (deepened)
        ["amp"]         = ("A", 0xF34E3F), // Amp brand red
        ["cursor"]      = ("Cu", 0xC08532), // Cursor bronze (brand is mono)
        ["augment"]     = ("Au", 0x1AA049), // Augment green
        ["factory"]     = ("F", 0xEE6018), // Factory orange
        ["minimax"]     = ("Mx", 0xF23F5D), // MiniMax red
        ["windsurf"]    = ("Ws", 0x09B6A2), // Windsurf teal
        ["openrouter"]  = ("OR", 0x7624F4), // OpenRouter violet
        ["moonshot"]    = ("Mo", 0x0A7AFF), // Moonshot blue
        ["venice"]      = ("Ve", 0x3C8FDD), // Venetian blue
        ["crof"]        = ("Cr", 0x6E52ED), // Crof violet
        ["openai"]      = ("OA", 0x10A37F), // OpenAI green
        ["azureopenai"] = ("Az", 0x0078D4), // Azure blue
        ["elevenlabs"]  = ("11", 0xEBEBE6), // ElevenLabs warm off-white
        ["warp"]        = ("W", 0x00C2FF), // Warp cursor cyan
        ["codebuff"]    = ("Cb", 0x9EFC62), // Codebuff green
        ["synthetic"]   = ("Sy", 0x6366F1), // Synthetic indigo
        ["zai"]         = ("z", 0x3762FF), // z.ai blue
        ["zcode"]       = ("Z", 0x3762FF), // ZCode blue
        ["llmproxy"]    = ("LP", 0x24B47E), // proxy green
        ["doubao"]      = ("Db", 0x0066FF), // Doubao blue
        ["groq"]        = ("Gq", 0xF43E01), // Groq orange-red
        ["deepgram"]    = ("Dg", 0x13EF93), // Deepgram green
        ["grok"]        = ("Gk", 0xDC5607), // xAI sunset (nudged off Alibaba)
        ["kilo"]        = ("Kl", 0xFFE600), // Kilo yellow
        ["jetbrains"]   = ("JB", 0xFC1D69), // JetBrains AI magenta
        ["kimik2"]      = ("K2", 0x1247D6), // Kimi K2 deep blue
        ["manus"]       = ("Ma", 0x0081F2), // Manus blue
        ["perplexity"]  = ("P", 0x20808D), // Perplexity teal
        ["t3chat"]      = ("T3", 0xA23B67), // T3 Chat plum
        ["commandcode"] = ("Cc", 0x7B5BFF), // Command Code purple
        ["ollama"]      = ("Ol", 0xC4B58D), // Ollama sand
        ["abacus"]      = ("Ab", 0x814EE8), // Abacus purple
        ["stepfun"]     = ("Sf", 0x00F5E6), // StepFun cyan
        ["opencode"]    = ("Oc", 0xFAB283), // opencode TUI peach
        ["opencodego"]  = ("Go", 0x9D7CD8), // opencode Go accent violet
        ["mistral"]     = ("Ms", 0xFA500F), // Mistral orange
    };

    public static string Monogram(string providerType) =>
        Map.TryGetValue(providerType, out var v) ? v.Mono : Initials(providerType);

    public static Color Color(string providerType)
    {
        var hex = Map.TryGetValue(providerType, out var v) ? v.Hex : 0x6E7B8Au;
        return Windows.UI.Color.FromArgb(0xFF, (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
    }

    public static SolidColorBrush Brush(string providerType) => new(Color(providerType));

    // ---- Legible-on-dark brand color ---------------------------------------
    // Ten of the 49 brands are near-black ink (#111827 and friends). Drawn at
    // full size on the app's dark material they disappear — an ink-branded
    // provider becomes an invisible hole in the usage chart, or an unreadable
    // monogram in the picker. This lifts only those toward white until they
    // read; every other brand is returned exactly as authored, which is what
    // keeps Claude clay / Groq orange / Windsurf mint distinguishable.
    // Deliberately separate from Color()/Brush(), which feed the ambient tint
    // and must not be renormalized.

    private const double MinInkLuminance = 0.34;

    private static readonly Dictionary<string, Color> LegibleColorCache = new();
    private static readonly object LegibleColorCacheGate = new();

    public static Color LegibleColor(string providerType)
    {
        lock (LegibleColorCacheGate)
        {
            if (LegibleColorCache.TryGetValue(providerType, out var cached))
                return cached;

            var ink = NormalizeInkColor(Color(providerType));
            LegibleColorCache[providerType] = ink;
            return ink;
        }
    }

    public static SolidColorBrush TileBrush(string providerType) => new(LegibleColor(providerType));

    /// <summary>Lifts a color toward white until it reads clearly on a dark neutral chip.</summary>
    private static Color NormalizeInkColor(Color color)
    {
        var white = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var result = color;
        for (var step = 0; step < 12 && Luminance(result) < MinInkLuminance; step++)
            result = Lerp(result, white, 0.18);

        return result;
    }

    private static Color Lerp(Color from, Color to, double amount) => Windows.UI.Color.FromArgb(
        0xFF,
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    /// <summary>Relative luminance on linearized sRGB (WCAG definition).</summary>
    internal static double Luminance(Color color) =>
        (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));

    private static double Linearize(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    // The tint fades over the whole window at 8 bits/channel, which quantizes into
    // ~40px-wide visible bands with linear stops. Sampling a smoothstep-eased curve
    // through the same three control points makes the band spacing non-uniform
    // (Mach banding is tuned to evenly spaced edges), and the dither layer in
    // MainWindow breaks up what remains. Count must stay stable: the animated brush
    // updates stop colors in place by index.
    private static readonly double[] AmbientTintStopOffsets =
        { 0.00, 0.10, 0.19, 0.28, 0.38, 0.50, 0.62, 0.75, 0.88, 1.00 };

    public static IReadOnlyList<(double Offset, Color Color)> AmbientTintStops(Color color) =>
        AmbientTintStopOffsets
            .Select(offset => (offset, AmbientTintStopColor(color, AmbientTintAlphaAt(offset))))
            .ToArray();

    private static byte AmbientTintAlphaAt(double offset)
    {
        // Piecewise smoothstep through (0, Lead) → (0.38, Mid) → (1, 0).
        double alpha;
        if (offset <= 0.38)
        {
            var t = SmoothStep(offset / 0.38);
            alpha = AmbientTintLeadAlpha + ((AmbientTintMidAlpha - AmbientTintLeadAlpha) * t);
        }
        else
        {
            var t = SmoothStep((offset - 0.38) / 0.62);
            alpha = AmbientTintMidAlpha * (1 - t);
        }

        return (byte)Math.Clamp(Math.Round(alpha), byte.MinValue, byte.MaxValue);
    }

    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - (2 * t));
    }

    public static Brush AmbientTintBrush(string providerType)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        foreach (var stop in AmbientTintStops(Color(providerType)))
            brush.GradientStops.Add(new GradientStop { Offset = stop.Offset, Color = stop.Color });
        return brush;
    }

    /// <summary>A soft tinted background brush (~16% of the brand color) for the chip fill.</summary>
    public static SolidColorBrush SoftBrush(string providerType)
    {
        var c = Color(providerType);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0x2B, c.R, c.G, c.B));
    }

    /// <summary>
    /// Fallback monogram for a provider id with no curated entry. Two characters
    /// (first letter + the letter after a separator or camel hump) so an unmapped
    /// provider still fills the picker's 32px tile instead of showing a lone glyph.
    /// </summary>
    internal static string Initials(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "?";

        var first = char.ToUpperInvariant(s[0]);
        for (var index = 1; index < s.Length; index++)
        {
            var previous = s[index - 1];
            var current = s[index];
            if (!char.IsLetterOrDigit(current))
                continue;

            var afterSeparator = !char.IsLetterOrDigit(previous);
            var camelHump = char.IsLower(previous) && char.IsUpper(current);
            if (afterSeparator || camelHump)
                return string.Concat(first, char.ToLowerInvariant(current));
        }

        return s.Length > 1
            ? string.Concat(first, char.ToLowerInvariant(s[1]))
            : first.ToString();
    }

    private static Color AmbientTintStopColor(Color color, byte alpha)
    {
        var scaledAlpha = (byte)Math.Clamp(Math.Round(alpha * (color.A / 255.0)), byte.MinValue, byte.MaxValue);
        return Windows.UI.Color.FromArgb(scaledAlpha, color.R, color.G, color.B);
    }
}
