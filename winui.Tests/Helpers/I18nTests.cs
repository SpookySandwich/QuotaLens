using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Helpers;

namespace QuotaLens.Tests.Helpers;

[TestClass]
public class I18nTests
{
    [TestMethod]
    public void SetLanguage_MapsExplicitValues()
    {
        var original = I18n.Current;
        try
        {
            I18n.SetLanguage("en");
            Assert.AreEqual(I18n.Lang.En, I18n.Current);

            I18n.SetLanguage("zh");
            Assert.AreEqual(I18n.Lang.Zh, I18n.Current);
        }
        finally
        {
            I18n.SetLanguage(original == I18n.Lang.Zh ? "zh" : "en");
        }
    }

    [TestMethod]
    public void SetLanguage_SystemValues_FallBackToDetectedLanguage()
    {
        var original = I18n.Current;
        try
        {
            // "", null and "system" all mean "follow the Windows display language";
            // the exact detected value depends on the test machine, so only assert
            // that they resolve to one of the two supported languages.
            foreach (var value in new[] { "", null, "system", "fr" })
            {
                I18n.SetLanguage(value);
                Assert.IsTrue(
                    I18n.Current is I18n.Lang.En or I18n.Lang.Zh,
                    $"Unexpected language for value '{value}': {I18n.Current}");
            }
        }
        finally
        {
            I18n.SetLanguage(original == I18n.Lang.Zh ? "zh" : "en");
        }
    }

    [TestMethod]
    public void SetLanguage_IsCaseInsensitiveForExplicitValues()
    {
        var original = I18n.Current;
        try
        {
            I18n.SetLanguage("EN");
            Assert.AreEqual(I18n.Lang.En, I18n.Current);

            I18n.SetLanguage("ZH");
            Assert.AreEqual(I18n.Lang.Zh, I18n.Current);
        }
        finally
        {
            I18n.SetLanguage(original == I18n.Lang.Zh ? "zh" : "en");
        }
    }
}
