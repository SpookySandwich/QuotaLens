using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class WebLoginServiceTests
{
    [TestMethod]
    public void IsSupported_IncludesWebViewProviders()
    {
        foreach (var providerType in WebLoginService.SupportedTypes)
            Assert.IsTrue(WebLoginService.IsSupported(providerType));

        Assert.IsFalse(WebLoginService.IsSupported("deepseek"));
        Assert.IsFalse(WebLoginService.IsSupported("openrouter"));
    }

    [TestMethod]
    public void ParseKimi_BuildsWeeklyAndRateLimitWindows()
    {
        var json = """
        {
          "usages": [{
            "scope": "FEATURE_CODING",
            "detail": {
              "limit": "2048",
              "used": "214",
              "remaining": "1834",
              "resetTime": "2026-01-09T15:23:13.716839300Z"
            },
            "limits": [{
              "window": { "duration": 300, "timeUnit": "TIME_UNIT_MINUTE" },
              "detail": {
                "limit": "200",
                "used": "139",
                "remaining": "61",
                "resetTime": "2026-01-06T13:33:02.717479433Z"
              }
            }]
          }]
        }
        """;

        var snapshot = WebLoginService.ParseKimi(json);

        Assert.AreEqual("kimi", snapshot.ProviderId);
        Assert.AreEqual("Kimi", snapshot.Name);
        Assert.AreEqual("Weekly", snapshot.Primary.Label);
        Assert.AreEqual(214d / 2048d * 100d, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("214/2048 requests", snapshot.Primary.DetailText);
        Assert.AreEqual(10080, snapshot.Primary.WindowMinutes);
        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("5h Rate Limit", snapshot.Secondary!.Label);
        Assert.AreEqual(139d / 200d * 100d, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual(300, snapshot.Secondary.WindowMinutes);
        Assert.AreEqual("Rate: 139/200 requests", snapshot.Secondary.DetailText);
        Assert.AreEqual("Kimi WebView", snapshot.SourceLabel);
    }

    [TestMethod]
    public void ParseKimi_WithTotalQuota_LeadsMonthlyAndUsesPeriodEndFallback()
    {
        var json = """
        {
          "usages": [{
            "scope": "FEATURE_CODING",
            "detail": {
              "limit": "100",
              "used": "16",
              "remaining": "84",
              "resetTime": "2026-01-09T15:23:13.716839300Z"
            }
          }],
          "totalQuota": { "limit": "100", "used": "68", "remaining": "32" }
        }
        """;

        var snapshot = WebLoginService.ParseKimi(json, periodEnd: "2026-09-12T16:00:00Z");

        // The Web source delegates to KimiProvider.ParseAppUsage, so the "Monthly"
        // rename has to arrive here without a second mapping.
        Assert.AreEqual("Monthly", snapshot.Primary.Label);
        Assert.AreEqual(68d, snapshot.Primary.UsedPercent, 0.001);
        Assert.IsNull(snapshot.Primary.DetailText);
        Assert.AreEqual(QuotaCadencePolicy.MonthlyMinutes, snapshot.Primary.WindowMinutes);
        Assert.IsTrue(snapshot.Primary.CountsForAvailability);
        Assert.AreEqual("2026-09-12T16:00:00Z", snapshot.Primary.ResetsAt);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual("Kimi WebView", snapshot.SourceLabel);
    }

    [TestMethod]
    public void ParsePerplexity_WithoutRecurringCredits_DoesNotRepeatThePurchasedRow()
    {
        var snapshot = WebLoginService.ParsePerplexity("""
        {
          "balance_cents": 3000,
          "renewal_date_ts": 1893456000,
          "current_period_purchased_cents": 2000,
          "total_usage_cents": 500,
          "credit_grants": [
            { "type": "purchased", "amount_cents": 2000 },
            { "type": "promotional", "amount_cents": 500, "expires_at_ts": 1893542400 }
          ]
        }
        """);

        // With no recurring pool the purchased window is promoted to Primary, so it
        // must not be emitted a second time in the Tertiary slot.
        Assert.AreEqual("Purchased credits", snapshot.Primary.Label);
        Assert.AreEqual("Bonus credits", snapshot.Secondary!.Label);
        Assert.IsNull(snapshot.Tertiary);
    }

    [TestMethod]
    public void ParseOpenCode_RenewalRow_IsInformationalNotAFullQuotaBar()
    {
        var snapshot = WebLoginService.ParseOpenCode("""
        {
          "usage": {
            "rollingUsage": { "usagePercent": 40, "resetInSec": 3600 },
            "weeklyUsage": { "usagePercent": 20, "resetInSec": 604800 },
            "renewAt": "2030-01-01T00:00:00Z"
          }
        }
        """);

        Assert.AreEqual("Renews", snapshot.Tertiary!.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Tertiary.Kind);
        Assert.AreEqual("Subscription renewal", snapshot.Tertiary.ValueText);
        Assert.IsFalse(snapshot.Tertiary.CountsForAvailability);
    }

    [TestMethod]
    public void ParseWindsurf_WithoutDailyFigures_DoesNotFabricateAFullyAvailableDailyPool()
    {
        var snapshot = WebLoginService.ParseWindsurf("""
        {
          "planName": "teams",
          "usage": {
            "flowActions": 1000,
            "usedFlowActions": 250
          }
        }
        """);

        Assert.AreEqual("Daily", snapshot.Primary.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("Flow actions", snapshot.Secondary!.Label);
    }

    [TestMethod]
    public void ParseAmp_BuildsFreeUsageWindow()
    {
        var json = """
        {
          "freeQuota": 100,
          "freeUsed": 25,
          "hourlyReplenishment": 10,
          "windowHours": 24
        }
        """;

        var snapshot = WebLoginService.ParseAmp(json);

        Assert.AreEqual("amp", snapshot.ProviderId);
        Assert.AreEqual("Amp", snapshot.Name);
        Assert.AreEqual("Amp Free", snapshot.Primary.Label);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("25.0/100.0 credits", snapshot.Primary.DetailText);
        Assert.AreEqual(24L * 60L, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("Amp WebView", snapshot.SourceLabel);
    }

    [TestMethod]
    public void ParseAmp_SubscriptionAndCredits_UsesPlanLanesAndBalance()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var snapshot = WebLoginService.ParseAmp(
            LoadWebLoginFixture("amp-subscription-and-credits.redacted.json"),
            now);

        Assert.AreEqual("Amp", snapshot.Name);
        Assert.AreEqual("Other usage", snapshot.Primary.Label);
        Assert.AreEqual(3, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(now.AddDays(29), DateTimeOffset.Parse(snapshot.Primary.ResetsAt!));
        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("Orb usage", snapshot.Secondary!.Label);
        Assert.AreEqual(0, snapshot.Secondary.UsedPercent, 0.001);
        Assert.IsNotNull(snapshot.Tertiary);
        Assert.AreEqual("Credits", snapshot.Tertiary!.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Tertiary.Kind);
        Assert.AreEqual("$42.86 remaining · individual + 2 workspaces", snapshot.Tertiary.ValueText);
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual(42.86, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseAmp_CreditsOnly_BuildsInformationalSnapshot()
    {
        var snapshot = WebLoginService.ParseAmp(LoadWebLoginFixture("amp-credits-only.redacted.json"));

        Assert.AreEqual("Amp", snapshot.Name);
        Assert.AreEqual("Credits", snapshot.Primary.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("$1,267.20 remaining · individual + 2 workspaces", snapshot.Primary.ValueText);
        Assert.AreEqual(1267.20, snapshot.Balance!.Total, 0.001);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void ParseAmp_SubscriptionWithOneMeter_PreservesPaidLane()
    {
        var snapshot = WebLoginService.ParseAmp("""
        {
          "subscription": {
            "plan": "Megawatt",
            "otherRemainingPercent": 75,
            "orbRemainingPercent": null,
            "renewalDays": 4
          }
        }
        """, DateTimeOffset.Parse("2026-08-03T00:00:00Z"));

        Assert.AreEqual("Amp", snapshot.Name);
        Assert.AreEqual("Other usage", snapshot.Primary.Label);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void AmpInitScript_ExtractsCompactPaidUsageWithoutAccountIdentity()
    {
        var script = WebLoginService.InitScriptForTesting("amp");

        StringAssert.Contains(script, "document.body.innerText");
        StringAssert.Contains(script, "otherRemainingPercent");
        StringAssert.Contains(script, "orbRemainingPercent");
        StringAssert.Contains(script, "if (other || orb)");
        StringAssert.Contains(script, "workspaceCreditTotal");
        Assert.IsFalse(script.Contains("Signed in as", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseAlibabaTokenPlan_BuildsCreditWindow()
    {
        var json = """
        {
          "Data": {
            "planName": "TOKEN PLAN",
            "totalQuota": 100000,
            "remainingQuota": 25000,
            "nextRefreshTime": "2030-01-01T00:00:00Z"
          }
        }
        """;

        var snapshot = WebLoginService.ParseAlibabaTokenPlan(json);

        Assert.AreEqual("alibabatokenplan", snapshot.ProviderId);
        Assert.AreEqual("Alibaba Token Plan", snapshot.Name);
        Assert.AreEqual("Monthly credits", snapshot.Primary.Label);
        Assert.AreEqual(75, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("25,000 / 100,000 credits left", snapshot.Primary.DetailText);
        Assert.AreEqual(30L * 24L * 60L, snapshot.Primary.WindowMinutes);
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual("credits", snapshot.Balance!.Currency);
        Assert.AreEqual(25000, snapshot.Balance.Total, 0.001);
        Assert.AreEqual(100000, snapshot.Balance.Granted, 0.001);
        Assert.AreEqual("Alibaba Token Plan WebView", snapshot.SourceLabel);
    }

    [TestMethod]
    public void ParseAlibabaTokenPlan_ExpandsStringifiedDataPayload()
    {
        var json = """
        {
          "successResponse": "{\"TotalValue\":200,\"UsedValue\":50,\"NearestExpireDate\":\"2030-02-01\"}"
        }
        """;

        var snapshot = WebLoginService.ParseAlibabaTokenPlan(json);

        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("50 / 200 credits used", snapshot.Primary.DetailText);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Primary.ResetsAt));
    }

    [TestMethod]
    public void ParseAlibabaTokenPlan_PersonalFixture_BuildsTierRollingWindows()
    {
        var snapshot = WebLoginService.ParseAlibabaTokenPlan(
            LoadWebLoginFixture("alibaba-token-plan-personal.redacted.json"));

        Assert.AreEqual("Alibaba Token Plan", snapshot.Name);
        Assert.AreEqual("5h Window", snapshot.Primary.Label);
        Assert.AreEqual(0.09973083333333333, snapshot.Primary.UsedPercent, 0.000000001);
        Assert.AreEqual(5 * 60, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("11.97 / 12,000 credits used", snapshot.Primary.DetailText);
        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual(0.03014725, snapshot.Secondary.UsedPercent, 0.000000001);
        Assert.AreEqual(7 * 24 * 60, snapshot.Secondary.WindowMinutes);
        Assert.AreEqual("12.06 / 40,000 credits used", snapshot.Secondary.DetailText);
    }

    [TestMethod]
    public void ParseAlibabaTokenPlan_PersonalWeeklyOnlyFixture_IsStillValid()
    {
        var snapshot = WebLoginService.ParseAlibabaTokenPlan(
            LoadWebLoginFixture("alibaba-token-plan-personal-weekly-only.redacted.json"));

        Assert.AreEqual("Alibaba Token Plan", snapshot.Name);
        Assert.AreEqual("Weekly", snapshot.Primary.Label);
        Assert.AreEqual(10.007527475, snapshot.Primary.UsedPercent, 0.000000001);
        Assert.AreEqual(7 * 24 * 60, snapshot.Primary.WindowMinutes);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void AlibabaTokenPlanInitScript_UsesPersonalEndpointsAndKeepsTeamFallback()
    {
        var script = WebLoginService.InitScriptForTesting("alibabatokenplan");

        StringAssert.Contains(script, "bailian-singapore-cs.alibabacloud.com");
        StringAssert.Contains(script, "bailian-cs.console.aliyun.com");
        StringAssert.Contains(script, "IntlBroadScopeAspnGateway");
        StringAssert.Contains(script, "BroadScopeAspnGateway");
        StringAssert.Contains(script, "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/usage");
        StringAssert.Contains(script, "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/subscription");
        StringAssert.Contains(script, "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/quota-config");
        StringAssert.Contains(script, "GetSubscriptionSummary");
    }

    [TestMethod]
    public void ParseMiniMax_CurrentPercentFixture_UsesRemainingPercentAndPoints()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_780_282_340);
        var snapshot = WebLoginService.ParseMiniMax(
            LoadWebLoginFixture("minimax-token-plan-normal.redacted.json"),
            now);

        Assert.AreEqual("MiniMax", snapshot.Name);
        Assert.AreEqual("General", snapshot.Primary.Label);
        Assert.AreEqual(4, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(5 * 60, snapshot.Primary.WindowMinutes);
        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("General", snapshot.Secondary!.Label);
        Assert.AreEqual(1, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual(7 * 24 * 60, snapshot.Secondary.WindowMinutes);
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual("points", snapshot.Balance!.Currency);
        Assert.AreEqual(14000, snapshot.Balance.Total, 0.001);
    }

    [TestMethod]
    public void ParseMiniMax_StatusBoostFixture_SkipsPlaceholderAndKeepsUnlimitedWeekly()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_780_347_620);
        var snapshot = WebLoginService.ParseMiniMax(
            LoadWebLoginFixture("minimax-token-plan-status-boost.redacted.json"),
            now);

        Assert.AreEqual("MiniMax", snapshot.Name);
        Assert.AreEqual("General", snapshot.Primary.Label);
        Assert.AreEqual(1, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("2/200 prompts · 5 hours", snapshot.Primary.DetailText);
        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("Unlimited", snapshot.Secondary!.DetailText);
        Assert.AreEqual(0, snapshot.Secondary.UsedPercent, 0.001);
        Assert.IsNull(snapshot.Tertiary);
    }

    [TestMethod]
    public void MiniMaxInitScript_PrefersCurrentTokenPlanEndpointAndCatalogPage()
    {
        var script = WebLoginService.InitScriptForTesting("minimax");
        var loginField = Catalog.Fields["minimax"].Single(field => field.Key == "minimax_url");

        Assert.IsTrue(
            script.IndexOf("https://api.minimax.io/v1/token_plan/remains", StringComparison.Ordinal)
            < script.IndexOf("https://platform.minimax.io/v1/api/openplatform/coding_plan/remains", StringComparison.Ordinal));
        StringAssert.Contains(script, "https://api.minimaxi.com/v1/token_plan/remains");
        Assert.AreEqual("https://platform.minimax.io/user-center/payment/token-plan", loginField.Placeholder);
    }

    [TestMethod]
    public void ParseStepFun_WeightedCreditFixture_UsesCreditPrimaryOnly()
    {
        var snapshot = WebLoginService.ParseStepFun(LoadWebLoginFixture("stepfun-credit-weighted.redacted.json"));

        Assert.AreEqual("Credits", snapshot.Primary.Label);
        Assert.AreEqual(42.5, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("57.5% available", snapshot.Primary.DetailText);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void ParseStepFun_LiveWindowFixture_WinsOverCreditFamilyId()
    {
        var snapshot = WebLoginService.ParseStepFun(
            LoadWebLoginFixture("stepfun-live-window-family-two.redacted.json"));

        Assert.AreEqual("5h Window", snapshot.Primary.Label);
        Assert.AreEqual(20, snapshot.Primary.UsedPercent, 0.001);
        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("Weekly Window", snapshot.Secondary!.Label);
        Assert.AreEqual(40, snapshot.Secondary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void StepFunInitScript_AcceptsCreditPayloadsWithoutRollingFields()
    {
        var script = WebLoginService.InitScriptForTesting("stepfun");

        StringAssert.Contains(script, "plan_credit_rate_limit");
        StringAssert.Contains(script, "hasCredit");
        StringAssert.Contains(script, "Number(usage.plan_family) === 2");
    }

    [TestMethod]
    public void ParseOpenCodeGo_RollingAndMonthlyFixture_PreservesMissingWeekly()
    {
        var snapshot = WebLoginService.ParseOpenCodeGo(
            LoadWebLoginFixture("opencode-go-rolling-only.redacted.json"));

        Assert.AreEqual("5h Window", snapshot.Primary.Label);
        Assert.AreEqual(17, snapshot.Primary.UsedPercent, 0.001);
        Assert.IsNull(snapshot.Secondary);
        Assert.IsNotNull(snapshot.Tertiary);
        Assert.AreEqual("Monthly", snapshot.Tertiary!.Label);
        Assert.AreEqual(91, snapshot.Tertiary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseOpenCodeGo_RawBillingFixture_BuildsBalanceOnlySnapshot()
    {
        var snapshot = WebLoginService.ParseOpenCodeGo(
            LoadWebLoginFixture("opencode-go-balance-only.redacted.json"));

        Assert.AreEqual("Zen balance", snapshot.Primary.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("$12.35 remaining", snapshot.Primary.ValueText);
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual(12.3456, snapshot.Balance!.Total, 0.0001);
    }

    [TestMethod]
    public void ParseOpenCodeGo_BalanceMetadataWithoutAmount_IsRejected()
    {
        var json = LoadWebLoginFixture("opencode-go-balance-metadata-only.redacted.json");

        try
        {
            WebLoginService.ParseOpenCodeGo(json);
            Assert.Fail("Expected ProviderException.");
        }
        catch (ProviderException)
        {
        }
    }

    [TestMethod]
    public void OpenCodeGoInitScript_FetchesAndNormalizesBillingServerBalance()
    {
        var script = WebLoginService.InitScriptForTesting("opencodego");

        StringAssert.Contains(script, "c83b78a614689c38ebee981f9b39a8b377716db85c1fd7dbab604adc02d3313d");
        StringAssert.Contains(script, "JSON.stringify([workspaceId])");
        StringAssert.Contains(script, "raw / 100000000");
        StringAssert.Contains(script, "payload.rollingUsage || balance != null");
    }

    [TestMethod]
    public void ParseMimo_BalanceOnlyFixture_UsesProviderTitleAndBalanceComponents()
    {
        var snapshot = WebLoginService.ParseMimo(LoadWebLoginFixture("mimo-balance-only.redacted.json"));

        Assert.AreEqual("MiMo", snapshot.Name);
        Assert.AreEqual(EntitlementStatus.Unknown, snapshot.EntitlementStatus);
        Assert.AreEqual("Balance", snapshot.Primary.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("USD 25.51 remaining", snapshot.Primary.ValueText);
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual("USD", snapshot.Balance!.Currency);
        Assert.AreEqual(25.51, snapshot.Balance.Total, 0.001);
        Assert.AreEqual(20, snapshot.Balance.Paid, 0.001);
        Assert.AreEqual(5.51, snapshot.Balance.Granted, 0.001);
        Assert.AreEqual("card.cashBalance", snapshot.Balance.PaidLabelKey);
        Assert.AreEqual("card.giftBalance", snapshot.Balance.GrantedLabelKey);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void MimoInitScript_CapturesBalanceWithoutRequiringTokenPlanUsage()
    {
        var script = WebLoginService.InitScriptForTesting("mimo");

        StringAssert.Contains(script, "/api/v1/balance");
        StringAssert.Contains(script, "Number(response.code) === 0");
        StringAssert.Contains(script, "hasUsage || hasBalance");
        StringAssert.Contains(script, "balance: hasBalance ? balance : null");
        StringAssert.Contains(script, "AbortController");
    }

    [TestMethod]
    public void ParseMimo_ErrorEnvelopeFixture_DoesNotReuseStaleData()
    {
        Assert.ThrowsExactly<ProviderException>(() =>
            WebLoginService.ParseMimo(LoadWebLoginFixture("mimo-error-envelope.redacted.json")));
    }

    [TestMethod]
    public void ParseMistral_VibeAndCreditsFixture_AddsMonthlyWindowAndBalance()
    {
        var snapshot = WebLoginService.ParseMistral(
            LoadWebLoginFixture("mistral-vibe-and-credits.redacted.json"));

        Assert.AreEqual("Mistral", snapshot.Name);
        Assert.AreEqual("Monthly spend", snapshot.Primary.Label);
        Assert.AreEqual("1500 tokens · 1 models", snapshot.Secondary!.ValueText);
        Assert.HasCount(1, snapshot.AdditionalWindows);
        Assert.AreEqual("Monthly Plan", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(37.5, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.AreEqual(
            DateTimeOffset.Parse("2030-02-01T00:00:00Z"),
            DateTimeOffset.Parse(snapshot.AdditionalWindows[0].ResetsAt!));
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual(52.5, snapshot.Balance!.Total, 0.001);
        Assert.AreEqual(50, snapshot.Balance.Paid, 0.001);
        Assert.AreEqual(10, snapshot.Balance.Granted, 0.001);
    }

    [TestMethod]
    public void ParseMistral_NegativeAvailableCreditFixture_ClampsToZero()
    {
        var snapshot = WebLoginService.ParseMistral(
            LoadWebLoginFixture("mistral-negative-balance.redacted.json"));

        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual(0, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void MistralInitScript_TreatsCreditsAndVibeAsOptionalEnrichment()
    {
        var script = WebLoginService.InitScriptForTesting("mistral");

        StringAssert.Contains(script, "/api/billing/credits");
        StringAssert.Contains(script, "billing.vibeUsage");
        StringAssert.Contains(script, "X-CSRFToken");
        StringAssert.Contains(script, "AbortController");
        StringAssert.Contains(script, ".catch(function() { return null; })");
    }

    [TestMethod]
    public void ParseBayesdl_WithTokenPlanCombo_NamesSnapshotForPlanRules()
    {
        var json = """
        {
          "code": "0",
          "data": {
            "rows": [{
              "tokensTotal": 10000000,
              "tokensUse": 2500000,
              "comboName": "Token Standard 标准包",
              "comboEndTime": "2030-01-01T00:00:00Z",
              "statusDict": { "name": "Active" },
              "isCodingPlan": 0
            }],
            "cost": {
              "balance": 18.50,
              "amountOwed": 3.25
            }
          }
        }
        """;

        var snapshot = WebLoginService.ParseBayesdl(json);

        Assert.AreEqual("BayesDL", snapshot.Name);
        Assert.AreEqual("Token Standard 标准包", snapshot.Primary.Label);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Token Standard 标准包 (2500000/10000000 tokens) | ¥15.25 bal / ¥3.25 owed", snapshot.Primary.DetailText);
        Assert.AreEqual("Active resets 2030-01-01T00:00:00Z", snapshot.Secondary!.DetailText);
        Assert.AreEqual(15.25, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseBayesdl_WithCodingPlanCombo_UsesCodingUnit()
    {
        var json = """
        {
          "code": "0",
          "data": {
            "rows": [{
              "tokensTotal": 18000,
              "tokensUse": 900,
              "comboName": "Coding Pro 进阶包",
              "comboAttributeDict": { "name": "Coding Plan" },
              "isCodingPlan": 1
            }],
            "cost": {}
          }
        }
        """;

        var snapshot = WebLoginService.ParseBayesdl(json);

        Assert.AreEqual("BayesDL", snapshot.Name);
        Assert.AreEqual(5, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Coding Pro 进阶包 (900/18000 uses) | ¥0.00 bal / ¥0.00 owed", snapshot.Primary.DetailText);
    }

    [TestMethod]
    public void ParseBayesdl_ExpiredFirstRow_SelectsLaterActivePlan()
    {
        var snapshot = WebLoginService.ParseBayesdl("""
        {
          "code": "0",
          "data": {
            "rows": [
              {
                "tokensTotal": 100,
                "tokensUse": 100,
                "comboName": "Expired",
                "comboEndTime": "2026-07-01T00:00:00Z",
                "statusDict": { "name": "Expired" }
              },
              {
                "tokensTotal": 1000,
                "tokensUse": 250,
                "comboName": "Active Pro",
                "comboStartTime": "2026-08-01T00:00:00Z",
                "comboEndTime": "2026-09-01T00:00:00Z",
                "statusDict": { "name": "Active" }
              }
            ],
            "cost": { "balance": 10, "amountOwed": 0 }
          }
        }
        """, DateTimeOffset.Parse("2026-08-03T00:00:00Z"));

        Assert.AreEqual("BayesDL", snapshot.Name);
        Assert.AreEqual("Active Pro", snapshot.PlanName);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(EntitlementStatus.Active, snapshot.EntitlementStatus);
    }

    [TestMethod]
    public void ParseBayesdl_FutureScheduledPlan_IsPendingRatherThanExpired()
    {
        var snapshot = WebLoginService.ParseBayesdl("""
        {
          "code": "000000",
          "data": {
            "rows": [
              {
                "comboName": "Scheduled Pro",
                "tokensTotal": 1000,
                "tokensUse": 0,
                "comboStartTime": "2030-02-01T00:00:00Z",
                "comboEndTime": "2030-03-01T00:00:00Z",
                "statusDict": { "name": "Scheduled" }
              }
            ],
            "cost": { "balance": 10, "amountOwed": 0 }
          }
        }
        """, DateTimeOffset.Parse("2030-01-01T00:00:00Z"));

        Assert.AreEqual("BayesDL", snapshot.Name);
        Assert.IsNull(snapshot.PlanName);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual(EntitlementStatus.Unknown, snapshot.EntitlementStatus);
    }

    [TestMethod]
    public void BayesdlCaptureScripts_PreserveEveryReturnedPlanRow()
    {
        var script = WebLoginService.InitScriptForTesting("bayesdl");

        StringAssert.Contains(script, "combo.data.rows.map");
        StringAssert.Contains(script, "rows: rows");
        Assert.IsFalse(script.Contains("rows: [{", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseAlibabaCodingPlan_BuildsFiveHourWeeklyMonthlyWindows()
    {
        var json = """
        {
          "data": {
            "codingPlanInstanceInfos": [
              { "planName": "Expired Starter", "status": "EXPIRED" },
              { "planName": "Active Pro", "status": "VALID" }
            ],
            "codingPlanQuotaInfo": {
              "per5HourUsedQuota": 52,
              "per5HourTotalQuota": 1000,
              "per5HourQuotaNextRefreshTime": 1893456000000,
              "perWeekUsedQuota": 800,
              "perWeekTotalQuota": 5000,
              "perWeekQuotaNextRefreshTime": 1894060800000,
              "perBillMonthUsedQuota": 1200,
              "perBillMonthTotalQuota": 20000,
              "perBillMonthQuotaNextRefreshTime": 1896048000000
            }
          },
          "status_code": 0
        }
        """;

        var snapshot = WebLoginService.ParseAlibabaCodingPlan(json);

        Assert.AreEqual("alibaba", snapshot.ProviderId);
        Assert.AreEqual("Alibaba", snapshot.Name);
        Assert.AreEqual("5h Pool", snapshot.Primary.Label);
        Assert.AreEqual(5.2, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("52 / 1000 used", snapshot.Primary.DetailText);
        Assert.AreEqual(300, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual(16, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual("Monthly", snapshot.Tertiary!.Label);
        Assert.AreEqual(6, snapshot.Tertiary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseAlibabaCodingPlan_WithPascalCaseGatewayPayload_BuildsWindows()
    {
        var json = """
        {
          "Code": "200",
          "Success": true,
          "Data": {
            "CodingPlanInstanceInfos": [
              {
                "PlanName": "Coding Plan Pro",
                "Status": "VALID",
                "CodingPlanQuotaInfo": {
                  "Per5HourUsedQuota": "10",
                  "Per5HourTotalQuota": "100",
                  "Per5HourQuotaNextRefreshTime": "2030-01-01T00:00:00Z",
                  "PerWeekUsedQuota": "250",
                  "PerWeekTotalQuota": "1000",
                  "PerWeekQuotaNextRefreshTime": "2030-01-07T00:00:00Z"
                }
              }
            ]
          }
        }
        """;

        var snapshot = WebLoginService.ParseAlibabaCodingPlan(json);

        Assert.AreEqual("alibaba", snapshot.ProviderId);
        Assert.AreEqual("Alibaba", snapshot.Name);
        Assert.AreEqual("5h Pool", snapshot.Primary.Label);
        Assert.AreEqual(10, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("10 / 100 used", snapshot.Primary.DetailText);
        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual(25, snapshot.Secondary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseAlibabaCodingPlan_WithBalancePayload_KeepsPlanRowsAndBalance()
    {
        var json = """
        {
          "Code": "200",
          "Success": true,
          "Data": {
            "AvailableAmount": "88.50",
            "AvailableCashAmount": "80.25",
            "Currency": "CNY",
            "CodingPlanInstanceInfos": [
              {
                "PlanName": "Coding Plan Pro",
                "Status": "VALID",
                "CodingPlanQuotaInfo": {
                  "Per5HourUsedQuota": 10,
                  "Per5HourTotalQuota": 100,
                  "PerWeekUsedQuota": 250,
                  "PerWeekTotalQuota": 1000
                }
              }
            ]
          }
        }
        """;

        var snapshot = WebLoginService.ParseAlibabaCodingPlan(json);

        Assert.AreEqual("Alibaba", snapshot.Name);
        Assert.AreEqual("5h Pool", snapshot.Primary.Label);
        Assert.AreEqual(10, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual("CNY", snapshot.Balance!.Currency);
        Assert.AreEqual(88.50, snapshot.Balance.Total, 0.001);
        Assert.AreEqual(80.25, snapshot.Balance.Paid, 0.001);
    }

    [TestMethod]
    public void ParseAlibabaCodingPlan_NormalizesExpiredFiveHourResetForward()
    {
        var staleReset = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        var json = $$"""
        {
          "data": {
            "codingPlanInstanceInfos": [
              {
                "planName": "Coding Plan Lite",
                "status": "VALID",
                "codingPlanQuotaInfo": {
                  "per5HourUsedQuota": 1,
                  "per5HourTotalQuota": 10,
                  "per5HourQuotaNextRefreshTime": {{staleReset}}
                }
              }
            ]
          },
          "status_code": 0
        }
        """;

        var before = DateTimeOffset.UtcNow;
        var snapshot = WebLoginService.ParseAlibabaCodingPlan(json);

        Assert.IsTrue(DateTimeOffset.TryParse(snapshot.Primary.ResetsAt, out var reset));
        Assert.IsTrue(reset > before.AddHours(4.8), $"Reset should be shifted close to five hours out, got {reset:O}.");
        Assert.IsTrue(reset < before.AddHours(5.1), $"Reset should not point at a weekly/monthly horizon, got {reset:O}.");
    }

    [TestMethod]
    public void ParseAlibabaCodingPlan_FallsBackToVisibleActivePlan()
    {
        var json = """
        {
          "data": {
            "codingPlanInstanceInfos": [
              {
                "planName": "Coding Plan Lite",
                "status": "VALID"
              }
            ]
          },
          "status_code": 0
        }
        """;

        var snapshot = WebLoginService.ParseAlibabaCodingPlan(json);

        Assert.AreEqual("Alibaba", snapshot.Name);
        Assert.AreEqual("Coding plan", snapshot.Primary.Label);
        Assert.AreEqual("Plan active", snapshot.Primary.DetailText);
    }

    [TestMethod]
    public void AlibabaCodingPlanInitScript_UsesConsoleRpcSecTokenFlow()
    {
        var script = WebLoginService.InitScriptForTesting("alibaba");

        StringAssert.Contains(script, "IntlBroadScopeAspnGateway");
        StringAssert.Contains(script, "BroadScopeAspnGateway");
        StringAssert.Contains(script, "sec_token");
        StringAssert.Contains(script, "queryCodingPlanInstanceInfoRequest");
        StringAssert.Contains(script, "application/x-www-form-urlencoded");
        StringAssert.Contains(script, "__qlCaptureJson");
        StringAssert.Contains(script, "codingPayloadLooksUseful");
        StringAssert.Contains(script, "codingplanquotainfo");
        StringAssert.Contains(script, "__quotalensError");
        StringAssert.Contains(script, "__qlAllowAlibabaNoQuotaError");
        StringAssert.Contains(script, "isVisibleLogin()");
        StringAssert.Contains(script, "sign in or open Alibaba Coding Plan");
        StringAssert.Contains(script, "Alibaba returned no recognized Coding Plan quota yet; retrying");
        StringAssert.Contains(script, "No Alibaba Coding Plan quota was found");
    }

    [TestMethod]
    public void WebLoginModeScript_ExposesVisibleAndHiddenCaptureModeToPageScripts()
    {
        var visibleScript = WebLoginService.ModeScriptForTesting(hidden: false);
        var hiddenScript = WebLoginService.ModeScriptForTesting(hidden: true);

        StringAssert.Contains(visibleScript, "window.__qlWebLoginMode = { hidden: false, visible: true }");
        StringAssert.Contains(visibleScript, "window.__qlVisibleLogin = true");
        StringAssert.Contains(visibleScript, "window.__qlSuppressBanner = true");
        StringAssert.Contains(visibleScript, "__ql_banner_suppression");
        StringAssert.Contains(visibleScript, "display:none!important");
        StringAssert.Contains(visibleScript, "window.top === window");
        StringAssert.Contains(hiddenScript, "window.__qlWebLoginMode = { hidden: true, visible: false }");
        StringAssert.Contains(hiddenScript, "window.__qlHiddenCapture = true");
        StringAssert.Contains(hiddenScript, "window.__qlSuppressBanner = false");
    }

    [TestMethod]
    public void WebMessageBridgeScript_RelaysCapturePayloadsFromChildFrames()
    {
        var script = WebLoginService.BridgeScriptForTesting();

        StringAssert.Contains(script, "function relayToTop");
        StringAssert.Contains(script, "window.top.postMessage(message, '*')");
        StringAssert.Contains(script, "window.addEventListener('message'");
        StringAssert.Contains(script, "quotalens-capture-json");
        StringAssert.Contains(script, "quotalens-hash");
    }

    [TestMethod]
    public void CaptureScriptInjection_SkipsVisibleAlibabaLoginButKeepsHiddenCapture()
    {
        Assert.IsFalse(WebLoginService.ShouldInjectCaptureScriptForTesting("alibaba", hidden: false));
        Assert.IsTrue(WebLoginService.ShouldInjectCaptureScriptForTesting("alibaba", hidden: true));
        Assert.IsTrue(WebLoginService.ShouldInjectCaptureScriptForTesting("kimi", hidden: false));
    }

    [TestMethod]
    public void AlibabaVisibleLogin_AutoClosesOnlyAfterPostLoginLanding()
    {
        Assert.IsFalse(WebLoginService.IsAlibabaPostLoginLandingForTesting(
            "https://account.aliyun.com/login/login.htm?oauth_callback=https%3A%2F%2Fwww.aliyun.com%2F"));
        Assert.IsFalse(WebLoginService.IsAlibabaPostLoginLandingForTesting(
            "https://passport.aliyun.com/mini_login.htm"));
        Assert.IsTrue(WebLoginService.IsAlibabaPostLoginLandingForTesting(
            "https://www.aliyun.com/"));
        Assert.IsTrue(WebLoginService.IsAlibabaPostLoginLandingForTesting(
            "https://bailian.console.aliyun.com/cn-beijing/?tab=model#/efm/coding_plan"));
        Assert.IsFalse(WebLoginService.IsAlibabaPostLoginLandingForTesting(
            "https://example.test/"));
    }

    [TestMethod]
    public void PollBudget_ExtendsAlibabaHiddenCaptureForConsoleSso()
    {
        Assert.IsNull(WebLoginService.PollBudgetForTesting("alibaba", hidden: false));
        Assert.AreEqual(45, WebLoginService.PollBudgetForTesting("alibaba", hidden: true));
        Assert.AreEqual(9, WebLoginService.PollBudgetForTesting("kimi", hidden: true));
    }

    [TestMethod]
    public void NativeCapturedResponseForTesting_MatchesAlibabaCodingPlanGatewayOnly()
    {
        Assert.IsTrue(WebLoginService.NativeCapturedResponseForTesting(
            "alibaba",
            "https://bailian-singapore-cs.alibabacloud.com/data/api.json?action=IntlBroadScopeAspnGateway&api=zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2"));

        Assert.IsFalse(WebLoginService.NativeCapturedResponseForTesting(
            "alibaba",
            "https://bailian-singapore-cs.alibabacloud.com/data/api.json?action=OtherApi"));
        Assert.IsFalse(WebLoginService.NativeCapturedResponseForTesting(
            "kimi",
            "https://bailian-singapore-cs.alibabacloud.com/data/api.json?action=IntlBroadScopeAspnGateway&api=zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2"));
    }

    [TestMethod]
    public void NativeCapturedResponseForTesting_MatchesProviderUsageApis()
    {
        // The dashboard page's own usage calls are sniffed so the login window closes
        // even when the injected script cannot read an HttpOnly auth cookie.
        Assert.IsTrue(WebLoginService.NativeCapturedResponseForTesting(
            "kimi",
            "https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages"));
        Assert.IsTrue(WebLoginService.NativeCapturedResponseForTesting(
            "manus",
            "https://api.manus.im/user.v1.UserService/GetAvailableCredits"));
        Assert.IsTrue(WebLoginService.NativeCapturedResponseForTesting(
            "windsurf",
            "https://windsurf.com/_backend/exa.seat_management_pb.SeatManagementService/GetPlanStatus"));

        Assert.IsFalse(WebLoginService.NativeCapturedResponseForTesting(
            "kimi",
            "https://www.kimi.com/apiv2/kimi.gateway.chat.v1.ChatService/GetHistory"));
        Assert.IsFalse(WebLoginService.NativeCapturedResponseForTesting(
            "manus",
            "https://api.manus.im/user.v1.UserService/GetProfile"));
        Assert.IsFalse(WebLoginService.NativeCapturedResponseForTesting("kimi", null));
        Assert.IsFalse(WebLoginService.NativeCapturedResponseForTesting(
            "cursor",
            "https://cursor.com/api/usage-summary"));
    }

    [TestMethod]
    public void NativeCookieCapture_IsConfiguredForHttpOnlyCookieProviders()
    {
        Assert.IsTrue(WebLoginService.HasNativeCookieCaptureForTesting("kimi"));
        Assert.IsTrue(WebLoginService.HasNativeCookieCaptureForTesting("manus"));
        Assert.IsFalse(WebLoginService.HasNativeCookieCaptureForTesting("cursor"));
        Assert.IsFalse(WebLoginService.HasNativeCookieCaptureForTesting("alibaba"));
    }

    [TestMethod]
    public void NativeCookieCapture_KimiRequestCarriesBearerAndConnectHeaders()
    {
        using var request = WebLoginService.NativeCookieCaptureRequestForTesting("kimi", "token-123");

        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(
            "https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages",
            request.RequestUri!.ToString());
        Assert.AreEqual("Bearer token-123", request.Headers.GetValues("Authorization").Single());
        Assert.AreEqual("kimi-auth=token-123", request.Headers.GetValues("Cookie").Single());
        Assert.AreEqual("1", request.Headers.GetValues("connect-protocol-version").Single());
        Assert.AreEqual("web", request.Headers.GetValues("x-msh-platform").Single());
        StringAssert.Contains(request.Content!.ReadAsStringAsync().Result, "FEATURE_CODING");

        using var subscription = WebLoginService.KimiNativeSubscriptionRequestForTesting("token-123");
        Assert.AreEqual(
            "https://www.kimi.com/apiv2/kimi.gateway.membership.v2.MembershipService/GetSubscription",
            subscription.RequestUri!.ToString());
        Assert.AreEqual("Bearer token-123", subscription.Headers.GetValues("Authorization").Single());
    }

    [TestMethod]
    public void TryDecodeCaptureJsonFromUrl_DecodesUrlSafeCapturePayload()
    {
        var payload = """{"ok":true,"provider":"alibaba"}""";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var decoded = WebLoginService.TryDecodeCaptureJsonFromUrlForTesting(
            $"https://modelstudio.console.alibabacloud.com/ap-southeast-1/?tab=coding-plan#/efm/coding_plan#__ql__{encoded}",
            out var json);

        Assert.IsTrue(decoded);
        Assert.AreEqual(payload, json);
    }

    [TestMethod]
    public void TryDecodeCaptureJsonFromUrl_IgnoresDiagnosticPayloads()
    {
        var decoded = WebLoginService.TryDecodeCaptureJsonFromUrlForTesting(
            "https://example.test/#__ql__HTTP_403",
            out var json);

        Assert.IsFalse(decoded);
        Assert.AreEqual("", json);
    }

    [TestMethod]
    public async Task FetchAsync_WithCachedSnapshot_StillRunsHiddenCapture()
    {
        // Arrange
        var staleTime = DateTimeOffset.UtcNow.AddHours(-2);
        var freshTime = DateTimeOffset.UtcNow;
        var calls = new List<(string InstanceId, string ProviderType, string LoginUrl, string UserDataFolder, bool Hidden)>();
        WebLoginService? service = null;

        service = new WebLoginService((request, hidden) =>
        {
            calls.Add((request.InstanceId, request.ProviderType, request.LoginUrl, request.UserDataFolder, hidden));
            service!.StoreResult(request.InstanceId, Snapshot(request.ProviderType, freshTime, "fresh"));
            return Task.FromResult(true);
        });
        service.StoreResult("mimo-main", Snapshot("mimo", staleTime, "stale"));

        // Act
        var snapshot = await service.FetchAsync("mimo-main", "mimo", new EmptyConfig());

        // Assert
        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("mimo-main", calls[0].InstanceId);
        Assert.AreEqual("mimo", calls[0].ProviderType);
        Assert.AreEqual("https://platform.xiaomimimo.com/console/plan-manage", calls[0].LoginUrl);
        StringAssert.Contains(calls[0].UserDataFolder, Path.Combine("QuotaLens", "WebView2Profiles", "mimo-main"));
        Assert.IsTrue(calls[0].Hidden);
        Assert.AreEqual(freshTime, snapshot.UpdatedAt);
        Assert.AreEqual("fresh", snapshot.Primary.Label);
    }

    [TestMethod]
    public async Task FetchAsync_WithHiddenCaptureDisabled_ReturnsFreshCacheWithoutOpeningHiddenCapture()
    {
        var freshTime = DateTimeOffset.UtcNow;
        var calls = new List<WebLoginService.WebLoginCaptureRequest>();
        var service = new WebLoginService((request, _) =>
        {
            calls.Add(request);
            return Task.FromResult(false);
        });
        service.StoreResult("kimi-main", Snapshot("kimi", freshTime, "fresh"));

        var snapshot = await service.FetchAsync(
            "kimi-main",
            "kimi",
            new EmptyConfig(),
            allowHiddenCapture: false);

        Assert.AreEqual(0, calls.Count);
        Assert.AreEqual("fresh", snapshot.Primary.Label);
        Assert.AreEqual(freshTime, snapshot.UpdatedAt);
    }

    [TestMethod]
    public async Task FetchAsync_WithHiddenCaptureDisabledAndStaleCache_ThrowsWithoutOpeningHiddenCapture()
    {
        var staleTime = DateTimeOffset.UtcNow.AddHours(-2);
        var calls = new List<WebLoginService.WebLoginCaptureRequest>();
        var service = new WebLoginService((request, _) =>
        {
            calls.Add(request);
            return Task.FromResult(false);
        });
        service.StoreResult("kimi-main", Snapshot("kimi", staleTime, "stale"));

        var exception = await ThrowsProviderExceptionAsync(
            () => service.FetchAsync(
                "kimi-main",
                "kimi",
                new EmptyConfig(),
                allowHiddenCapture: false));

        StringAssert.Contains(exception.Message, "Login required");
        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public async Task FetchAsync_WithoutCachedSnapshot_DoesNotOpenHiddenCapture()
    {
        var calls = new List<WebLoginService.WebLoginCaptureRequest>();
        var service = new WebLoginService((request, hidden) =>
        {
            calls.Add(request);
            return Task.FromResult(false);
        });
        var config = new TestConfig(new Dictionary<string, string>
        {
            ["kimi-main.kimi_url"] = "https://example.test/custom-kimi",
        });

        _ = await ThrowsProviderExceptionAsync(
            () => service.FetchAsync("kimi-main", "kimi", config));

        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public async Task OpenLoginAsync_WithScopedLoginUrl_UsesConfiguredInstanceUrl()
    {
        var calls = new List<(WebLoginService.WebLoginCaptureRequest Request, bool Hidden)>();
        var service = new WebLoginService((request, hidden) =>
        {
            calls.Add((request, hidden));
            return Task.FromResult(false);
        });
        var config = new TestConfig(new Dictionary<string, string>
        {
            ["kimi-main.kimi_url"] = "https://example.test/custom-kimi",
        });

        var captured = await service.OpenLoginAsync("kimi-main", "kimi", config);

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("kimi-main", calls[0].Request.InstanceId);
        Assert.AreEqual("kimi", calls[0].Request.ProviderType);
        Assert.AreEqual("https://example.test/custom-kimi", calls[0].Request.LoginUrl);
        Assert.IsFalse(calls[0].Hidden);
        Assert.IsFalse(captured);
    }

    [TestMethod]
    public async Task OpenLoginAsync_ForAlibaba_OpensStableAliyunLoginAndPreservesCaptureUrl()
    {
        var calls = new List<(WebLoginService.WebLoginCaptureRequest Request, bool Hidden)>();
        var service = new WebLoginService((request, hidden) =>
        {
            calls.Add((request, hidden));
            return Task.FromResult(false);
        });
        const string dashboard =
            "https://modelstudio.console.alibabacloud.com/ap-southeast-1/?tab=coding-plan#/efm/coding_plan";
        var config = new TestConfig(new Dictionary<string, string>
        {
            ["alibaba-main.alibaba_url"] = dashboard,
        });

        var captured = await service.OpenLoginAsync("alibaba-main", "alibaba", config);

        Assert.AreEqual(1, calls.Count);
        Assert.IsFalse(calls[0].Hidden);
        Assert.IsFalse(captured);
        Assert.AreEqual(
            "https://account.aliyun.com/login/login.htm?oauth_callback=https%3A%2F%2Fwww.aliyun.com%2F",
            calls[0].Request.LoginUrl);
        Assert.AreEqual(dashboard, calls[0].Request.CaptureUrl);
        StringAssert.Contains(calls[0].Request.LoginUrl, "oauth_callback=https%3A%2F%2Fwww.aliyun.com%2F");
        Assert.IsFalse(calls[0].Request.LoginUrl.Contains(Uri.EscapeDataString(dashboard), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task OpenLoginAsync_ForBayesdlLegacyUrl_UsesCurrentLoginUrl()
    {
        var calls = new List<(WebLoginService.WebLoginCaptureRequest Request, bool Hidden)>();
        var service = new WebLoginService((request, hidden) =>
        {
            calls.Add((request, hidden));
            return Task.FromResult(false);
        });
        var config = new TestConfig(new Dictionary<string, string>
        {
            ["bayesdl-main.bayesdl_url"] = "https://token.bayesdl.com/base/",
        });

        var captured = await service.OpenLoginAsync("bayesdl-main", "bayesdl", config);

        Assert.AreEqual(1, calls.Count);
        Assert.IsFalse(calls[0].Hidden);
        Assert.IsFalse(captured);
        Assert.AreEqual("https://ai.bayesdl.com/base/login", calls[0].Request.LoginUrl);
        Assert.AreEqual("https://ai.bayesdl.com/base/login", calls[0].Request.CaptureUrl);
    }

    [TestMethod]
    public async Task FetchAsync_ForAlibabaHiddenCapture_UsesConfiguredDashboardDirectly()
    {
        var freshTime = DateTimeOffset.UtcNow;
        var calls = new List<(WebLoginService.WebLoginCaptureRequest Request, bool Hidden)>();
        WebLoginService? service = null;
        service = new WebLoginService((request, hidden) =>
        {
            calls.Add((request, hidden));
            service!.StoreResult(request.InstanceId, Snapshot(request.ProviderType, freshTime, "fresh"));
            return Task.FromResult(true);
        });
        const string dashboard =
            "https://modelstudio.console.alibabacloud.com/ap-southeast-1/?tab=coding-plan#/efm/coding_plan";
        var config = new TestConfig(new Dictionary<string, string>
        {
            ["alibaba-main.alibaba_url"] = dashboard,
        });
        service.StoreResult("alibaba-main", Snapshot("alibaba", DateTimeOffset.UtcNow.AddHours(-2), "stale"));

        _ = await service.FetchAsync("alibaba-main", "alibaba", config);

        Assert.AreEqual(1, calls.Count);
        Assert.IsTrue(calls[0].Hidden);
        Assert.AreEqual(dashboard, calls[0].Request.LoginUrl);
        Assert.AreEqual(dashboard, calls[0].Request.CaptureUrl);
    }

    [TestMethod]
    public async Task OpenLoginAsync_WhenCaptureSucceeds_ReturnsTrue()
    {
        var service = new WebLoginService((_, _) => Task.FromResult(true));

        var captured = await service.OpenLoginAsync("kimi-main", "kimi", new EmptyConfig());

        Assert.IsTrue(captured);
    }

    [TestMethod]
    public void ProfileFolderFor_DefaultLegacyInstance_UsesOldTauriProfile()
    {
        var profile = WebLoginService.ProfileFolderFor("mimo", "mimo");

        StringAssert.Contains(profile, Path.Combine("com.quotalens.app", "EBWebView"));
    }

    [TestMethod]
    public void ProfileFolderFor_GeneratedInstance_UsesIsolatedQuotaLensProfile()
    {
        var profile = WebLoginService.ProfileFolderFor("mimo-1234abcd", "mimo");

        StringAssert.Contains(profile, Path.Combine("QuotaLens", "WebView2Profiles", "mimo-1234abcd"));
        Assert.IsFalse(profile.Contains(Path.Combine("com.quotalens.app", "EBWebView"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task FetchAsync_WhenHiddenCaptureFailsAndCacheIsFresh_ReturnsCachedSnapshot()
    {
        // Arrange
        var freshTime = DateTimeOffset.UtcNow;
        var service = new WebLoginService((_, _) => Task.FromResult(false));
        service.StoreResult("bayesdl-main", Snapshot("bayesdl", freshTime, "cached"));

        // Act
        var snapshot = await service.FetchAsync("bayesdl-main", "bayesdl", new EmptyConfig());

        // Assert
        Assert.AreEqual(freshTime, snapshot.UpdatedAt);
        Assert.AreEqual("cached", snapshot.Primary.Label);
    }

    [TestMethod]
    public async Task FetchAsync_WithPersistedCachedSnapshot_LoadsSnapshotAfterServiceRestart()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var freshTime = DateTimeOffset.UtcNow;
            var writer = new WebLoginService((_, _) => Task.FromResult(false), cacheDir);
            writer.StoreResult("kimi-main", Snapshot("kimi", freshTime, "persisted"));

            var calls = new List<(WebLoginService.WebLoginCaptureRequest Request, bool Hidden)>();
            var reader = new WebLoginService((request, hidden) =>
            {
                calls.Add((request, hidden));
                return Task.FromResult(false);
            }, cacheDir);

            var snapshot = await reader.FetchAsync("kimi-main", "kimi", new EmptyConfig());

            Assert.AreEqual("persisted", snapshot.Primary.Label);
            Assert.AreEqual(freshTime, snapshot.UpdatedAt);
            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual("kimi-main", calls[0].Request.InstanceId);
            Assert.IsTrue(calls[0].Hidden);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task FetchAsync_WithLegacyPersistedSnapshot_LoadsBackwardCompatibleCache()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(cacheDir);
            var freshTime = DateTimeOffset.UtcNow;
            File.WriteAllText(
                Path.Combine(cacheDir, "kimi-main.json"),
                System.Text.Json.JsonSerializer.Serialize(Snapshot("kimi", freshTime, "legacy")));
            var service = new WebLoginService((_, _) => Task.FromResult(false), cacheDir);

            var snapshot = await service.FetchAsync("kimi-main", "kimi", new EmptyConfig());

            Assert.AreEqual("legacy", snapshot.Primary.Label);
            Assert.AreEqual(freshTime, snapshot.UpdatedAt);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task FetchAsync_WithMismatchedPersistedProviderType_IgnoresCacheAndDoesNotOpenHiddenCapture()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(
                Path.Combine(cacheDir, "kimi-main.json"),
                """
                {
                  "version": 1,
                  "providerType": "cursor",
                  "snapshot": {
                    "providerId": "cursor",
                    "name": "Cursor",
                    "primary": { "label": "Requests", "usedPercent": 10 },
                    "updatedAt": "2030-01-01T00:00:00Z",
                    "sourceLabel": "Cursor WebView",
                    "confidence": 0
                  }
                }
                """);
            var calls = new List<WebLoginService.WebLoginCaptureRequest>();
            var service = new WebLoginService((request, _) =>
            {
                calls.Add(request);
                return Task.FromResult(false);
            }, cacheDir);

            var exception = await ThrowsProviderExceptionAsync(
                () => service.FetchAsync("kimi-main", "kimi", new EmptyConfig()));

            StringAssert.Contains(exception.Message, "Login required");
            Assert.AreEqual(0, calls.Count);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task FetchAsync_ReturnsCloneSoDashboardMutationDoesNotCorruptCache()
    {
        var freshTime = DateTimeOffset.UtcNow;
        var service = new WebLoginService((_, _) => Task.FromResult(false));
        service.StoreResult("kimi-main", Snapshot("kimi", freshTime, "cached"));

        var returned = await service.FetchAsync("kimi-main", "kimi", new EmptyConfig());
        returned.ProviderId = "kimi-main";
        returned.Primary.Label = "mutated";

        var cached = service.GetCached("kimi-main", "kimi");

        Assert.IsNotNull(cached);
        Assert.AreEqual("kimi", cached!.ProviderId);
        Assert.AreEqual("cached", cached.Primary.Label);
    }

    [TestMethod]
    public async Task FetchAsync_WithCorruptPersistedSnapshot_IgnoresCacheAndDoesNotOpenHiddenCapture()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(Path.Combine(cacheDir, "kimi-main.json"), "{not json");
            var calls = new List<WebLoginService.WebLoginCaptureRequest>();
            var service = new WebLoginService((request, hidden) =>
            {
                calls.Add(request);
                return Task.FromResult(false);
            }, cacheDir);

            var exception = await ThrowsProviderExceptionAsync(
                () => service.FetchAsync("kimi-main", "kimi", new EmptyConfig()));

            StringAssert.Contains(exception.Message, "Login required");
            Assert.AreEqual(0, calls.Count);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [TestMethod]
    public void RemoveInstanceData_RemovesMemoryAndPersistedCache()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new WebLoginService((_, _) => Task.FromResult(false), cacheDir);
            service.StoreResult("kimi-main", Snapshot("kimi", DateTimeOffset.UtcNow, "cached"));
            var cacheFile = Path.Combine(cacheDir, "kimi-main.json");
            Assert.IsTrue(File.Exists(cacheFile));
            Assert.IsNotNull(service.GetCached("kimi-main"));

            service.RemoveInstanceData("kimi-main", "kimi");

            Assert.IsFalse(File.Exists(cacheFile));
            Assert.IsNull(service.GetCached("kimi-main"));
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [TestMethod]
    public void RemoveInstanceData_RemovesGeneratedProfileFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var profile = Path.Combine(root, "QuotaLens", "WebView2Profiles", "mimo-main");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "marker.txt"), "profile");
            var service = new WebLoginService((_, _) => Task.FromResult(false), cacheDirectory: null, localAppDataDirectory: root);

            service.RemoveInstanceData("mimo-main", "mimo");

            Assert.IsFalse(Directory.Exists(profile));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RemoveInstanceData_KeepsLegacySharedProfileFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var profile = Path.Combine(root, "com.quotalens.app", "EBWebView");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "marker.txt"), "legacy profile");
            var service = new WebLoginService((_, _) => Task.FromResult(false), cacheDirectory: null, localAppDataDirectory: root);

            service.RemoveInstanceData("mimo", "mimo");

            Assert.IsTrue(Directory.Exists(profile));
            Assert.IsTrue(File.Exists(Path.Combine(profile, "marker.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FetchAsync_WhenHiddenCaptureFailsAndCacheIsStale_ThrowsLoginRequired()
    {
        // Arrange
        var staleTime = DateTimeOffset.UtcNow.AddHours(-2);
        var service = new WebLoginService((_, _) => Task.FromResult(false));
        service.StoreResult("mimo-main", Snapshot("mimo", staleTime, "stale"));

        // Act
        var exception = await ThrowsProviderExceptionAsync(
            () => service.FetchAsync("mimo-main", "mimo", new EmptyConfig()));

        // Assert
        StringAssert.Contains(exception.Message, "Login required");
    }

    [TestMethod]
    public async Task FetchAsync_WhenHiddenCaptureFailsWithoutCache_ThrowsLoginRequired()
    {
        // Arrange
        var service = new WebLoginService((_, _) => Task.FromResult(false));

        // Act
        var exception = await ThrowsProviderExceptionAsync(
            () => service.FetchAsync("mimo-main", "mimo", new EmptyConfig()));

        // Assert
        StringAssert.Contains(exception.Message, "Login required");
    }

    [TestMethod]
    public void NormalizeSnapshot_UsesCatalogWebViewMetadata()
    {
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "old",
            Name = "Kimi · Custom",
            SourceLabel = "old source",
            Confidence = Confidence.Unofficial,
        };

        var normalized = WebLoginService.NormalizeSnapshot("kimi", snapshot);

        Assert.AreSame(snapshot, normalized);
        Assert.AreEqual("kimi", normalized.ProviderId);
        Assert.AreEqual("Kimi · Custom", normalized.Name);
        Assert.AreEqual("Kimi WebView", normalized.SourceLabel);
        Assert.AreEqual(Confidence.Unofficial, normalized.Confidence);
        Assert.AreEqual(ProviderSourceKind.PrivateDashboard, normalized.SourceKind);
        Assert.AreEqual(ProviderContractStability.PrivateContract, normalized.ContractStability);
    }

    private static string LoadWebLoginFixture(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "winui.Tests", "Fixtures", "web-login", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException($"Could not find WebLogin fixture '{fileName}'.");
    }

    private static ProviderSnapshot Snapshot(string providerType, DateTimeOffset updatedAt, string label) => new()
    {
        ProviderId = providerType,
        Name = providerType,
        Primary = new RateWindow
        {
            Label = label,
            UsedPercent = 0,
        },
        UpdatedAt = updatedAt,
        SourceLabel = "test",
        Confidence = Confidence.Official,
    };

    private static async Task<ProviderException> ThrowsProviderExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ProviderException exception)
        {
            return exception;
        }

        Assert.Fail("Expected ProviderException.");
        throw new InvalidOperationException("Unreachable.");
    }

    private sealed class EmptyConfig : TestConfig
    {
        public EmptyConfig() : base(new Dictionary<string, string>())
        {
        }
    }

    private class TestConfig(IReadOnlyDictionary<string, string> values) : IConfig
    {
        public string Get(string key, string fallback = "") =>
            values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            values.TryGetValue($"{instanceId}.{key}", out var value) ? value : fallback;

        public bool HasScoped(string instanceId, string key) =>
            values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) =>
            values.TryGetValue(key, out var value)
                ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                : fallback;
    }
}
