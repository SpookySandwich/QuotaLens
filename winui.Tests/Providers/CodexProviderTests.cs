using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Providers;
using QuotaLens.Services;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class CodexProviderTests
{
    [TestMethod]
    public void ParseUsage_WithSessionAndWeeklyWindows_MapsCodexQuotaRows()
    {
        var snapshot = CodexProvider.ParseUsage(Json("""
        {
          "plan_type": "pro",
          "rate_limit": {
            "primary_window": {
              "used_percent": 22,
              "reset_at": 1766948068,
              "limit_window_seconds": 18000
            },
            "secondary_window": {
              "used_percent": 43,
              "reset_at": 1767407914,
              "limit_window_seconds": 604800
            }
          },
          "credits": {
            "has_credits": true,
            "unlimited": false,
            "balance": "14.5"
          }
        }
        """), Credentials(), DateTimeOffset.UnixEpoch);

        Assert.AreEqual("codex", snapshot.ProviderId);
        Assert.AreEqual("Codex", snapshot.Name);
        Assert.AreEqual("5h Pool", snapshot.Primary.Label);
        Assert.AreEqual(22, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(300, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("Weekly Pool", snapshot.Secondary!.Label);
        Assert.AreEqual(43, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual(10080, snapshot.Secondary.WindowMinutes);
        Assert.AreEqual(14.5, snapshot.Balance!.Total, 0.001);
        Assert.AreEqual("credits", snapshot.Balance.Currency);
    }

    [TestMethod]
    public void ParseUsage_WithFreeWeeklyOnlyWindow_KeepsUsableWindow()
    {
        var snapshot = CodexProvider.ParseUsage(Json("""
        {
          "plan_type": "free",
          "rate_limit": {
            "primary_window": {
              "used_percent": 0,
              "reset_at": 1775468693,
              "limit_window_seconds": 604800
            },
            "secondary_window": null
          }
        }
        """), Credentials(), DateTimeOffset.UnixEpoch);

        Assert.AreEqual("Codex", snapshot.Name);
        Assert.AreEqual("Weekly Pool", snapshot.Primary.Label);
        Assert.AreEqual(10080, snapshot.Primary.WindowMinutes);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void ParseUsage_WithCreditsOnlyPayload_ReturnsCreditsWindow()
    {
        var snapshot = CodexProvider.ParseUsage(Json("""
        {
          "credits": {
            "has_credits": true,
            "unlimited": false,
            "balance": 8
          }
        }
        """), Credentials(plan: "plus"), DateTimeOffset.UnixEpoch);

        Assert.AreEqual("Codex", snapshot.Name);
        Assert.AreEqual("Credits", snapshot.Primary.Label);
        Assert.AreEqual(8, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseUsage_WithAdditionalSparkLimits_MapsExtraRateWindows()
    {
        var snapshot = CodexProvider.ParseUsage(Json("""
        {
          "plan_type": "pro",
          "rate_limit": {
            "primary_window": {
              "used_percent": 10,
              "reset_at": 1766948068,
              "limit_window_seconds": 18000
            },
            "secondary_window": {
              "used_percent": 20,
              "reset_at": 1767407914,
              "limit_window_seconds": 604800
            }
          },
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "metered_feature": "codex_spark",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 33,
                  "reset_at": 1766948068,
                  "limit_window_seconds": 18000
                },
                "secondary_window": {
                  "used_percent": 44,
                  "reset_at": 1767407914,
                  "limit_window_seconds": 604800
                }
              }
            }
          ]
        }
        """), Credentials(), DateTimeOffset.UnixEpoch);

        Assert.AreEqual(2, snapshot.AdditionalWindows.Count);
        Assert.AreEqual("Codex Spark 5-hour", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(33, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.AreEqual(300, snapshot.AdditionalWindows[0].WindowMinutes);
        Assert.AreEqual("Codex Spark Weekly", snapshot.AdditionalWindows[1].Label);
        Assert.AreEqual(44, snapshot.AdditionalWindows[1].UsedPercent, 0.001);
        Assert.AreEqual(10080, snapshot.AdditionalWindows[1].WindowMinutes);
    }

    [TestMethod]
    public void ParseUsage_WithGenericAdditionalLimit_MapsNamedExtraWindow()
    {
        var snapshot = CodexProvider.ParseUsage(Json("""
        {
          "plan_type": "pro",
          "rate_limit": {
            "primary_window": {
              "used_percent": 10,
              "reset_at": 1766948068,
              "limit_window_seconds": 18000
            }
          },
          "additional_rate_limits": [
            "ignored malformed entry",
            {
              "limit_name": "Code Review",
              "metered_feature": "codex_code_review",
              "rate_limit": {
                "secondary_window": {
                  "used_percent": 55,
                  "reset_at": 1767407914,
                  "limit_window_seconds": 604800
                }
              }
            }
          ]
        }
        """), Credentials(), DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1, snapshot.AdditionalWindows.Count);
        Assert.AreEqual("Code Review", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(55, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.AreEqual(10080, snapshot.AdditionalWindows[0].WindowMinutes);
    }

    [TestMethod]
    public void ProviderRegistry_CreateCodex_ReturnsCodexProvider()
    {
        Assert.IsInstanceOfType(ProviderRegistry.Create("codex"), typeof(CodexProvider));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CodexProvider.CodexCredentials Credentials(string? email = "user@example.com", string? plan = null) =>
        new("access-token", Jwt(email, plan), "account-123");

    private static string Jwt(string? email, string? plan)
    {
        var payload = new Dictionary<string, object?>();
        if (email is not null)
            payload["email"] = email;
        if (plan is not null)
            payload["chatgpt_plan_type"] = plan;

        return "header." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload)) + ".sig";
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
