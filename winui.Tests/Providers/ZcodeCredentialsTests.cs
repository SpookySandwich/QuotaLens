using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class ZcodeCredentialsTests
{
    [TestMethod]
    public void TryReadSessionToken_RoundTripsTheRealEnvelopeThroughAFile()
    {
        var envelope = ZcodeCredentials.Encrypt("session-jwt-123", "test-secret");
        var path = Path.Combine(Path.GetTempPath(), $"zcode-creds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{ "zcodejwttoken": "{{envelope}}" }""");

        try
        {
            Assert.AreEqual("session-jwt-123", ZcodeCredentials.TryReadSessionToken(path, "test-secret"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void TryReadSessionToken_WithWrongSecret_ReturnsNull()
    {
        var envelope = ZcodeCredentials.Encrypt("session-jwt-123", "right-secret");
        var path = Path.Combine(Path.GetTempPath(), $"zcode-creds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{ "zcodejwttoken": "{{envelope}}" }""");

        try
        {
            Assert.IsNull(ZcodeCredentials.TryReadSessionToken(path, "wrong-secret"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void TryReadSessionToken_WithFutureEnvelopeVersion_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zcode-creds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "zcodejwttoken": "enc:v2:aaa.bbb.ccc" }""");

        try
        {
            Assert.IsNull(ZcodeCredentials.TryReadSessionToken(path, "test-secret"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void TryReadSessionToken_WithMissingOrMalformedStore_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"zcode-missing-{Guid.NewGuid():N}.json");
        Assert.IsNull(ZcodeCredentials.TryReadSessionToken(missing, "test-secret"));

        var noField = Path.Combine(Path.GetTempPath(), $"zcode-nofield-{Guid.NewGuid():N}.json");
        File.WriteAllText(noField, """{ "other": "value" }""");
        var plaintext = Path.Combine(Path.GetTempPath(), $"zcode-plain-{Guid.NewGuid():N}.json");
        File.WriteAllText(plaintext, """{ "zcodejwttoken": "not-an-envelope" }""");

        try
        {
            Assert.IsNull(ZcodeCredentials.TryReadSessionToken(noField, "test-secret"));
            Assert.IsNull(ZcodeCredentials.TryReadSessionToken(plaintext, "test-secret"));
        }
        finally
        {
            File.Delete(noField);
            File.Delete(plaintext);
        }
    }
}
