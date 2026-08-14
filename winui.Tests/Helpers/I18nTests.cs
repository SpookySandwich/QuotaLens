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

    [TestMethod]
    public void ProviderName_ReturnsLocalizedOrFallback()
    {
        var original = I18n.Current;
        try
        {
            I18n.SetLanguage("zh");
            Assert.AreEqual("阿里云", I18n.ProviderName("alibaba", "Alibaba"));
            Assert.AreEqual("DeepSeek", I18n.ProviderName("deepseek", "DeepSeek"));
            Assert.AreEqual("UnknownX", I18n.ProviderName("unknownx", "UnknownX"),
                "Unknown providers fall back to the catalog name.");

            I18n.SetLanguage("en");
            Assert.AreEqual("Alibaba", I18n.ProviderName("alibaba", "Alibaba"));
        }
        finally
        {
            I18n.SetLanguage(original == I18n.Lang.Zh ? "zh" : "en");
        }
    }

    [TestMethod]
    public void LocalizeErrorMessage_TranslatesKnownPrefixesOnly()
    {
        var original = I18n.Current;
        try
        {
            I18n.SetLanguage("zh");
            Assert.AreEqual(
                "需要登录：CLI is not signed in.",
                I18n.LocalizeErrorMessage("Login required: CLI is not signed in."));
            Assert.AreEqual(
                "未配置：credentials missing",
                I18n.LocalizeErrorMessage("Not configured: credentials missing"));
            Assert.AreEqual(
                "Something else entirely",
                I18n.LocalizeErrorMessage("Something else entirely"));

            I18n.SetLanguage("en");
            Assert.AreEqual(
                "Login required: CLI",
                I18n.LocalizeErrorMessage("Login required: CLI"),
                "English messages pass through unchanged.");
        }
        finally
        {
            I18n.SetLanguage(original == I18n.Lang.Zh ? "zh" : "en");
        }
    }

    [TestMethod]
    public void FieldLabel_ReturnsLocalizedOrEnglish()
    {
        var original = I18n.Current;
        try
        {
            I18n.SetLanguage("zh");
            Assert.AreEqual("API 密钥", I18n.FieldLabel("API Key"));
            Assert.AreEqual("AccessKey ID", I18n.FieldLabel("AccessKey ID"));

            I18n.SetLanguage("en");
            Assert.AreEqual("API Key", I18n.FieldLabel("API Key"));
            Assert.AreEqual("Some custom label", I18n.FieldLabel("Some custom label"));
        }
        finally
        {
            I18n.SetLanguage(original == I18n.Lang.Zh ? "zh" : "en");
        }
    }
}
