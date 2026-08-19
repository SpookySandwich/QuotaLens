using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Helpers;
using Windows.UI;

namespace QuotaLens.Tests.Helpers;

[TestClass]
public sealed class BrandTests
{
    [TestMethod]
    [DataRow("claude", 0xD9, 0x77, 0x57)]
    [DataRow("codex", 0x49, 0xA3, 0xB0)]
    [DataRow("copilot", 0x85, 0x34, 0xF3)]
    [DataRow("gemini", 0x42, 0x85, 0xF4)]
    [DataRow("bedrock", 0xFF, 0x99, 0x00)]
    [DataRow("vertexai", 0xAE, 0xCB, 0xFA)]
    [DataRow("deepseek", 0x4D, 0x6B, 0xFE)]
    [DataRow("kiro", 0x90, 0x46, 0xFF)]
    [DataRow("alibaba", 0xFF, 0x6A, 0x00)]
    [DataRow("alibabacloud", 0xFF, 0x6A, 0x00)]
    [DataRow("alibabatokenplan", 0xFF, 0x6A, 0x00)]
    [DataRow("antigravity", 0x74, 0x9B, 0xFF)]
    [DataRow("bayesdl", 0x32, 0x73, 0xF9)]
    [DataRow("mimo", 0xFB, 0x81, 0x47)]
    [DataRow("qoder", 0x2A, 0xDB, 0x5C)]
    [DataRow("kimi", 0x11, 0x66, 0xC7)]
    [DataRow("amp", 0xF3, 0x4E, 0x3F)]
    [DataRow("cursor", 0xC0, 0x85, 0x32)]
    [DataRow("augment", 0x1A, 0xA0, 0x49)]
    [DataRow("factory", 0xEE, 0x60, 0x18)]
    [DataRow("minimax", 0xF2, 0x3F, 0x5D)]
    [DataRow("windsurf", 0x09, 0xB6, 0xA2)]
    [DataRow("openrouter", 0x76, 0x24, 0xF4)]
    [DataRow("moonshot", 0x0A, 0x7A, 0xFF)]
    [DataRow("venice", 0x3C, 0x8F, 0xDD)]
    [DataRow("crof", 0x6E, 0x52, 0xED)]
    [DataRow("openai", 0x10, 0xA3, 0x7F)]
    [DataRow("azureopenai", 0x00, 0x78, 0xD4)]
    [DataRow("elevenlabs", 0xEB, 0xEB, 0xE6)]
    [DataRow("warp", 0x00, 0xC2, 0xFF)]
    [DataRow("codebuff", 0x9E, 0xFC, 0x62)]
    [DataRow("synthetic", 0x63, 0x66, 0xF1)]
    [DataRow("zai", 0x37, 0x62, 0xFF)]
    [DataRow("zcode", 0x37, 0x62, 0xFF)]
    [DataRow("llmproxy", 0x24, 0xB4, 0x7E)]
    [DataRow("doubao", 0x00, 0x66, 0xFF)]
    [DataRow("groq", 0xF4, 0x3E, 0x01)]
    [DataRow("deepgram", 0x13, 0xEF, 0x93)]
    [DataRow("grok", 0xDC, 0x56, 0x07)]
    [DataRow("kilo", 0xFF, 0xE6, 0x00)]
    [DataRow("jetbrains", 0xFC, 0x1D, 0x69)]
    [DataRow("kimik2", 0x12, 0x47, 0xD6)]
    [DataRow("manus", 0x00, 0x81, 0xF2)]
    [DataRow("perplexity", 0x20, 0x80, 0x8D)]
    [DataRow("t3chat", 0xA2, 0x3B, 0x67)]
    [DataRow("commandcode", 0x7B, 0x5B, 0xFF)]
    [DataRow("ollama", 0xC4, 0xB5, 0x8D)]
    [DataRow("abacus", 0x81, 0x4E, 0xE8)]
    [DataRow("stepfun", 0x00, 0xF5, 0xE6)]
    [DataRow("opencode", 0xFA, 0xB2, 0x83)]
    [DataRow("opencodego", 0x9D, 0x7C, 0xD8)]
    [DataRow("mistral", 0xFA, 0x50, 0x0F)]
    public void Color_ForProvider_ReturnsOfficialTint(string providerType, int red, int green, int blue)
    {
        // Act
        var color = Brand.Color(providerType);

        // Assert
        Assert.AreEqual((byte)red, color.R);
        Assert.AreEqual((byte)green, color.G);
        Assert.AreEqual((byte)blue, color.B);
    }

    [TestMethod]
    public void AmbientTintStops_UseSharedTintStrengthAlongEasedCurve()
    {
        var stops = Brand.AmbientTintStops(Color.FromArgb(0xFF, 0xD9, 0x77, 0x57));

        // Anti-banding curve: same three control points as before (lead at 0,
        // mid at 0.38, transparent at 1) sampled through eased intermediates.
        Assert.AreEqual(0.00, stops[0].Offset);
        Assert.AreEqual(Brand.AmbientTintLeadAlpha, stops[0].Color.A);
        var midStop = stops.Single(stop => stop.Offset == 0.38);
        Assert.AreEqual(Brand.AmbientTintMidAlpha, midStop.Color.A);
        Assert.AreEqual(1.00, stops[^1].Offset);
        Assert.AreEqual(0, stops[^1].Color.A);

        // Alpha must decrease monotonically — an oscillating ramp would band worse.
        for (var index = 1; index < stops.Count; index++)
            Assert.IsTrue(stops[index].Color.A <= stops[index - 1].Color.A, $"alpha rose at stop {index}");
    }

    [TestMethod]
    public void AmbientTintStops_ScaleAlphaForTransparentAnimationEndpoint()
    {
        var stops = Brand.AmbientTintStops(Color.FromArgb(0x00, 0xD9, 0x77, 0x57));

        foreach (var stop in stops)
            Assert.AreEqual(0, stop.Color.A);
    }

}
