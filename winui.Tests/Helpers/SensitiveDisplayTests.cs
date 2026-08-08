using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Helpers;

namespace QuotaLens.Tests.Helpers;

[TestClass]
public sealed class SensitiveDisplayTests
{
    [TestMethod]
    public void MaskEmails_ReplacesEmailAddresses()
    {
        var masked = SensitiveDisplay.MaskEmails("Claude · user@example.com");

        Assert.AreEqual("Claude · ••••@••••", masked);
    }

    [TestMethod]
    public void ProviderName_WhenHidden_RemovesEmailAndKeepsProviderLabel()
    {
        var display = SensitiveDisplay.ProviderName("Claude · Max · user@example.com", hidden: true);

        Assert.AreEqual("Claude · Max", display);
    }

    [TestMethod]
    public void AccountName_WhenHidden_UsesStableAccountNumber()
    {
        var masked = SensitiveDisplay.AccountName("user@example.com", 2, hidden: true);

        Assert.AreEqual("Account 3", masked);
    }
}
