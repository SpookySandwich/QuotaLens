using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class QoderProviderTests
{
    [TestMethod]
    public void BuildSnapshot_MapsCliUsageQuotaToCreditsBalance()
    {
        var snapshot = QoderProvider.BuildSnapshot(
            new QoderProvider.QoderUsageData
            {
                UserType = "personal_professional_trial",
                ExpiresAt = 1781600933791,
                UserQuota = new QoderProvider.QoderQuota
                {
                    Total = 300,
                    Used = 0,
                    Remaining = 300,
                    Percentage = 0,
                    Unit = "credits",
                },
            },
            new QoderProvider.QoderStatusData
            {
                Plan = "Pro Trial",
            });

        Assert.AreEqual("qoder", snapshot.ProviderId);
        Assert.AreEqual("Qoder · Pro Trial", snapshot.Name);
        Assert.AreEqual("qodercli usage", snapshot.SourceLabel);
        Assert.AreEqual(Confidence.Official, snapshot.Confidence);

        Assert.AreEqual("Plan Credits", snapshot.Primary.Label);
        Assert.AreEqual(0.0, snapshot.Primary.UsedPercent);
        Assert.AreEqual("0/300 credits (300 left)", snapshot.Primary.ResetDescription);
        Assert.IsNotNull(snapshot.Primary.ResetsAt);

        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual("credits", snapshot.Balance!.Currency);
        Assert.AreEqual(300.0, snapshot.Balance.Total);
        Assert.AreEqual(0.0, snapshot.Balance.Paid);
        Assert.AreEqual(300.0, snapshot.Balance.Granted);
    }

    [TestMethod]
    public void BuildSnapshot_WhenStatusMissing_DerivesFriendlyPlanFromUserType()
    {
        var snapshot = QoderProvider.BuildSnapshot(new QoderProvider.QoderUsageData
        {
            UserType = "personal_professional",
            UserQuota = new QoderProvider.QoderQuota
            {
                Total = 300,
                Used = 75,
                Remaining = 225,
                Unit = "credits",
            },
        });

        Assert.AreEqual("Qoder · Pro", snapshot.Name);
        Assert.AreEqual(25.0, snapshot.Primary.UsedPercent);
        Assert.AreEqual("75/300 credits (225 left)", snapshot.Primary.ResetDescription);
    }

    [TestMethod]
    public void BuildSnapshot_WithMultipleCreditSources_PreservesBucketBreakdown()
    {
        var snapshot = QoderProvider.BuildSnapshot(new QoderProvider.QoderUsageData
        {
            UserType = "personal_standard",
            UserQuota = new QoderProvider.QoderQuota
            {
                Total = 300,
                Used = 30,
                Remaining = 270,
                Unit = "credits",
            },
            AddOnQuota = new QoderProvider.QoderQuota
            {
                Total = 100,
                Used = 50,
                Remaining = 50,
                Unit = "credits",
            },
            OrgResourcePackage = new QoderProvider.QoderOrgResourcePackage
            {
                Cap = 600,
                Used = 540,
                Remaining = 60,
                Available = true,
                Unit = "credits",
            },
        });

        Assert.AreEqual("Total Credits", snapshot.Primary.Label);
        Assert.AreEqual(62.0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("620/1000 credits (380 left)", snapshot.Primary.ResetDescription);
        Assert.IsNull(snapshot.Secondary);
        Assert.IsNull(snapshot.Tertiary);
        Assert.HasCount(3, snapshot.AdditionalWindows);
        CollectionAssert.AreEqual(
            new[] { "Plan Credits", "Add-on Credits", "Organization Credits" },
            snapshot.AdditionalWindows.Select(window => window.Label).ToArray());
        CollectionAssert.AreEqual(
            new[] { 10.0, 50.0, 90.0 },
            snapshot.AdditionalWindows.Select(window => window.UsedPercent).ToArray());
        Assert.IsTrue(snapshot.AdditionalWindows.All(window => !window.CountsForAvailability));
        Assert.AreEqual(380.0, snapshot.Balance!.Total);
        Assert.AreEqual(620.0, snapshot.Balance.Paid);
        Assert.AreEqual(1000.0, snapshot.Balance.Granted);
    }

    [TestMethod]
    public void BuildSnapshot_WhenPlanIsUnknown_UsesProviderOnlyTitle()
    {
        var snapshot = QoderProvider.BuildSnapshot(new QoderProvider.QoderUsageData
        {
            UserQuota = new QoderProvider.QoderQuota
            {
                Total = 100,
                Remaining = 100,
                Unit = "credits",
            },
        });

        Assert.AreEqual("Qoder", snapshot.Name);
    }

    [TestMethod]
    public void BuildSnapshot_WhenQoderReportsExhaustedZeroCapacity_MarksUnavailable()
    {
        var snapshot = QoderProvider.BuildSnapshot(new QoderProvider.QoderUsageData
        {
            UserType = "personal_standard",
            IsQuotaExceeded = true,
            ExpiresAt = 253402214400000,
            UserQuota = new QoderProvider.QoderQuota
            {
                Total = 0,
                Used = 0,
                Remaining = 0,
                Percentage = 0,
                Unit = "credits",
            },
        });

        Assert.AreEqual("Qoder · Standard", snapshot.Name);
        Assert.AreEqual(100.0, snapshot.Primary.UsedPercent);
        Assert.AreEqual("0/0 credits (0 left)", snapshot.Primary.ResetDescription);
        Assert.IsNull(snapshot.Primary.ResetsAt);
        Assert.AreEqual(0.0, snapshot.Balance!.Total);
    }

    [TestMethod]
    public void ParseUsageResponse_AcceptsSnakeCaseQuotaPayload()
    {
        var usage = QoderProvider.ParseUsageResponse(Json("""
        {
          "operation": "usage",
          "success": true,
          "data": {
            "user_id": "u-1",
            "user_type": "personal_professional_trial",
            "total_usage_percentage": "12.5",
            "expires_at": "1781600933791",
            "user_quota": {
              "total": "300",
              "used": "37.5",
              "remaining": "262.5",
              "percentage": "12.5",
              "unit": "requests"
            }
          }
        }
        """));

        Assert.AreEqual("u-1", usage.UserId);
        Assert.AreEqual("personal_professional_trial", usage.UserType);
        Assert.AreEqual(12.5, usage.TotalUsagePercentage, 0.001);
        Assert.AreEqual(1781600933791, usage.ExpiresAt);
        Assert.IsNotNull(usage.UserQuota);
        Assert.AreEqual(300, usage.UserQuota!.Total, 0.001);
        Assert.AreEqual(37.5, usage.UserQuota.Used, 0.001);
        Assert.AreEqual(262.5, usage.UserQuota.Remaining, 0.001);
        Assert.AreEqual("requests", usage.UserQuota.Unit);
    }

    [TestMethod]
    public void ParseUsageResponse_AcceptsAlternateCreditsPayload()
    {
        var usage = QoderProvider.ParseUsageResponse(Json("""
        {
          "success": true,
          "result": {
            "userId": "u-2",
            "userType": "personal_professional",
            "usagePercentage": 50,
            "resetAt": 1781600933791,
            "credits": {
              "limit": 200,
              "consumed": 100,
              "available": 100,
              "used_percentage": 50,
              "currency": "credits"
            }
          }
        }
        """));

        Assert.AreEqual("u-2", usage.UserId);
        Assert.AreEqual("personal_professional", usage.UserType);
        Assert.AreEqual(50, usage.TotalUsagePercentage, 0.001);
        Assert.AreEqual(1781600933791, usage.ExpiresAt);
        Assert.AreEqual(200, usage.UserQuota!.Total, 0.001);
        Assert.AreEqual(100, usage.UserQuota.Used, 0.001);
        Assert.AreEqual(100, usage.UserQuota.Remaining, 0.001);
        Assert.AreEqual(50, usage.UserQuota.Percentage, 0.001);
        Assert.AreEqual("credits", usage.UserQuota.Unit);
    }

    [TestMethod]
    public void ParseUsageResponse_AcceptsQoderCliOneThirtyUsageWrapper()
    {
        var usage = QoderProvider.ParseUsageResponse(Json("""
        {
          "usage": {
            "userId": "u-1",
            "userType": "personal_standard",
            "totalUsagePercentage": 0,
            "isQuotaExceeded": false,
            "isPlanQuotaProrated": false,
            "expiresAt": 1781600933791,
            "userQuota": {
              "total": 300,
              "used": 0,
              "remaining": 300,
              "percentage": 0,
              "unit": "credits"
            },
            "addOnQuota": {
              "total": 100,
              "used": 25,
              "remaining": 75,
              "percentage": 25,
              "unit": "credits",
              "detailUrl": "https://example.invalid/usage"
            },
            "orgResourcePackage": {
              "used": 40,
              "cap": 200,
              "remaining": 160,
              "percentage": 20,
              "available": true,
              "unit": "credits"
            }
          }
        }
        """));

        Assert.AreEqual("u-1", usage.UserId);
        Assert.AreEqual("personal_standard", usage.UserType);
        Assert.AreEqual(0, usage.TotalUsagePercentage, 0.001);
        Assert.IsFalse(usage.IsQuotaExceeded);
        Assert.IsFalse(usage.IsPlanQuotaProrated);
        Assert.AreEqual(1781600933791, usage.ExpiresAt);
        Assert.AreEqual(300, usage.UserQuota!.Total, 0.001);
        Assert.AreEqual(100, usage.AddOnQuota!.Total, 0.001);
        Assert.AreEqual("https://example.invalid/usage", usage.AddOnQuota.DetailUrl);
        Assert.IsTrue(usage.OrgResourcePackage!.Available);
        Assert.AreEqual(200, usage.OrgResourcePackage.Cap, 0.001);
    }

    [TestMethod]
    public void ParseUsageResponse_AcceptsCurrentWebQuotaSummariesWithoutFlatteningSources()
    {
        var usage = QoderProvider.ParseUsageResponse(Json("""
        {
          "userId": "redacted",
          "quotaKey": "big_model_credits",
          "nextResetAt": "2024-09-01T00:00:00Z",
          "status": "active",
          "totalQuota": {
            "quotaSummary": {
              "usedValue": 125,
              "limitValue": 500,
              "remainingValue": 375,
              "usagePercentage": 25,
              "unit": "credit"
            }
          },
          "sharedQuota": {
            "quotaSummary": {
              "usedValue": 200,
              "limitValue": 1000,
              "usagePercentage": 20,
              "unit": "credit"
            }
          }
        }
        """));

        Assert.AreEqual("redacted", usage.UserId);
        Assert.AreEqual(1725148800000, usage.ExpiresAt);
        Assert.AreEqual("Plan + Resource Credits", usage.UserQuotaLabel);
        Assert.AreEqual(125, usage.UserQuota!.Used, 0.001);
        Assert.AreEqual(500, usage.UserQuota.Total, 0.001);
        Assert.AreEqual(375, usage.UserQuota.Remaining, 0.001);
        Assert.AreEqual("Shared Add-on Credits", usage.AddOnQuotaLabel);
        Assert.AreEqual(200, usage.AddOnQuota!.Used, 0.001);
        Assert.AreEqual(1000, usage.AddOnQuota.Total, 0.001);
        Assert.AreEqual(800, usage.AddOnQuota.Remaining, 0.001);

        var snapshot = QoderProvider.BuildSnapshot(usage);
        Assert.AreEqual("Qoder", snapshot.Name);
        Assert.AreEqual("Total Credits", snapshot.Primary.Label);
        Assert.AreEqual(325.0 / 1500.0 * 100.0, snapshot.Primary.UsedPercent, 0.001);
        CollectionAssert.AreEqual(
            new[] { "Plan + Resource Credits", "Shared Add-on Credits" },
            snapshot.AdditionalWindows.Select(window => window.Label).ToArray());
    }

    [TestMethod]
    public void ParseUsageResponse_AcceptsLegacySnakeCaseWebQuotaSummary()
    {
        var usage = QoderProvider.ParseUsageResponse(Json("""
        {
          "user_id": "redacted",
          "next_reset_at": 1725148800,
          "total_quota": {
            "quota_summary": {
              "used_value": 125,
              "limit_value": 500,
              "remaining_value": 375,
              "usage_percentage": 25,
              "unit": "credit"
            }
          }
        }
        """));

        Assert.AreEqual("redacted", usage.UserId);
        Assert.AreEqual(1725148800000, usage.ExpiresAt);
        Assert.AreEqual(125, usage.UserQuota!.Used, 0.001);
        Assert.AreEqual(500, usage.UserQuota.Total, 0.001);
        Assert.AreEqual(375, usage.UserQuota.Remaining, 0.001);
        Assert.AreEqual(25, usage.UserQuota.Percentage, 0.001);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
