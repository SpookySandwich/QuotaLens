using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class SimpleApiProviderTests
{
    [TestMethod]
    public void ParseOpenRouter_MapsLimitResetTypeAndUsagePeriods()
    {
        var snapshot = SimpleApiProvider.ParseOpenRouter(Json("""
        {
          "data": {
            "limit": 100.0,
            "limit_remaining": 40.0,
            "limit_reset": "weekly",
            "usage": 25.0,
            "usage_daily": 1.25,
            "usage_weekly": 7.5,
            "usage_monthly": 18.75
          }
        }
        """));

        Assert.AreEqual("openrouter", snapshot.ProviderId);
        Assert.AreEqual(60, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(7 * 24 * 60, snapshot.Primary.WindowMinutes);
        StringAssert.Contains(snapshot.Primary.DetailText!, "resets weekly");
        Assert.AreEqual("Daily usage", snapshot.Secondary!.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Secondary.Kind);
        Assert.AreEqual("$1.25 used", snapshot.Secondary.ValueText);
        Assert.AreEqual("Current UTC day", snapshot.Secondary.DetailText);
        Assert.AreEqual("Weekly usage", snapshot.Tertiary!.Label);
        Assert.AreEqual("Monthly usage", snapshot.AdditionalWindows.Single().Label);
        Assert.IsNull(snapshot.Balance);
        Assert.AreEqual("OpenRouter API", snapshot.SourceLabel);
    }

    [TestMethod]
    public void ParseOpenRouter_NullRemainingMeansNoPerKeyCapNotUnlimitedFunding()
    {
        var snapshot = SimpleApiProvider.ParseOpenRouter(Json("""
        {
          "data": {
            "limit": null,
            "limit_remaining": null,
            "limit_reset": null,
            "usage_daily": 1,
            "usage_weekly": 2,
            "usage_monthly": 3
          }
        }
        """));

        Assert.AreEqual(0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("No per-key limit", snapshot.Primary.ValueText);
        Assert.AreEqual("Account funding is reported separately", snapshot.Primary.DetailText);
        Assert.IsNull(snapshot.Primary.ResetsAt);
        Assert.AreEqual(ProviderAvailabilityKind.Unknown, snapshot.AvailabilityKind);
        Assert.AreEqual(
            ProviderAvailabilityKind.Unknown,
            Quota.ProviderAvailabilityState(snapshot).Kind);
    }

    [TestMethod]
    public void ParseOpenRouter_ManagementCreditsAreOptionalAndMergedSeparately()
    {
        var key = Json("""
        {
          "data": {
            "limit": 100,
            "limit_remaining": 75,
            "limit_reset": "monthly",
            "usage_daily": 1,
            "usage_weekly": 5,
            "usage_monthly": 10
          }
        }
        """);
        var withoutManagement = SimpleApiProvider.ParseOpenRouter(key);
        var withManagement = SimpleApiProvider.ParseOpenRouter(key, Json("""
        {
          "data": {
            "total_credits": 100.0,
            "total_usage": 25.0
          }
        }
        """));

        Assert.IsNull(withoutManagement.Balance);
        Assert.AreEqual(75, withManagement.Balance!.Total, 0.001);
        Assert.AreEqual(25, withManagement.Balance.Paid, 0.001);
        Assert.AreEqual(100, withManagement.Balance.Granted, 0.001);
    }

    [TestMethod]
    public void ProviderSnapshotMetadata_ForSimpleApi_PreservesRuntimeIdentityAndSource()
    {
        var provider = new SimpleApiProvider("openai");
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "old",
            SourceLabel = "old source",
            Confidence = Confidence.Unofficial,
        };

        var normalized = ProviderSnapshotMetadata.Apply(provider, snapshot);

        Assert.AreSame(snapshot, normalized);
        Assert.AreEqual("old", normalized.ProviderId);
        Assert.AreEqual("OpenAI API", normalized.Name);
        Assert.AreEqual("old source", normalized.SourceLabel);
        Assert.AreEqual(Confidence.Official, normalized.Confidence);
    }

    [TestMethod]
    public void ParseMoonshot_MapsBalanceResponse()
    {
        var snapshot = SimpleApiProvider.ParseMoonshot(Json("""
        {
          "code": 0,
          "status": true,
          "scode": "ok",
          "data": {
            "available_balance": 12.5,
            "voucher_balance": 2.5,
            "cash_balance": 10.0
          }
        }
        """));

        Assert.AreEqual("moonshot", snapshot.ProviderId);
        Assert.AreEqual(12.5, snapshot.Balance!.Total, 0.001);
        Assert.AreEqual(10.0, snapshot.Balance.Paid, 0.001);
        Assert.AreEqual(2.5, snapshot.Balance.Granted, 0.001);
    }

    [TestMethod]
    public void ParseMoonshot_WithNegativeAvailableBalance_LabelsDeficit()
    {
        var snapshot = SimpleApiProvider.ParseMoonshot(Json("""
        {
          "code": 0,
          "status": true,
          "data": {
            "available_balance": -1.25,
            "voucher_balance": 0,
            "cash_balance": -1.25
          }
        }
        """));

        Assert.AreEqual(100, snapshot.Primary.UsedPercent, 0.001);
        StringAssert.Contains(snapshot.Primary.DetailText!, "$1.25 deficit");
        Assert.AreEqual(-1.25, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ResolveMoonshotUrl_SupportsInternationalAndChinaOfficialHosts()
    {
        var international = new FakeConfig(new Dictionary<string, string>());
        var china = new FakeConfig(new Dictionary<string, string>
        {
            ["moonshot-cn.moonshot_base_url"] = "https://api.moonshot.cn/",
        });

        Assert.AreEqual(
            "https://api.moonshot.ai/v1/users/me/balance",
            SimpleApiProvider.ResolveMoonshotUrl("moonshot", international));
        Assert.AreEqual(
            "https://api.moonshot.cn/v1/users/me/balance",
            SimpleApiProvider.ResolveMoonshotUrl("moonshot-cn", china));
    }

    [TestMethod]
    public void ParseVenice_PrefersUsdBalanceWhenAvailable()
    {
        var snapshot = SimpleApiProvider.ParseVenice(Json("""
        {
          "canConsume": true,
          "consumptionCurrency": "USD",
          "balances": {
            "usd": "3.25",
            "diem": "42"
          }
        }
        """));

        Assert.AreEqual("venice", snapshot.ProviderId);
        Assert.AreEqual("USD", snapshot.Balance!.Currency);
        Assert.AreEqual(3.25, snapshot.Balance.Total, 0.001);
        Assert.AreEqual(0, snapshot.Primary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseVenice_WithDiemEpochAllocation_MapsFiniteQuotaProgress()
    {
        var snapshot = SimpleApiProvider.ParseVenice(Json("""
        {
          "canConsume": true,
          "consumptionCurrency": "BUNDLED_CREDITS",
          "balances": {
            "usd": 10,
            "diem": "75"
          },
          "diemEpochAllocation": "100"
        }
        """));

        Assert.AreEqual("DIEM", snapshot.Balance!.Currency);
        Assert.AreEqual(75, snapshot.Balance.Total, 0.001);
        Assert.AreEqual(25, snapshot.Balance.Paid, 0.001);
        Assert.AreEqual(100, snapshot.Balance.Granted, 0.001);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("DIEM 75.00 / 100.00 epoch allocation", snapshot.Primary.DetailText);
    }

    [TestMethod]
    public void ParseVenice_WhenApiConsumptionIsDisabled_ShowsUnavailableEvenWithBalance()
    {
        var snapshot = SimpleApiProvider.ParseVenice(Json("""
        {
          "canConsume": false,
          "consumptionCurrency": "USD",
          "balances": {
            "usd": 100,
            "diem": null
          },
          "diemEpochAllocation": null
        }
        """));

        Assert.AreEqual(100, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Balance unavailable for API calls", snapshot.Primary.DetailText);
    }

    [TestMethod]
    public void ParseCrof_MapsUsableRequestsToUsedPercent()
    {
        var snapshot = SimpleApiProvider.ParseCrof(Json("""
        {
          "credits": 9,
          "requests_plan": 100,
          "usable_requests": 40
        }
        """));

        Assert.AreEqual("crof", snapshot.ProviderId);
        Assert.AreEqual(60, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("40 requests left", snapshot.Primary.DetailText);
        Assert.AreEqual(24 * 60, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("Credits", snapshot.Secondary!.Label);
        Assert.AreEqual("$9.00", snapshot.Secondary.DetailText);
        Assert.AreEqual("USD", snapshot.Balance!.Currency);
        Assert.AreEqual(9, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseCrof_CreditsOnlyPayloadUsesBalanceWithoutInventingRequestQuota()
    {
        var snapshot = SimpleApiProvider.ParseCrof(Json("""
        {
          "credits": 9.0441,
          "requests_plan": null,
          "usable_requests": null,
          "usage": {
            "deepseek-v4-flash": { "total_tokens": 155 }
          }
        }
        """));

        Assert.AreEqual("Credits", snapshot.Primary.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual(0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("$9.04", snapshot.Primary.ValueText);
        Assert.AreEqual("$9.04", snapshot.Primary.DetailText);
        Assert.IsNull(snapshot.Primary.ResetsAt);
        Assert.IsNull(snapshot.Secondary);
        Assert.AreEqual(9.0441, snapshot.Balance!.Total, 0.0001);
        Assert.AreEqual(
            ProviderAvailabilityKind.Unknown,
            Quota.ProviderAvailabilityState(snapshot).Kind);
    }

    [TestMethod]
    public void NextCrofRequestReset_UsesNextMidnightInChicago()
    {
        var reset = SimpleApiProvider.NextCrofRequestReset(DateTimeOffset.Parse("2026-01-15T12:00:00Z"));

        Assert.AreEqual(
            DateTimeOffset.Parse("2026-01-16T06:00:00Z"),
            DateTimeOffset.Parse(reset).ToUniversalTime());
    }

    [TestMethod]
    public void OpenAiCredentialContract_UsesOnlyExplicitAdminKeyEnvironmentVariable()
    {
        CollectionAssert.AreEqual(
            new[] { "OPENAI_ADMIN_KEY" },
            SimpleApiProvider.EnvironmentKeysFor("openai").ToArray());

        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["openai-main.openai_key"] = "sk-admin-test",
        });

        Assert.AreEqual(
            "sk-admin-test",
            SimpleApiProvider.ResolveCredential("openai", "openai-main", config));
    }

    [TestMethod]
    public void Catalog_ExposesSeparatedOpenRouterManagementAndOpenAiAdminConfiguration()
    {
        var openRouterFields = Catalog.Fields["openrouter"].ToDictionary(field => field.Key);
        Assert.IsTrue(openRouterFields["openrouter_key"].IsRequired);
        Assert.IsFalse(openRouterFields["openrouter_management_key"].IsRequired);
        Assert.IsTrue(openRouterFields["openrouter_management_key"].IsPassword);
        Assert.IsTrue(Catalog.SensitiveKeys.Contains("openrouter_management_key"));

        var openAiFields = Catalog.Fields["openai"].ToDictionary(field => field.Key);
        Assert.AreEqual("Organization Admin Key", openAiFields["openai_key"].Label);
        Assert.IsTrue(openAiFields["openai_key"].IsRequired);
        Assert.IsTrue(openAiFields.ContainsKey("openai_project_ids"));
    }

    [TestMethod]
    public void OpenRouterCredentialContract_NeverFallsBackFromManagementKeyToOrdinaryKey()
    {
        var withoutManagement = new FakeConfig(new Dictionary<string, string>
        {
            ["router.openrouter_key"] = "sk-or-ordinary",
            ["router.openrouter_management_key"] = "",
        });
        var withManagement = new FakeConfig(new Dictionary<string, string>
        {
            ["router.openrouter_key"] = "sk-or-ordinary",
            ["router.openrouter_management_key"] = "sk-or-management",
        });

        Assert.AreEqual(
            "sk-or-ordinary",
            SimpleApiProvider.ResolveCredential("openrouter", "router", withoutManagement));
        Assert.IsNull(SimpleApiProvider.ResolveOpenRouterManagementKey("router", withoutManagement));
        Assert.AreEqual(
            "sk-or-management",
            SimpleApiProvider.ResolveOpenRouterManagementKey("router", withManagement));
    }

    [TestMethod]
    public async Task FetchOpenRouter_UsesOrdinaryKeyForKeyEndpointAndManagementKeyForCredits()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/key" => JsonResponse("""
            {
              "data": {
                "limit": 100,
                "limit_remaining": 75,
                "limit_reset": "monthly",
                "usage_daily": 1,
                "usage_weekly": 5,
                "usage_monthly": 10
              }
            }
            """),
            "/api/v1/credits" => JsonResponse("""
            {
              "data": {
                "total_credits": 100,
                "total_usage": 25
              }
            }
            """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var provider = new SimpleApiProvider("openrouter", client);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["router.openrouter_key"] = "sk-or-ordinary",
            ["router.openrouter_management_key"] = "sk-or-management",
        });

        var snapshot = await provider.FetchAsync("router", config, CancellationToken.None);

        Assert.AreEqual(2, handler.Requests.Count);
        var keyRequest = handler.Requests.Single(request => request.Path == "/api/v1/key");
        var creditsRequest = handler.Requests.Single(request => request.Path == "/api/v1/credits");
        Assert.AreEqual("Bearer sk-or-ordinary", keyRequest.Authorization);
        Assert.AreEqual("Bearer sk-or-management", creditsRequest.Authorization);
        Assert.AreEqual(75, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public async Task FetchOpenRouter_WithoutManagementKey_NeverCallsCreditsEndpoint()
    {
        var handler = new RecordingHandler(request => JsonResponse("""
        {
          "data": {
            "limit": null,
            "limit_remaining": null,
            "limit_reset": null,
            "usage_daily": 0,
            "usage_weekly": 0,
            "usage_monthly": 0
          }
        }
        """));
        using var client = new HttpClient(handler);
        var provider = new SimpleApiProvider("openrouter", client);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["router.openrouter_key"] = "sk-or-ordinary",
            ["router.openrouter_management_key"] = "",
        });

        var snapshot = await provider.FetchAsync("router", config, CancellationToken.None);

        Assert.AreEqual("No per-key limit", snapshot.Primary.ValueText);
        Assert.AreEqual(ProviderAvailabilityKind.Unknown, snapshot.AvailabilityKind);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("/api/v1/key", handler.Requests.Single().Path);
        Assert.AreEqual("Bearer sk-or-ordinary", handler.Requests.Single().Authorization);
    }

    [TestMethod]
    public async Task FetchOpenAi_UsesAdminKeyForBothEndpointsAndAggregatesPagination()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/organization/usage/completions")
            {
                return request.RequestUri.Query.Contains("page=usage_page_2", StringComparison.Ordinal)
                    ? JsonResponse("""
                    {
                      "data": [
                        { "results": [
                          { "input_tokens": 200, "output_tokens": 75, "num_model_requests": 2 }
                        ] }
                      ],
                      "has_more": false,
                      "next_page": null
                    }
                    """)
                    : JsonResponse("""
                    {
                      "data": [
                        { "results": [
                          {
                            "input_tokens": 100,
                            "output_tokens": 50,
                            "input_audio_tokens": 90,
                            "output_audio_tokens": 45,
                            "num_model_requests": 1
                          }
                        ] }
                      ],
                      "has_more": true,
                      "next_page": "usage_page_2"
                    }
                    """);
            }

            if (request.RequestUri.AbsolutePath == "/v1/organization/costs")
            {
                return request.RequestUri.Query.Contains("page=cost_page_2", StringComparison.Ordinal)
                    ? JsonResponse("""
                    {
                      "data": [
                        { "results": [
                          { "amount": { "value": 0.02, "currency": "usd" } }
                        ] }
                      ],
                      "has_more": false,
                      "next_page": null
                    }
                    """)
                    : JsonResponse("""
                    {
                      "data": [
                        { "results": [
                          { "amount": { "value": 0.40, "currency": "usd" } }
                        ] }
                      ],
                      "has_more": true,
                      "next_page": "cost_page_2"
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new HttpClient(handler);
        var provider = new SimpleApiProvider("openai", client);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["openai-main.openai_key"] = "sk-admin-test",
            ["openai-main.openai_project_ids"] = "proj_a",
        });

        var snapshot = await provider.FetchAsync("openai-main", config, CancellationToken.None);

        Assert.AreEqual(4, handler.Requests.Count);
        Assert.IsTrue(handler.Requests.All(request => request.Authorization == "Bearer sk-admin-test"));
        Assert.AreEqual(2, handler.Requests.Count(request => request.Path == "/v1/organization/usage/completions"));
        Assert.AreEqual(2, handler.Requests.Count(request => request.Path == "/v1/organization/costs"));
        Assert.IsTrue(handler.Requests.All(request => request.Query.Contains("project_ids=proj_a", StringComparison.Ordinal)));
        Assert.IsTrue(handler.Requests.Any(request => request.Query.Contains("page=usage_page_2", StringComparison.Ordinal)));
        Assert.IsTrue(handler.Requests.Any(request => request.Query.Contains("page=cost_page_2", StringComparison.Ordinal)));
        Assert.AreEqual("$0.42 spent", snapshot.Primary.ValueText);
        Assert.AreEqual("300 tokens", snapshot.Secondary!.ValueText);
        Assert.AreEqual("125 tokens", snapshot.Tertiary!.ValueText);
        Assert.AreEqual("3 requests", snapshot.AdditionalWindows.Single().ValueText);
    }

    [TestMethod]
    public void ParseOpenAiUsagePage_SumsInclusiveTotalsWithoutSubdivisionDoubleCounting()
    {
        var page = SimpleApiProvider.ParseOpenAiUsagePage(Json("""
        {
          "object": "page",
          "data": [
            {
              "object": "bucket",
              "results": [
                {
                  "input_tokens": 1000,
                  "output_tokens": 500,
                  "input_audio_tokens": 900,
                  "output_audio_tokens": 450,
                  "input_image_tokens": 800,
                  "output_image_tokens": 400,
                  "num_model_requests": 5
                },
                {
                  "input_tokens": 250,
                  "output_tokens": 100,
                  "input_text_tokens": 250,
                  "output_text_tokens": 100,
                  "num_model_requests": 2
                }
              ]
            }
          ],
          "has_more": true,
          "next_page": "page_2"
        }
        """));

        Assert.AreEqual(1250, page.InputTokens, 0.001);
        Assert.AreEqual(600, page.OutputTokens, 0.001);
        Assert.AreEqual(7, page.Requests, 0.001);
        Assert.IsTrue(page.HasMore);
        Assert.AreEqual("page_2", page.NextPage);
    }

    [TestMethod]
    public void ParseOpenAiCostPage_SumsOfficialAmountObjects()
    {
        var page = SimpleApiProvider.ParseOpenAiCostPage(Json("""
        {
          "object": "page",
          "data": [
            {
              "object": "bucket",
              "results": [
                { "amount": { "value": 0.06, "currency": "usd" } },
                { "amount": { "value": 1.20, "currency": "usd" } }
              ]
            }
          ],
          "has_more": false,
          "next_page": null
        }
        """));

        Assert.AreEqual(1.26, page.Amount, 0.001);
        Assert.AreEqual("USD", page.Currency);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public void BuildOpenAiRequestUrl_IncludesRequiredStartProjectFiltersAndCursor()
    {
        var url = SimpleApiProvider.BuildOpenAiRequestUrl(
            "https://api.openai.com/v1/organization/usage/completions",
            1730419200,
            new[] { "proj alpha", "proj/beta" },
            "page_2+=/");

        StringAssert.StartsWith(url, "https://api.openai.com/v1/organization/usage/completions?");
        StringAssert.Contains(url, "start_time=1730419200");
        StringAssert.Contains(url, "bucket_width=1d");
        StringAssert.Contains(url, "project_ids=proj%20alpha");
        StringAssert.Contains(url, "project_ids=proj%2Fbeta");
        StringAssert.Contains(url, "page=page_2%2B%3D%2F");
    }

    [TestMethod]
    public void ResolveOpenAiProjectIds_ParsesScopedCommaSeparatedFilter()
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["openai-main.openai_project_ids"] = "proj_a, proj_b;proj_a",
        });

        CollectionAssert.AreEqual(
            new[] { "proj_a", "proj_b" },
            SimpleApiProvider.ResolveOpenAiProjectIds("openai-main", config).ToArray());
    }

    [TestMethod]
    public void AdvanceOpenAiPage_ContinuesOnceAndRejectsRepeatedCursor()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Assert.AreEqual("page_2", SimpleApiProvider.AdvanceOpenAiPage(true, "page_2", seen));
        Assert.ThrowsExactly<ProviderException>(() =>
            SimpleApiProvider.AdvanceOpenAiPage(true, "page_2", seen));
        Assert.IsNull(SimpleApiProvider.AdvanceOpenAiPage(false, "ignored", seen));
    }

    [TestMethod]
    public void BuildOpenAiSnapshot_UsesInformationalCostTokenAndRequestWindows()
    {
        var snapshot = SimpleApiProvider.BuildOpenAiSnapshot(1250, 600, 7, 1.26, "usd", 30);

        Assert.AreEqual("openai", snapshot.ProviderId);
        Assert.AreEqual("30-day cost", snapshot.Primary.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("$1.26 spent", snapshot.Primary.ValueText);
        Assert.AreEqual("1,250 tokens", snapshot.Secondary!.ValueText);
        Assert.AreEqual("600 tokens", snapshot.Tertiary!.ValueText);
        Assert.AreEqual("7 requests", snapshot.AdditionalWindows.Single().ValueText);
        Assert.IsNull(snapshot.Balance);
    }

    [TestMethod]
    public void ParseCopilot_MapsPremiumAndChatQuotaSnapshots()
    {
        var snapshot = SimpleApiProvider.ParseCopilot(Json("""
        {
          "copilot_plan": "business",
          "quota_reset_date": "2030-01-01T00:00:00Z",
          "quota_snapshots": {
            "premium_interactions": {
              "entitlement": 100,
              "remaining": 40,
              "percent_remaining": 40,
              "quota_id": "premium"
            },
            "chat": {
              "entitlement": 200,
              "remaining": 150,
              "quota_id": "chat"
            }
          }
        }
        """));

        Assert.AreEqual("copilot", snapshot.ProviderId);
        Assert.AreEqual("Copilot", snapshot.Name);
        Assert.AreEqual("Premium", snapshot.Primary.Label);
        Assert.AreEqual(60, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("60/100", snapshot.Primary.DetailText);
        Assert.AreEqual("Chat", snapshot.Secondary!.Label);
        Assert.AreEqual(25, snapshot.Secondary.UsedPercent, 0.001);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Primary.ResetsAt));
    }

    [TestMethod]
    public void ParseCopilot_MapsMonthlyLimitedQuotaFallback()
    {
        var snapshot = SimpleApiProvider.ParseCopilot(Json("""
        {
          "copilot_plan": "pro",
          "monthly_quotas": {
            "chat": 100,
            "completions": 50
          },
          "limited_user_quotas": {
            "chat": 25,
            "completions": 10
          }
        }
        """));

        Assert.AreEqual("Copilot", snapshot.Name);
        Assert.AreEqual(80, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(75, snapshot.Secondary!.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseCopilot_OnlyUnlimitedQuotaRendersInformationalPlanState()
    {
        var snapshot = SimpleApiProvider.ParseCopilot(Json("""
        {
          "copilot_plan": "individual",
          "quota_snapshots": {
            "chat_messages": {
              "entitlement": 0,
              "remaining": 0,
              "quota_id": "chat_messages",
              "unlimited": true
            }
          }
        }
        """));

        Assert.AreEqual("Copilot", snapshot.Name);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("Plan quota", snapshot.Primary.Label);
        Assert.AreEqual("Unlimited", snapshot.Primary.ValueText);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void ParseCopilot_FinitePremiumOmitsUnlimitedChatQuota()
    {
        var snapshot = SimpleApiProvider.ParseCopilot(Json("""
        {
          "copilot_plan": "individual",
          "quota_snapshots": {
            "premium_interactions": {
              "entitlement": 200,
              "remaining": 156.2,
              "percent_remaining": 78.1
            },
            "chat_messages": {
              "entitlement": 0,
              "remaining": 0,
              "unlimited": true
            }
          }
        }
        """));

        Assert.AreEqual(21.9, snapshot.Primary.UsedPercent, 0.0001);
        Assert.IsNull(snapshot.Secondary);
    }

    [TestMethod]
    public void ParseCopilot_UnlimitedDirectChatFallsBackToFiniteMonthlyChatQuota()
    {
        var snapshot = SimpleApiProvider.ParseCopilot(Json("""
        {
          "copilot_plan": "individual",
          "quota_snapshots": {
            "chat": {
              "entitlement": 0,
              "remaining": 0,
              "unlimited": true
            }
          },
          "monthly_quotas": { "chat": 100 },
          "limited_user_quotas": { "chat": 60 }
        }
        """));

        Assert.AreEqual("Chat", snapshot.Primary.Label);
        Assert.AreEqual(40, snapshot.Primary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseElevenLabs_MapsCharacterAndVoiceQuotaWindows()
    {
        var snapshot = SimpleApiProvider.ParseElevenLabs(Json("""
        {
          "tier": "creator",
          "status": "active",
          "character_count": 250,
          "character_limit": 1000,
          "voice_slots_used": 1,
          "voice_limit": 3,
          "professional_voice_slots_used": 2,
          "professional_voice_limit": 4,
          "next_character_count_reset_unix": 1893456000
        }
        """));

        Assert.AreEqual("elevenlabs", snapshot.ProviderId);
        Assert.AreEqual("ElevenLabs", snapshot.Name);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Voice slots", snapshot.Secondary!.Label);
        Assert.AreEqual(2d / 4d * 100d, snapshot.Tertiary!.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseElevenLabs_WithCanceledSubscription_PreservesLifecycleInCardContent()
    {
        var snapshot = SimpleApiProvider.ParseElevenLabs(Json("""
        {
          "tier": "creator",
          "status": "canceled",
          "character_count": 250,
          "character_limit": 1000
        }
        """));

        ProviderSnapshotIdentity.Normalize("elevenlabs", snapshot);

        Assert.AreEqual(EntitlementStatus.Expired, snapshot.EntitlementStatus);
        Assert.AreEqual("ElevenLabs", snapshot.Name);
        Assert.IsNull(snapshot.PlanName);
        StringAssert.Contains(snapshot.Primary.DetailText, "Status: Canceled");
    }

    [TestMethod]
    public void ParseWarp_MapsRequestLimitAndBonusCredits()
    {
        var snapshot = SimpleApiProvider.ParseWarp(Json("""
        {
          "data": {
            "user": {
              "__typename": "UserOutput",
              "user": {
                "requestLimitInfo": {
                  "isUnlimited": false,
                  "nextRefreshTime": "2030-01-01T00:00:00Z",
                  "requestLimit": 100,
                  "requestsUsedSinceLastRefresh": 25
                },
                "bonusGrants": [
                  {
                    "requestCreditsGranted": 10,
                    "requestCreditsRemaining": 4,
                    "expiration": "2030-02-01T00:00:00Z"
                  }
                ],
                "workspaces": []
              }
            }
          }
        }
        """));

        Assert.AreEqual("warp", snapshot.ProviderId);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Bonus credits", snapshot.Secondary!.Label);
        Assert.AreEqual(60, snapshot.Secondary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseWarp_UnlimitedPlanPreservesExplicitAvailability()
    {
        var snapshot = SimpleApiProvider.ParseWarp(Json("""
        {
          "data": {
            "user": {
              "user": {
                "requestLimitInfo": {
                  "isUnlimited": true,
                  "requestLimit": 0,
                  "requestsUsedSinceLastRefresh": 123
                },
                "bonusGrants": [],
                "workspaces": []
              }
            }
          }
        }
        """));

        Assert.AreEqual(ProviderAvailabilityKind.Unlimited, snapshot.AvailabilityKind);
        Assert.AreEqual(ProviderAvailabilityKind.Unlimited, Quota.ProviderAvailabilityState(snapshot).Kind);
    }

    [TestMethod]
    public void ParseCodebuff_MapsRemainingBalanceToCredits()
    {
        var snapshot = SimpleApiProvider.ParseCodebuff(Json("""
        {
          "usage": 25,
          "quota": 100,
          "remainingBalance": 75,
          "next_quota_reset": "2030-01-01T00:00:00Z"
        }
        """));

        Assert.AreEqual("codebuff", snapshot.ProviderId);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(75, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public async Task FetchCodebuff_UsesExpectedEndpointsAndMergesSubscriptionMetadata()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/usage" => JsonResponse("""
            {
              "usage": 25,
              "quota": 100,
              "remainingBalance": 75,
              "autoTopupEnabled": true,
              "next_quota_reset": "2030-01-01T00:00:00Z"
            }
            """),
            "/api/user/subscription" => JsonResponse("""
            {
              "hasSubscription": true,
              "subscription": {
                "status": "active",
                "displayName": "Pro",
                "billingPeriodEnd": "2030-02-01T00:00:00Z"
              },
              "rateLimit": {
                "weeklyUsed": 2100,
                "weeklyLimit": 7000,
                "weeklyResetsAt": "2030-01-08T00:00:00Z"
              },
              "user": {
                "email": "user@example.com"
              }
            }
            """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var provider = new SimpleApiProvider("codebuff", client);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["codebuff-main.codebuff_key"] = "cb-test-token",
        });

        var snapshot = await provider.FetchAsync("codebuff-main", config, CancellationToken.None);

        Assert.HasCount(2, handler.Requests);
        var usageRequest = handler.Requests.Single(request => request.Path == "/api/v1/usage");
        Assert.AreEqual(HttpMethod.Post, usageRequest.Method);
        Assert.AreEqual("Bearer cb-test-token", usageRequest.Authorization);
        Assert.IsNotNull(usageRequest.Body);
        using (var body = JsonDocument.Parse(usageRequest.Body))
        {
            Assert.HasCount(1, body.RootElement.EnumerateObject().ToArray());
            Assert.AreEqual("quotalens-usage", body.RootElement.GetProperty("fingerprintId").GetString());
        }

        var subscriptionRequest = handler.Requests.Single(request => request.Path == "/api/user/subscription");
        Assert.AreEqual(HttpMethod.Get, subscriptionRequest.Method);
        Assert.AreEqual("Bearer cb-test-token", subscriptionRequest.Authorization);
        Assert.IsNull(subscriptionRequest.Body);

        Assert.AreEqual("Codebuff · Pro", snapshot.Name);
        Assert.AreEqual(EntitlementStatus.Active, snapshot.EntitlementStatus);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual(30, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual(7 * 24 * 60, snapshot.Secondary.WindowMinutes);
        Assert.AreEqual(
            DateTimeOffset.Parse("2030-01-08T00:00:00Z"),
            DateTimeOffset.Parse(snapshot.Secondary.ResetsAt!));
        Assert.AreEqual(75, snapshot.Balance!.Total, 0.001);
        var account = snapshot.Accounts.Single();
        Assert.AreEqual("user@example.com", account.Email);
        Assert.AreEqual("Pro", account.Plan);
        var subscription = snapshot.AdditionalWindows.Single(window => window.Label == "Subscription");
        Assert.AreEqual(RateWindowKind.Informational, subscription.Kind);
        Assert.AreEqual("Pro · Active", subscription.ValueText);
        Assert.AreEqual(
            DateTimeOffset.Parse("2030-02-01T00:00:00Z"),
            DateTimeOffset.Parse(subscription.ResetsAt!));
        var autoTopUp = snapshot.AdditionalWindows.Single(window => window.Label == "Auto top-up");
        Assert.AreEqual("Enabled", autoTopUp.ValueText);
    }

    [TestMethod]
    [DataRow("http")]
    [DataRow("malformed")]
    [DataRow("network")]
    public async Task FetchCodebuff_WhenOptionalSubscriptionCannotBeUsed_PreservesUsage(
        string failureMode)
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/usage" => JsonResponse("""
            {
              "usage": 10,
              "quota": 20,
              "remainingBalance": 10
            }
            """),
            "/api/user/subscription" => failureMode switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "malformed" => JsonResponse("not-json"),
                "network" => throw new HttpRequestException("offline"),
                _ => throw new AssertFailedException($"Unknown failure mode: {failureMode}"),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var provider = new SimpleApiProvider("codebuff", client);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["codebuff-main.codebuff_key"] = "cb-test-token",
        });

        var snapshot = await provider.FetchAsync("codebuff-main", config, CancellationToken.None);

        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual(50, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(10, snapshot.Balance!.Total, 0.001);
        Assert.AreEqual("Codebuff", snapshot.Name);
        Assert.IsNull(snapshot.Secondary);
        Assert.IsEmpty(snapshot.AdditionalWindows);
        Assert.IsEmpty(snapshot.Accounts);
    }

    [TestMethod]
    public async Task FetchCodebuff_WhenRequiredUsageFails_DoesNotCallSubscription()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/usage" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "/api/user/subscription" => throw new AssertFailedException(
                "Subscription enrichment must not run when required usage fails."),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var provider = new SimpleApiProvider("codebuff", client);
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["codebuff-main.codebuff_key"] = "cb-test-token",
        });

        var exception = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            provider.FetchAsync("codebuff-main", config, CancellationToken.None));

        StringAssert.Contains(exception.Message, "HTTP 503");
        Assert.HasCount(1, handler.Requests);
        Assert.AreEqual(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.AreEqual("/api/v1/usage", handler.Requests.Single().Path);
    }

    [TestMethod]
    public void ParseCodebuff_SubscriptionWithoutWeeklyUsage_DoesNotInventAQuotaBar()
    {
        var snapshot = SimpleApiProvider.ParseCodebuff(
            Json("""{"usage":25,"quota":100,"remainingBalance":75}"""),
            Json("""
            {
              "subscription": {
                "status": "expired",
                "tier": "pro"
              },
              "rateLimit": {
                "weeklyLimit": 7000
              }
            }
            """));

        Assert.IsNull(snapshot.Secondary);
        Assert.AreEqual("Codebuff", snapshot.Name);
        Assert.AreEqual(EntitlementStatus.Expired, snapshot.EntitlementStatus);
    }

    [TestMethod]
    public void ParseSynthetic_MapsKnownQuotaSlotsWithoutShiftingMissingSlots()
    {
        var snapshot = SimpleApiProvider.ParseSynthetic(Json("""
        {
          "data": {
            "planName": "Team",
            "rollingFiveHourLimit": {
              "used": 20,
              "limit": 100,
              "windowHours": 5
            },
            "weeklyTokenLimit": {
              "remaining": 60,
              "limit": 100,
              "resetAt": "2030-01-01T00:00:00Z"
            }
          }
        }
        """));

        Assert.AreEqual("synthetic", snapshot.ProviderId);
        Assert.AreEqual("Synthetic", snapshot.Name);
        Assert.AreEqual("Rolling Five Hour Limit", snapshot.Primary.Label);
        Assert.AreEqual(20, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Weekly Token Limit", snapshot.Secondary!.Label);
        Assert.AreEqual(40, snapshot.Secondary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseSynthetic_FallbackDiscoveryPreservesFourthAndLaterQuotaLanes()
    {
        var snapshot = SimpleApiProvider.ParseSynthetic(Json("""
        {
          "quotas": [
            { "name": "Lane 1", "used": 1, "limit": 10 },
            { "name": "Lane 2", "used": 2, "limit": 10 },
            { "name": "Lane 3", "used": 3, "limit": 10 },
            { "name": "Lane 4", "used": 4, "limit": 10 },
            { "name": "Lane 5", "used": 5, "limit": 10 }
          ]
        }
        """));

        Assert.AreEqual("Lane 1", snapshot.Primary.Label);
        Assert.AreEqual("Lane 2", snapshot.Secondary!.Label);
        Assert.AreEqual("Lane 3", snapshot.Tertiary!.Label);
        CollectionAssert.AreEqual(
            new[] { "Lane 4", "Lane 5" },
            snapshot.AdditionalWindows.Select(window => window.Label).ToArray());
    }

    [TestMethod]
    public void ParseSynthetic_MergesKnownAndNewlyDiscoveredQuotaLanesWithoutDuplicates()
    {
        var snapshot = SimpleApiProvider.ParseSynthetic(Json("""
        {
          "data": {
            "rollingFiveHourLimit": { "used": 1, "limit": 10 },
            "weeklyTokenLimit": { "used": 2, "limit": 10 },
            "newLimits": [
              { "name": "Agent daily", "used": 3, "limit": 10 },
              { "name": "Search daily", "used": 4, "limit": 10 }
            ]
          }
        }
        """));

        var actualLabels = ProviderSnapshotWindows.AllWindows(snapshot).Select(window => window.Label).ToArray();
        CollectionAssert.AreEqual(
            new[] { "Rolling Five Hour Limit", "Weekly Token Limit", "Agent Daily", "Search Daily" },
            actualLabels,
            $"Actual labels: {string.Join(", ", actualLabels)}");
    }

    [TestMethod]
    public void ParseZai_MapsTokenAndMonthlyLimits()
    {
        var snapshot = SimpleApiProvider.ParseZai(Json("""
        {
          "code": 200,
          "success": true,
          "msg": "success",
          "data": {
            "planName": "Pro",
            "limits": [
              {
                "type": "TOKENS_LIMIT",
                "unit": 6,
                "number": 1,
                "usage": 1000,
                "currentValue": 250,
                "remaining": 750,
                "percentage": 25,
                "nextResetTime": 1893456000000
              },
              {
                "type": "TIME_LIMIT",
                "unit": 5,
                "number": 1,
                "percentage": 0
              }
            ]
          }
        }
        """));

        Assert.AreEqual("zai", snapshot.ProviderId);
        Assert.AreEqual("z.ai", snapshot.Name);
        Assert.AreEqual("1 week window", snapshot.Primary.Label);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Monthly", snapshot.Secondary!.DetailText);
        Assert.IsNull(snapshot.Secondary.WindowMinutes);
    }

    [TestMethod]
    public void ParseLlmProxy_MapsAggregateQuotaStats()
    {
        var snapshot = SimpleApiProvider.ParseLlmProxy(Json("""
        {
          "providers": {
            "openai": {
              "credential_count": 3,
              "active_count": 2,
              "exhausted_count": 1,
              "total_requests": 20,
              "tokens": {
                "input_cached": 5,
                "input_uncached": 10,
                "output": 15
              },
              "approx_cost": 1.25,
              "quota_groups": [
                {
                  "remaining_percent": 40,
                  "reset_time": "2030-01-01T00:00:00Z"
                }
              ]
            }
          },
          "summary": {
            "total_requests": 20,
            "total_tokens": 30,
            "approx_cost": 1.25
          }
        }
        """));

        Assert.AreEqual("llmproxy", snapshot.ProviderId);
        Assert.AreEqual("LLM Proxy", snapshot.Name);
        Assert.AreEqual(60, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Secondary!.Kind);
        Assert.AreEqual("20 requests", snapshot.Secondary.ValueText);
        Assert.AreEqual("30 tokens", snapshot.Tertiary!.ValueText);
        Assert.IsTrue(snapshot.AdditionalWindows.Any(window =>
            window.Label == "Credentials" && window.ValueText == "2/3 active · 1 exhausted"));
        Assert.IsTrue(snapshot.AdditionalWindows.Any(window =>
            window.Label == "Approx. spend" && window.ValueText == "$1.25"));
        Assert.IsTrue(snapshot.AdditionalWindows.Any(window =>
            window.Label == "openai" && window.ValueText == "20 req · 30 tok · $1.25"));
    }

    [TestMethod]
    public void ParseLlmProxy_IgnoresElapsedResetsAndKeepsSoonestFutureReset()
    {
        var now = DateTimeOffset.Parse("2030-01-10T00:00:00Z");
        var snapshot = SimpleApiProvider.ParseLlmProxy(Json("""
        {
          "providers": {
            "openai": {
              "quota_groups": [
                { "remaining_percent": 50, "reset_time": "2030-01-01T00:00:00Z" },
                { "remaining_percent": 40, "reset_time": "2030-01-20T00:00:00Z" },
                { "remaining_percent": 80, "reset_time": "2030-02-01T00:00:00Z" }
              ]
            }
          }
        }
        """), now);

        Assert.AreEqual("2030-01-20T00:00:00.0000000+00:00", snapshot.Primary.ResetsAt);
    }

    [TestMethod]
    public void ParseLlmProxy_WithoutQuotaGroupsUsesInformationalProviderCount()
    {
        var snapshot = SimpleApiProvider.ParseLlmProxy(Json("""
        {
          "providers": {
            "openai": { "total_requests": 1 },
            "anthropic": { "total_requests": 2 }
          }
        }
        """), DateTimeOffset.Parse("2030-01-10T00:00:00Z"));

        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("Providers", snapshot.Primary.Label);
        Assert.AreEqual("2 providers", snapshot.Primary.ValueText);
        Assert.AreEqual(100, Quota.ProviderAvailability(snapshot));
    }

    [TestMethod]
    public void ResolveLlmProxyUrl_AcceptsRootOrVersionedBaseWithoutDuplicatingVersion()
    {
        var root = new FakeConfig(new Dictionary<string, string>
        {
            ["proxy.llmproxy_base_url"] = "https://proxy.example.com",
        });
        var versioned = new FakeConfig(new Dictionary<string, string>
        {
            ["proxy.llmproxy_base_url"] = "https://proxy.example.com/v1/",
        });

        Assert.AreEqual("https://proxy.example.com/v1/quota-stats", SimpleApiProvider.ResolveLlmProxyUrl("proxy", root));
        Assert.AreEqual("https://proxy.example.com/v1/quota-stats", SimpleApiProvider.ResolveLlmProxyUrl("proxy", versioned));
    }

    [TestMethod]
    [DataRow("https://proxy.example.com?token=leak")]
    [DataRow("https://user:password@proxy.example.com")]
    [DataRow("https://proxy.example.com/#fragment")]
    public void ResolveLlmProxyUrl_RejectsAmbiguousCredentialBase(string baseUrl)
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["proxy.llmproxy_base_url"] = baseUrl,
        });

        Assert.ThrowsExactly<ProviderException>(() => SimpleApiProvider.ResolveLlmProxyUrl("proxy", config));
    }

    [TestMethod]
    public void Scoped_IsConfigOnlyAndNeverConsultsTheEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("QUOTALENS_TEST_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("QUOTALENS_TEST_API_KEY", "env-token");

            var blank = new FakeConfig(new Dictionary<string, string>
            {
                ["deepseek-new.deepseek_key"] = "",
            });
            Assert.IsNull(ProviderConfig.Scoped("deepseek-new", blank, "deepseek_key"));

            var set = new FakeConfig(new Dictionary<string, string>
            {
                ["deepseek-new.deepseek_key"] = "from-config",
            });
            Assert.AreEqual("from-config", ProviderConfig.Scoped("deepseek-new", set, "deepseek_key"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUOTALENS_TEST_API_KEY", previous);
        }
    }

    [TestMethod]
    public void Resolve_ConfigThenEnvironmentThenDefault()
    {
        var previous = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        try
        {
            var blank = new FakeConfig(new Dictionary<string, string>
            {
                ["deepseek-new.deepseek_key"] = "",
            });

            // Environment wins when config is empty (process scope shadows user/machine).
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "env-token");
            Assert.AreEqual("env-token", ProviderConfig.Resolve("deepseek-new", blank, "deepseek", "deepseek_key"));

            // Config wins over environment.
            var set = new FakeConfig(new Dictionary<string, string>
            {
                ["deepseek-new.deepseek_key"] = "from-config",
            });
            Assert.AreEqual("from-config", ProviderConfig.Resolve("deepseek-new", set, "deepseek", "deepseek_key"));

            // Default when the field has no env mapping (claude_path is not env-backed).
            Assert.AreEqual("fallback", ProviderConfig.Resolve("deepseek-new", blank, "claude", "claude_path", "fallback"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", previous);
        }
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        string Query,
        string? Authorization,
        string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly object _gate = new();

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
            var authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.Single()
                : null;
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
                Requests.Add(new CapturedRequest(request.Method, uri.AbsolutePath, uri.Query, authorization, body));
            return responseFactory(request);
        }
    }

    private sealed class FakeConfig(IReadOnlyDictionary<string, string> values) : IConfig
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
