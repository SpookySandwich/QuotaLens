using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

/// <summary>
/// Covers the Grok credits-config path: the REST surface the grok CLI's own
/// x.ai/billing extension calls. This is the path that keeps the card working
/// on CLI releases where the ACP stdio surface returns -32601 Method not found.
/// </summary>
[TestClass]
public sealed class GrokProviderTests
{
    [TestMethod]
    public void ParseCreditsConfig_MapsTheNewCreditsShape()
    {
        var config = GrokProvider.ParseCreditsConfig("""
        {
          "config": {
            "creditUsagePercent": 42.5,
            "currentPeriod": {
              "type": "USAGE_PERIOD_TYPE_WEEKLY",
              "start": "2026-08-11T16:47:18.906012+00:00",
              "end": "2026-08-18T16:47:18.906012+00:00"
            },
            "onDemandCap": { "val": 5000 },
            "onDemandUsed": { "val": 300 },
            "prepaidBalance": { "val": 1250 },
            "isUnifiedBillingUser": true
          },
          "subscriptionTier": "SuperGrok"
        }
        """);

        Assert.AreEqual(42.5, config.CreditUsagePercent!.Value, 0.001);
        Assert.AreEqual("USAGE_PERIOD_TYPE_WEEKLY", config.PeriodType);
        Assert.AreEqual("2026-08-11T16:47:18.906012+00:00", config.PeriodStart);
        Assert.AreEqual("2026-08-18T16:47:18.906012+00:00", config.PeriodEnd);
        Assert.AreEqual(5000, config.OnDemandCapCents);
        Assert.AreEqual(300, config.OnDemandUsedCents);
        Assert.AreEqual(1250, config.PrepaidBalanceCents);
        Assert.AreEqual("SuperGrok", config.SubscriptionTier);
        Assert.IsNull(config.MonthlyLimitCents);
        Assert.IsNull(config.UsedCents);
    }

    [TestMethod]
    public void Snapshot_CreditsConfig_MapsPercentPeriodAndPrepaidBalance()
    {
        var config = GrokProvider.ParseCreditsConfig("""
        {
          "config": {
            "creditUsagePercent": 42.5,
            "currentPeriod": {
              "type": "USAGE_PERIOD_TYPE_WEEKLY",
              "start": "2026-08-11T00:00:00Z",
              "end": "2026-08-18T00:00:00Z"
            },
            "prepaidBalance": { "val": 1250 },
            "onDemandCap": { "val": 5000 },
            "onDemandUsed": { "val": 300 }
          },
          "subscriptionTier": "SuperGrok"
        }
        """);

        var snapshot = GrokProvider.Snapshot(config, DateTimeOffset.Parse("2030-01-02T00:00:00Z"));

        Assert.AreEqual("grok", snapshot.ProviderId);
        Assert.AreEqual("Weekly included", snapshot.Primary.Label);
        Assert.AreEqual(42.5, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("42.5% of included allowance used", snapshot.Primary.ResetDescription);
        Assert.AreEqual("2026-08-18T00:00:00.0000000+00:00", snapshot.Primary.ResetsAt);
        Assert.AreEqual(7L * 24L * 60L, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("On-demand", snapshot.Secondary!.Label);
        Assert.AreEqual(6, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual("$3.00 / $50.00 cap", snapshot.Secondary.ResetDescription);
        Assert.AreEqual(12.5, snapshot.Balance!.Total, 0.001);
        Assert.AreEqual("SuperGrok", snapshot.PlanName);
        Assert.AreEqual("grok.com billing", snapshot.SourceLabel);
    }

    [TestMethod]
    public void Snapshot_CreditsConfig_WhenUsageOmitted_TreatsAsZeroUsage()
    {
        // proto3 JSON omits zero-valued fields; a fresh account has no
        // creditUsagePercent at all, which must read as 0% used, not a parse error.
        var config = GrokProvider.ParseCreditsConfig("""
        {
          "config": {
            "currentPeriod": {
              "type": "USAGE_PERIOD_TYPE_MONTHLY",
              "start": "2026-08-01T00:00:00Z",
              "end": "2026-09-01T00:00:00Z"
            },
            "onDemandCap": { "val": 0 },
            "prepaidBalance": { "val": 0 }
          }
        }
        """);

        var snapshot = GrokProvider.Snapshot(config);

        Assert.AreEqual("Monthly included", snapshot.Primary.Label);
        Assert.AreEqual(0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("$0.00 used", snapshot.Primary.ResetDescription);
        Assert.IsNull(snapshot.Secondary);
        Assert.IsNull(snapshot.Balance); // zero prepaid balance is not a balance
    }

    [TestMethod]
    public void Snapshot_CreditsConfig_SupportsTheLegacyCentsShape()
    {
        var config = GrokProvider.ParseCreditsConfig("""
        {
          "config": {
            "monthlyLimit": { "val": 3000 },
            "used": { "val": 1000 },
            "billingPeriodStart": "2026-08-01T00:00:00Z",
            "billingPeriodEnd": "2026-09-01T00:00:00Z"
          }
        }
        """);

        var snapshot = GrokProvider.Snapshot(config);

        Assert.AreEqual("Credits", snapshot.Primary.Label);
        Assert.AreEqual(1000d / 3000d * 100d, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("$10.00 / $30.00 included", snapshot.Primary.ResetDescription);
        Assert.AreEqual(20, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void LoadCredentials_PrefersSuperGrokAndSkipsExpiredSessions()
    {
        var directory = TempDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "auth.json");
            System.IO.File.WriteAllText(path, """
            {
              "https://auth.x.ai::client-expired": {
                "key": "expired-key",
                "user_id": "u-expired",
                "email": "expired@example.com",
                "expires_at": "2000-01-01T00:00:00Z",
                "auth_mode": "oidc"
              },
              "https://accounts.x.ai/sign-in": {
                "key": "legacy-key",
                "user_id": "u-legacy",
                "email": "legacy@example.com",
                "auth_mode": "session"
              },
              "https://auth.x.ai::client-valid": {
                "key": "valid-key",
                "user_id": "u-valid",
                "email": "valid@example.com",
                "expires_at": "2999-01-01T00:00:00Z",
                "auth_mode": "oidc"
              }
            }
            """);

            var credentials = GrokProvider.LoadCredentials(directory);

            Assert.IsNotNull(credentials);
            Assert.AreEqual("valid-key", credentials.Key);
            Assert.AreEqual("u-valid", credentials.UserId);
            Assert.AreEqual("valid@example.com", credentials.Email);
            Assert.AreEqual("oidc", credentials.AuthMode);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [TestMethod]
    public void LoadCredentials_WithOnlyExpiredSessions_ReturnsNull()
    {
        var directory = TempDirectory();
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "auth.json"), """
            {
              "https://auth.x.ai::client": {
                "key": "expired-key",
                "expires_at": "2000-01-01T00:00:00Z"
              }
            }
            """);

            Assert.IsNull(GrokProvider.LoadCredentials(directory));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [TestMethod]
    public void LoadCredentials_WhenAuthFileMissing_ReturnsNull()
    {
        var directory = TempDirectory();
        try
        {
            Assert.IsNull(GrokProvider.LoadCredentials(directory));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [TestMethod]
    public void BillingUrl_AppendsCreditsPathToTheDefaultProxy()
    {
        var url = GrokProvider.BillingUrl(GrokProvider.DefaultProxyBaseUrl);

        Assert.AreEqual(
            "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
            url.ToString());
    }

    [TestMethod]
    public void BillingUrl_AllowsAnHttpsProxyOverrideButRejectsInsecureHosts()
    {
        var url = GrokProvider.BillingUrl("https://proxy.example.com/v1");
        Assert.AreEqual("https://proxy.example.com/v1/billing?format=credits", url.ToString());

        var error = Assert.ThrowsExactly<ProviderException>(() =>
            GrokProvider.BillingUrl("http://cli-chat-proxy.grok.com/v1"));
        Assert.AreEqual(ProviderErrorKind.Misconfigured, error.Kind);
    }

    private static string TempDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quotalens-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Test cleanup is best-effort.
        }
    }
}
