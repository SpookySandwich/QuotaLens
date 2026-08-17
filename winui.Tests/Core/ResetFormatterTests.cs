using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Helpers;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ResetFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [ClassInitialize]
    public static void Initialize(TestContext _) => I18n.SetLanguage("en");

    [TestMethod]
    public void FormatDurationUntil_UsesCompactSignificantUnits()
    {
        Assert.AreEqual("3h 12m", ResetFormatter.FormatDurationUntil(Now.AddHours(3).AddMinutes(12).ToString("O"), Now));
        Assert.AreEqual("2d 4h", ResetFormatter.FormatDurationUntil(Now.AddDays(2).AddHours(4).AddMinutes(59).ToString("O"), Now));
        Assert.AreEqual("12m", ResetFormatter.FormatDurationUntil(Now.AddMinutes(12).ToString("O"), Now));
        Assert.AreEqual("<1m", ResetFormatter.FormatDurationUntil(Now.AddSeconds(30).ToString("O"), Now));
        Assert.AreEqual("now", ResetFormatter.FormatDurationUntil(Now.AddSeconds(-1).ToString("O"), Now));
    }

    [TestMethod]
    public void FormatReset_UsesOneCanonicalPhrase()
    {
        Assert.AreEqual(
            "resets in 3h 12m",
            ResetFormatter.FormatReset(Now.AddHours(3).AddMinutes(12).ToString("O"), Now));
        Assert.AreEqual("resets now", ResetFormatter.FormatReset(Now.ToString("O"), Now));
    }

    [TestMethod]
    public void FormatCaption_ValidResetAlwaysWinsOverProviderDetail()
    {
        var window = new RateWindow
        {
            ResetsAt = Now.AddHours(3).AddMinutes(12).ToString("O"),
            DetailText = "provider-specific reset prose",
        };

        Assert.AreEqual("resets in 3h 12m", ResetFormatter.FormatCaption(window, Now));
    }

    [TestMethod]
    public void FormatCaption_WithoutResetUsesProviderDetail()
    {
        var window = new RateWindow { DetailText = "$75 of $100 remaining" };

        Assert.AreEqual("$75 of $100 remaining", ResetFormatter.FormatCaption(window, Now));
    }
}
