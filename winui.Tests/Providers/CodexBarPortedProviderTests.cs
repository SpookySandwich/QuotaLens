using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;
using QuotaLens.Services;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class CodexBarPortedProviderTests
{
    [TestMethod]
    public void GroqParseScalar_SumsPrometheusSeriesValues()
    {
        var scalar = GroqProvider.ParseScalar(Json("""
        {
          "status": "success",
          "data": {
            "result": [
              { "value": [1893456000, "1.25"] },
              { "value": [1893456000, 2.75] }
            ]
          }
        }
        """));

        Assert.AreEqual(4, scalar, 0.001);
    }

    [TestMethod]
    public void DeepgramParseUsage_AggregatesBreakdownRows()
    {
        var usage = DeepgramProvider.ParseUsage(
            Json("""
            {
              "start": "2030-01-01",
              "end": "2030-01-02",
              "results": [
                {
                  "hours": 1.5,
                  "total_hours": 2,
                  "agent_hours": 0.5,
                  "tokens_in": 10,
                  "tokens_out": 20,
                  "tts_characters": 30,
                  "requests": 4
                },
                {
                  "hours": 0.5,
                  "total_hours": 1,
                  "tokens_in": 5,
                  "tokens_out": 15,
                  "requests": 6
                }
              ]
            }
            """),
            new DeepgramProvider.DeepgramProject("project-1", "Main"));

        Assert.AreEqual(2, usage.Hours, 0.001);
        Assert.AreEqual(3, usage.TotalHours, 0.001);
        Assert.AreEqual(50, usage.TokensIn + usage.TokensOut);
        Assert.AreEqual(10, usage.Requests);
    }

    [TestMethod]
    public void DeepgramSnapshot_ForMultipleProjects_LabelsAggregate()
    {
        var usage = DeepgramProvider.Aggregate(new[]
        {
            new DeepgramProvider.DeepgramUsage("one", "One", 1, "2030-01-01", "2030-01-02", 1, 1, 0, 10, 20, 0, 3),
            new DeepgramProvider.DeepgramUsage("two", "Two", 1, "2030-01-02", "2030-01-03", 2, 3, 1, 30, 40, 50, 7),
        });
        var snapshot = DeepgramProvider.Snapshot(usage);

        Assert.AreEqual("Deepgram · 2 projects", snapshot.Name);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("10 requests", snapshot.Primary.ValueText);
        Assert.AreEqual("100 tokens · 50 chars", snapshot.Tertiary!.ValueText);
    }

    [TestMethod]
    public void GroqSnapshot_UsesEnterpriseInformationalMetrics()
    {
        var snapshot = GroqProvider.Snapshot(12.5, 3456, 7.25);

        Assert.AreEqual("Groq Enterprise Prometheus", snapshot.SourceLabel);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("12.5 req/min", snapshot.Primary.ValueText);
        Assert.AreEqual("3456 tok/min", snapshot.Secondary!.ValueText);
        Assert.AreEqual("7.25 cache/min", snapshot.Tertiary!.ValueText);
    }

    [TestMethod]
    public void KiloBatchUri_UsesAuthenticatedTrpcBatchFormat()
    {
        var url = KiloProvider.BatchUri("https://kilo.example/trpc");

        Assert.IsTrue(url.AbsolutePath.Contains("user.getCreditBlocks,kiloPass.getState,user.getAutoTopUpPaymentMethod", StringComparison.Ordinal));
        Assert.IsTrue(url.Query.Contains("batch=1", StringComparison.Ordinal));
        Assert.IsTrue(Uri.UnescapeDataString(url.Query).Contains("\"0\":{\"json\":null}", StringComparison.Ordinal));
    }

    [TestMethod]
    public void KiloParseUsage_MapsCreditsPlanAndAutoTopUp()
    {
        var usage = KiloProvider.ParseUsage(Json("""
        [
          {
            "result": {
              "data": {
                "json": {
                  "blocks": [
                    {
                      "usedCredits": 25,
                      "totalCredits": 100,
                      "remainingCredits": 75
                    }
                  ]
                }
              }
            }
          },
          {
            "result": {
              "data": {
                "json": {
                  "plan": {
                    "name": "Kilo Pass Pro"
                  }
                }
              }
            }
          },
          {
            "result": {
              "data": {
                "json": {
                  "enabled": true,
                  "paymentMethod": "visa"
                }
              }
            }
          }
        ]
        """));
        var snapshot = KiloProvider.Snapshot(usage);

        Assert.AreEqual("kilo", snapshot.ProviderId);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("25/100 credits", snapshot.Primary.ResetDescription);
        Assert.AreEqual("Kilo · Kilo Pass Pro", snapshot.Name);
        Assert.AreEqual("Kilo Pass Pro · Auto top-up: visa", snapshot.Tertiary!.ResetDescription);
    }

    [TestMethod]
    public void KiloParseUsage_MapsKiloPassSubscription()
    {
        var usage = KiloProvider.ParseUsage(Json("""
        [
          {
            "result": {
              "data": {
                "creditBlocks": [
                  {
                    "balance_mUsd": 19000000,
                    "amount_mUsd": 19000000
                  }
                ],
                "totalBalance_mUsd": 19000000,
                "autoTopUpEnabled": false
              }
            }
          },
          {
            "result": {
              "data": {
                "subscription": {
                  "tier": "tier_19",
                  "currentPeriodUsageUsd": 0,
                  "currentPeriodBaseCreditsUsd": 19.0,
                  "currentPeriodBonusCreditsUsd": 9.5,
                  "nextBillingAt": "2026-03-28T04:00:00.000Z"
                }
              }
            }
          },
          {
            "result": {
              "data": {
                "enabled": false,
                "amountCents": 5000,
                "paymentMethod": null
              }
            }
          }
        ]
        """));
        var snapshot = KiloProvider.Snapshot(usage);

        Assert.AreEqual(0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("0/19 credits", snapshot.Primary.ResetDescription);
        Assert.AreEqual("Kilo · Starter", snapshot.Name);
        Assert.AreEqual("$0.00 / $19.00 (+ $9.50 bonus)", snapshot.Secondary!.ResetDescription);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Secondary.ResetsAt));
        Assert.AreEqual("Starter · Auto top-up: off", snapshot.Tertiary!.ResetDescription);
    }

    [TestMethod]
    public void KiloParseUsage_KeepsSparseIndexedObjectRoutingByProcedureIndex()
    {
        var usage = KiloProvider.ParseUsage(Json("""
        {
          "0": {
            "result": {
              "data": {
                "json": {
                  "creditsUsed": 10,
                  "creditsRemaining": 90
                }
              }
            }
          },
          "2": {
            "result": {
              "data": {
                "json": {
                  "planName": "wrong-route",
                  "enabled": true,
                  "method": "visa"
                }
              }
            }
          }
        }
        """));
        var snapshot = KiloProvider.Snapshot(usage);

        Assert.AreEqual(10, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Auto top-up: visa", snapshot.Tertiary!.ResetDescription);
    }

    [TestMethod]
    public void KiloParseUsage_TreatsZeroBalanceWithoutCreditBlocksAsVisibleExhaustedState()
    {
        var usage = KiloProvider.ParseUsage(Json("""
        [
          {
            "result": {
              "data": {
                "creditBlocks": [],
                "totalBalance_mUsd": 0,
                "isFirstPurchase": true,
                "autoTopUpEnabled": false
              }
            }
          },
          {
            "result": {
              "data": {
                "subscription": null
              }
            }
          },
          {
            "result": {
              "data": {
                "enabled": false,
                "amountCents": 5000,
                "paymentMethod": null
              }
            }
          }
        ]
        """));
        var snapshot = KiloProvider.Snapshot(usage);

        Assert.AreEqual(100, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("0/0 credits", snapshot.Primary.ResetDescription);
        Assert.AreEqual("Auto top-up: off", snapshot.Tertiary!.ResetDescription);
    }

    [TestMethod]
    public void KiloParseUsage_DegradesOptionalAutoTopUpTrpcError()
    {
        var usage = KiloProvider.ParseUsage(Json("""
        [
          {
            "result": {
              "data": {
                "json": {
                  "creditsUsed": 10,
                  "creditsRemaining": 90
                }
              }
            }
          },
          {
            "result": {
              "data": {
                "json": {
                  "planName": "Starter"
                }
              }
            }
          },
          {
            "error": {
              "json": {
                "message": "Internal server error",
                "data": {
                  "code": "INTERNAL_SERVER_ERROR"
                }
              }
            }
          }
        ]
        """));
        var snapshot = KiloProvider.Snapshot(usage);

        Assert.AreEqual(10, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Kilo · Starter", snapshot.Name);
        Assert.AreEqual("Starter", snapshot.Tertiary!.ResetDescription);
    }

    [TestMethod]
    public void KiloParseUsage_KeepsRequiredTrpcErrorFatal()
    {
        try
        {
            KiloProvider.ParseUsage(Json("""
            [
              {
                "error": {
                  "json": {
                    "message": "Unauthorized",
                    "data": {
                      "code": "UNAUTHORIZED"
                    }
                  }
                }
              }
            ]
            """));
            Assert.Fail("Expected ProviderException.");
        }
        catch (ProviderException error)
        {
            Assert.IsTrue(error.Message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void ProviderRegistry_CreatePortedCodexBarProviders_ReturnsProviders()
    {
        Assert.IsInstanceOfType(ProviderRegistry.Create("doubao"), typeof(DoubaoProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("azureopenai"), typeof(AzureOpenAIProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("groq"), typeof(GroqProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("deepgram"), typeof(DeepgramProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("grok"), typeof(GrokProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("kilo"), typeof(KiloProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("jetbrains"), typeof(JetBrainsProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("copilot"), typeof(SimpleApiProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("gemini"), typeof(GeminiProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("bedrock"), typeof(BedrockProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("vertexai"), typeof(VertexAIProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("alibaba"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("alibabacloud"), typeof(AlibabaProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("alibabatokenplan"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("cursor"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("augment"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("factory"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("minimax"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("windsurf"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("manus"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("perplexity"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("t3chat"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("commandcode"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("ollama"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("abacus"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("stepfun"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("opencode"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("opencodego"), typeof(WebViewLoginProvider));
        Assert.IsInstanceOfType(ProviderRegistry.Create("mistral"), typeof(WebViewLoginProvider));
    }

    [TestMethod]
    public void Catalog_CoversPinnedCodexBarCompatibilitySubset()
    {
        // QuotaLens's shared subset at ProviderContracts.AuditedUpstreamRevision.
        // The complete 66-provider upstream registry and the intentional 18-provider
        // delta are enforced separately by ProviderUpstreamLockTests.
        var codexBarProviders = new[]
        {
            "abacus",
            "alibaba",
            "alibabatokenplan",
            "amp",
            "antigravity",
            "augment",
            "azureopenai",
            "bedrock",
            "claude",
            "codebuff",
            "codex",
            "commandcode",
            "copilot",
            "crof",
            "cursor",
            "deepgram",
            "deepseek",
            "doubao",
            "elevenlabs",
            "factory",
            "gemini",
            "grok",
            "groq",
            "jetbrains",
            "kilo",
            "kimi",
            "kimik2",
            "kiro",
            "llmproxy",
            "manus",
            "mimo",
            "minimax",
            "mistral",
            "moonshot",
            "ollama",
            "openai",
            "opencode",
            "opencodego",
            "openrouter",
            "perplexity",
            "stepfun",
            "synthetic",
            "t3chat",
            "venice",
            "vertexai",
            "warp",
            "windsurf",
            "zai",
        };

        var cataloged = Catalog.Types.Select(type => type.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = ProviderRegistry.RegisteredTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingCatalog = codexBarProviders.Where(provider => !cataloged.Contains(provider)).ToArray();
        var missingRegistry = codexBarProviders.Where(provider => !registered.Contains(provider)).ToArray();

        Assert.AreEqual("", string.Join(", ", missingCatalog), "Missing CodexBar providers from catalog.");
        Assert.AreEqual("", string.Join(", ", missingRegistry), "Missing CodexBar providers from registry.");
    }

    [TestMethod]
    public void ParseCursor_MapsSummaryAndLegacyRequests()
    {
        var snapshot = WebLoginService.ParseCursor("""
        {
          "usageSummary": {
            "billingCycleEnd": "2030-01-01T00:00:00Z",
            "membershipType": "pro",
            "individualUsage": {
              "plan": {
                "used": 1250,
                "limit": 2000,
                "autoPercentUsed": 25.5,
                "apiPercentUsed": 10,
                "totalPercentUsed": 62.5
              },
              "onDemand": {
                "used": 250,
                "limit": 1000
              }
            }
          },
          "userInfo": {
            "email": "dev@example.com",
            "sub": "user_1"
          },
          "requestUsage": {
            "gpt-4": {
              "numRequestsTotal": 20,
              "maxRequestUsage": 100
            }
          }
        }
        """);

        Assert.AreEqual("cursor", snapshot.ProviderId);
        Assert.AreEqual("Cursor · Pro", snapshot.Name);
        Assert.AreEqual("Requests", snapshot.Primary.Label);
        Assert.AreEqual(20, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("20/100 requests", snapshot.Primary.ResetDescription);
        Assert.AreEqual(25.5, snapshot.Secondary!.UsedPercent, 0.001);
        Assert.AreEqual(10, snapshot.Tertiary!.UsedPercent, 0.001);
        Assert.AreEqual(7.5, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseCursor_UsesPercentFieldsAsPercentScale()
    {
        var snapshot = WebLoginService.ParseCursor("""
        {
          "usageSummary": {
            "membershipType": "hobby",
            "individualUsage": {
              "plan": {
                "used": 1,
                "limit": 100,
                "totalPercentUsed": 0.36
              }
            }
          }
        }
        """);

        Assert.AreEqual(0.36, snapshot.Primary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseAugment_MapsCreditsAndSubscription()
    {
        var snapshot = WebLoginService.ParseAugment("""
        {
          "creditsResponse": {
            "usageUnitsRemaining": 75,
            "usageUnitsConsumedThisBillingCycle": 25,
            "usageUnitsAvailable": 100,
            "usageBalanceStatus": "ok"
          },
          "subscriptionResponse": {
            "planName": "pro",
            "billingPeriodEnd": "2030-01-01T00:00:00Z",
            "email": "dev@example.com"
          }
        }
        """);

        Assert.AreEqual("augment", snapshot.ProviderId);
        Assert.AreEqual("Augment · Pro", snapshot.Name);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("25.0 / 100.0 credits", snapshot.Primary.ResetDescription);
        Assert.AreEqual(75, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseFactory_MapsTokenRateLimitWindows()
    {
        var snapshot = WebLoginService.ParseFactory("""
        {
          "authInfo": {
            "organization": {
              "subscription": {
                "factoryTier": "pro",
                "orbSubscription": {
                  "plan": { "name": "Factory Pro" }
                }
              }
            }
          },
          "billingLimits": {
            "extraUsageBalanceCents": 1234,
            "limits": {
              "standard": {
                "fiveHour": { "usedPercent": 40, "secondsRemaining": 3600 },
                "weekly": { "usedPercent": 20, "windowEnd": "2030-01-07T00:00:00Z" },
                "monthly": { "usedPercent": 10, "windowEnd": "2030-02-01T00:00:00Z" }
              }
            }
          }
        }
        """);

        Assert.AreEqual("factory", snapshot.ProviderId);
        Assert.AreEqual("Factory · Factory Pro", snapshot.Name);
        Assert.AreEqual("5h Window", snapshot.Primary.Label);
        Assert.AreEqual(40, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(20, snapshot.Secondary!.UsedPercent, 0.001);
        Assert.AreEqual(10, snapshot.Tertiary!.UsedPercent, 0.001);
        Assert.AreEqual(12.34, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseFactory_FallsBackToStandardPremiumUsage()
    {
        var snapshot = WebLoginService.ParseFactory("""
        {
          "authInfo": {
            "organization": {
              "subscription": {
                "orbSubscription": {
                  "plan": { "name": "Starter" }
                }
              }
            }
          },
          "usageResponse": {
            "usage": {
              "endDate": 1893456000000,
              "standard": {
                "userTokens": 250,
                "totalAllowance": 1000,
                "usedRatio": 0.25
              },
              "premium": {
                "userTokens": 50,
                "totalAllowance": 100,
                "usedRatio": 0.5
              }
            }
          }
        }
        """);

        Assert.AreEqual("Factory · Starter", snapshot.Name);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("250/1.0K tokens", snapshot.Primary.ResetDescription);
        Assert.AreEqual(50, snapshot.Secondary!.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseMiniMax_MapsModelRemainsIntervalAndWeeklyWindows()
    {
        var snapshot = WebLoginService.ParseMiniMax("""
        {
          "data": {
            "current_subscribe_title": "coding pro",
            "model_remains": [
              {
                "model_name": "MiniMax-M1",
                "current_interval_total_count": 100,
                "current_interval_usage_count": 75,
                "start_time": 1893456000,
                "end_time": 1893474000,
                "current_weekly_total_count": 300,
                "current_weekly_usage_count": 150,
                "weekly_start_time": 1893456000,
                "weekly_end_time": 1894060800
              }
            ]
          }
        }
        """);

        Assert.AreEqual("minimax", snapshot.ProviderId);
        Assert.AreEqual("MiniMax · Coding Pro", snapshot.Name);
        Assert.AreEqual("Text Generation", snapshot.Primary.Label);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("25/100 prompts · 5 hours", snapshot.Primary.ResetDescription);
        Assert.AreEqual(300, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("Text Generation", snapshot.Secondary!.Label);
        Assert.AreEqual(50, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual("150/300 prompts · Weekly", snapshot.Secondary.ResetDescription);
    }

    [TestMethod]
    public void ParseMiniMax_MapsMultiServicePayload()
    {
        var snapshot = WebLoginService.ParseMiniMax("""
        {
          "data": {
            "plan_name": "team plan",
            "services": [
              {
                "service_type": "image-generation",
                "window_type": "Today",
                "time_range": "2029-12-31T00:00:00Z-2030-01-01T00:00:00Z",
                "usage": 10,
                "limit": 50
              },
              {
                "service_type": "text-generation",
                "window_type": "5 hours",
                "time_range": "10:00-15:00(UTC+8)",
                "usage": 20,
                "limit": 100,
                "percent": 20
              }
            ]
          }
        }
        """);

        Assert.AreEqual("minimax", snapshot.ProviderId);
        Assert.AreEqual("MiniMax · Team Plan", snapshot.Name);
        Assert.AreEqual("Text Generation", snapshot.Primary.Label);
        Assert.AreEqual(20, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("20/100 prompts · 5 hours", snapshot.Primary.ResetDescription);
        Assert.AreEqual(300, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("Image Generation", snapshot.Secondary!.Label);
        Assert.AreEqual(20, snapshot.Secondary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseWindsurf_MapsPlanStatusQuotaWindows()
    {
        var snapshot = WebLoginService.ParseWindsurf("""
        {
          "planStatus": {
            "planInfo": { "planName": "pro" },
            "dailyQuotaRemainingPercent": 65,
            "weeklyQuotaRemainingPercent": 40,
            "dailyQuotaResetAtUnix": 1893456000,
            "weeklyQuotaResetAtUnix": 1894060800
          }
        }
        """);

        Assert.AreEqual("windsurf", snapshot.ProviderId);
        Assert.AreEqual("Windsurf · Pro", snapshot.Name);
        Assert.AreEqual("Daily", snapshot.Primary.Label);
        Assert.AreEqual(35, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("65% remaining", snapshot.Primary.ResetDescription);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual(60, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual("40% remaining", snapshot.Secondary.ResetDescription);
    }

    [TestMethod]
    public void ParseWindsurf_FallsBackToCachedMessageUsage()
    {
        var snapshot = WebLoginService.ParseWindsurf("""
        {
          "planName": "teams",
          "usage": {
            "messages": 500,
            "remainingMessages": 125,
            "flowActions": 100,
            "usedFlowActions": 25
          }
        }
        """);

        Assert.AreEqual("Windsurf · Teams", snapshot.Name);
        Assert.AreEqual("Messages", snapshot.Primary.Label);
        Assert.AreEqual(75, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("375 / 500 messages", snapshot.Primary.ResetDescription);
        Assert.AreEqual("Flow actions", snapshot.Secondary!.Label);
        Assert.AreEqual(25, snapshot.Secondary.UsedPercent, 0.001);
    }

    [TestMethod]
    public void GeminiParseQuotaResponse_GroupsLowestRemainingBucketByModel()
    {
        var usage = GeminiProvider.ParseQuotaResponse("""
        {
          "buckets": [
            {
              "modelId": "gemini-2.5-pro",
              "remainingFraction": 0.75,
              "resetTime": "2030-01-01T00:00:00Z"
            },
            {
              "modelId": "gemini-2.5-pro",
              "remainingFraction": 0.40,
              "resetTime": "2030-01-01T01:00:00Z"
            },
            {
              "modelId": "gemini-2.5-flash",
              "remainingFraction": 0.60,
              "resetTime": "2030-01-01T02:00:00Z"
            },
            {
              "modelId": "gemini-2.5-flash-lite",
              "remainingFraction": 0.90,
              "resetTime": "2030-01-01T03:00:00Z"
            }
          ]
        }
        """, "dev@example.com");
        var snapshot = GeminiProvider.Snapshot(usage with { AccountPlan = "Paid" }, DateTimeOffset.Parse("2030-01-01T00:00:00Z"));

        Assert.AreEqual("gemini", snapshot.ProviderId);
        Assert.AreEqual("Gemini · Paid", snapshot.Name);
        Assert.HasCount(1, snapshot.Accounts);
        Assert.AreEqual("dev@example.com", snapshot.Accounts[0].Email);
        Assert.AreEqual("Pro", snapshot.Primary.Label);
        Assert.AreEqual(60, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Flash", snapshot.Secondary!.Label);
        Assert.AreEqual(40, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual("Flash Lite", snapshot.Tertiary!.Label);
        Assert.AreEqual(10, snapshot.Tertiary.UsedPercent, 0.001);
        Assert.AreEqual(3, snapshot.ModelQuotas.Count);
    }

    [TestMethod]
    public void BedrockParseCostExplorer_SumsOnlyBedrockServiceGroups()
    {
        var total = BedrockProvider.ParseTotalBedrockCost("""
        {
          "ResultsByTime": [
            {
              "Groups": [
                {
                  "Keys": ["Amazon Bedrock"],
                  "Metrics": { "UnblendedCost": { "Amount": "12.50", "Unit": "USD" } }
                },
                {
                  "Keys": ["Amazon Elastic Compute Cloud - Compute"],
                  "Metrics": { "UnblendedCost": { "Amount": "99.00", "Unit": "USD" } }
                },
                {
                  "Keys": ["AWS Bedrock"],
                  "Metrics": { "UnblendedCost": { "Amount": "0.75", "Unit": "USD" } }
                }
              ]
            }
          ]
        }
        """);

        Assert.AreEqual(13.25, total, 0.001);
    }

    [TestMethod]
    public void BedrockSnapshot_WithBudgetMapsSpendToMonthlyUtilization()
    {
        var snapshot = BedrockProvider.Snapshot(new BedrockProvider.BedrockUsage(
            25,
            100,
            "us-west-2",
            DateTimeOffset.Parse("2030-01-15T00:00:00Z")));

        Assert.AreEqual("bedrock", snapshot.ProviderId);
        Assert.AreEqual("AWS Bedrock", snapshot.Name);
        Assert.AreEqual("Monthly spend", snapshot.Primary.Label);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("$25.00 spent / $100.00 budget", snapshot.Primary.ResetDescription);
        Assert.AreEqual(75, snapshot.Balance!.Total, 0.001);
        Assert.AreEqual("AWS Cost Explorer · us-west-2", snapshot.SourceLabel);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Primary.ResetsAt));
    }

    [TestMethod]
    public void BedrockSnapshot_WithoutBudgetUsesInformationalSpendMetric()
    {
        var snapshot = BedrockProvider.Snapshot(new BedrockProvider.BedrockUsage(
            25,
            null,
            "us-west-2",
            DateTimeOffset.Parse("2030-01-15T00:00:00Z")));

        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("$25.00 spent", snapshot.Primary.ValueText);
        Assert.IsNull(snapshot.Balance);
    }

    [TestMethod]
    public void BedrockCurrentMonthRange_UsesUtcMonthStartAndTomorrowEnd()
    {
        var range = BedrockProvider.CurrentMonthRange(DateTimeOffset.Parse("2030-02-28T23:30:00Z"));

        Assert.AreEqual("2030-02-01", range.Start);
        Assert.AreEqual("2030-03-01", range.End);
    }

    [TestMethod]
    public void BedrockCostExplorerBody_IncludesServiceGroupingAndPagination()
    {
        var body = BedrockProvider.CostExplorerBody("2030-01-01", "2030-01-02", "page-2");

        Assert.IsTrue(body.Contains("\"UnblendedCost\"", StringComparison.Ordinal));
        Assert.IsTrue(body.Contains("\"SERVICE\"", StringComparison.Ordinal));
        Assert.IsTrue(body.Contains("\"NextPageToken\":\"page-2\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VertexAIParseMonitoringQuota_UsesNewestUsagePointAndMatchingLimitSeries()
    {
        var usage = VertexAIProvider.ParseMonitoringQuota("""
        {
          "timeSeries": [
            {
              "metric": {
                "labels": {
                  "quota_metric": "aiplatform.googleapis.com/generate_content_requests",
                  "limit_name": "GenerateContentRequestsPerMinute"
                }
              },
              "resource": { "labels": { "location": "us-central1" } },
              "points": [
                {
                  "interval": { "endTime": "2030-01-01T00:01:00Z" },
                  "value": { "doubleValue": 25 }
                },
                {
                  "interval": { "endTime": "2030-01-01T00:00:00Z" },
                  "value": { "doubleValue": 40 }
                }
              ]
            }
          ]
        }
        """, """
        {
          "timeSeries": [
            {
              "metric": {
                "labels": {
                  "quota_metric": "aiplatform.googleapis.com/generate_content_requests",
                  "limit_name": "GenerateContentRequestsPerMinute"
                }
              },
              "resource": { "labels": { "location": "us-central1" } },
              "points": [
                { "value": { "int64Value": "100" } }
              ]
            }
          ]
        }
        """);

        Assert.AreEqual(25, usage.RequestsUsedPercent, 0.001);
        Assert.AreEqual("generate_content_requests · us-central1", usage.ResetDescription);
    }

    [TestMethod]
    public void VertexAISnapshot_UsesProjectAndEmailIdentity()
    {
        var snapshot = VertexAIProvider.Snapshot(
            new VertexAIProvider.VertexAIUsage(12.5, null, "requests", null, "proj-1", "dev@example.com"),
            DateTimeOffset.Parse("2030-01-01T00:00:00Z"));

        Assert.AreEqual("vertexai", snapshot.ProviderId);
        Assert.AreEqual("Vertex AI", snapshot.Name);
        Assert.AreEqual("requests", snapshot.Primary.Label);
        Assert.AreEqual(12.5, snapshot.Primary.UsedPercent, 0.001);
        Assert.IsNull(snapshot.Primary.WindowMinutes);
        Assert.HasCount(1, snapshot.Accounts);
        Assert.AreEqual("dev@example.com", snapshot.Accounts[0].Email);
        Assert.AreEqual("proj-1", snapshot.Accounts[0].Plan);
    }

    [TestMethod]
    public void GrokSnapshot_MapsBillingCentsAndBillingCycle()
    {
        var billing = GrokProvider.ParseBilling("""
        {
          "billingCycle": {
            "billingPeriodStart": "2030-01-01T00:00:00.000Z",
            "billingPeriodEnd": "2030-02-01T00:00:00.000Z"
          },
          "monthlyLimit": { "val": 3000 },
          "onDemandCap": { "val": 1000 },
          "on_demand_enabled": true,
          "usage": {
            "includedUsed": { "val": 750 },
            "onDemandUsed": { "val": 250 },
            "totalUsed": { "val": 1000 }
          }
        }
        """);

        var snapshot = GrokProvider.Snapshot(billing, DateTimeOffset.Parse("2030-01-02T00:00:00Z"));

        Assert.AreEqual("grok", snapshot.ProviderId);
        Assert.AreEqual("Grok", snapshot.Name);
        Assert.AreEqual("Monthly included", snapshot.Primary.Label);
        Assert.AreEqual(1000d / 3000d * 100d, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("$10.00 / $30.00 included", snapshot.Primary.ResetDescription);
        Assert.AreEqual("2030-02-01T00:00:00.0000000+00:00", snapshot.Primary.ResetsAt);
        Assert.AreEqual(31L * 24L * 60L, snapshot.Primary.WindowMinutes);
        Assert.AreEqual("On-demand", snapshot.Secondary!.Label);
        Assert.AreEqual(25, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual("$2.50 / $10.00 cap", snapshot.Secondary.ResetDescription);
        Assert.AreEqual(20, snapshot.Balance!.Total, 0.001);
        Assert.AreEqual("grok agent stdio", snapshot.SourceLabel);
    }

    [TestMethod]
    public void GrokSnapshot_WhenNoMonthlyLimit_ShowsUsageWithoutRatio()
    {
        var billing = GrokProvider.ParseBilling("""
        {
          "usage": {
            "includedUsed": { "val": 0 },
            "totalUsed": { "val": 125 }
          }
        }
        """);

        var snapshot = GrokProvider.Snapshot(billing);

        Assert.AreEqual(0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("$1.25 used", snapshot.Primary.ResetDescription);
        Assert.AreEqual(0, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseManus_MapsMonthlyAndRefreshCredits()
    {
        var snapshot = WebLoginService.ParseManus("""
        {
          "data": {
            "totalCredits": 1800,
            "freeCredits": 100,
            "periodicCredits": 700,
            "addonCredits": 200,
            "refreshCredits": 50,
            "maxRefreshCredits": 100,
            "proMonthlyCredits": 1000,
            "eventCredits": 25,
            "nextRefreshTime": "2030-01-01T00:00:00Z",
            "refreshInterval": "daily"
          }
        }
        """);

        Assert.AreEqual("manus", snapshot.ProviderId);
        Assert.AreEqual(30, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Total 1800 · Free 100", snapshot.Primary.ResetDescription);
        Assert.AreEqual(50, snapshot.Secondary!.UsedPercent, 0.001);
        Assert.AreEqual("Daily: 50 / 100", snapshot.Secondary.ResetDescription);
        Assert.AreEqual(1800, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParsePerplexity_UsesWaterfallAttribution()
    {
        var snapshot = WebLoginService.ParsePerplexity("""
        {
          "balance_cents": 7500,
          "renewal_date_ts": 1893456000,
          "current_period_purchased_cents": 2000,
          "total_usage_cents": 1250,
          "credit_grants": [
            { "type": "recurring", "amount_cents": 1000 },
            { "type": "purchased", "amount_cents": 1500 },
            { "type": "promotional", "amount_cents": 500, "expires_at_ts": 1893542400 }
          ]
        }
        """);

        Assert.AreEqual("perplexity", snapshot.ProviderId);
        Assert.AreEqual("Perplexity", snapshot.Name);
        Assert.AreEqual(100, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("1000/1000 credits", snapshot.Primary.ResetDescription);
        Assert.AreEqual(0, snapshot.Secondary!.UsedPercent, 0.001);
        Assert.AreEqual(12.5, snapshot.Tertiary!.UsedPercent, 0.001);
        Assert.AreEqual(75, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseT3Chat_FindsNestedJsonlCustomerData()
    {
        var snapshot = WebLoginService.ParseT3Chat("""
        {"result":{"data":{"json":{"customer":{"subTier":"pro","usageBand":"high","usageFourHourPercentage":42,"usageMonthPercentage":7,"usageFourHourNextResetAt":1893456000000,"subscription":{"productName":"t3-pro","currentPeriodEnd":1896048000000}}}}}}
        """);

        Assert.AreEqual("t3chat", snapshot.ProviderId);
        Assert.AreEqual("T3 Chat · T3 Pro", snapshot.Name);
        Assert.AreEqual(42, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Base - high", snapshot.Primary.ResetDescription);
        Assert.AreEqual(7, snapshot.Secondary!.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseCommandCode_MapsCreditsAndPlanCatalog()
    {
        var snapshot = WebLoginService.ParseCommandCode("""
        {
          "creditsResponse": {
            "credits": {
              "monthlyCredits": 12,
              "purchasedCredits": 3,
              "premiumMonthlyCredits": 0,
              "opensourceMonthlyCredits": 0
            }
          },
          "subscriptionResponse": {
            "success": true,
            "data": {
              "planId": "individual-pro",
              "status": "active",
              "currentPeriodEnd": "2030-01-01T00:00:00Z"
            }
          }
        }
        """);

        Assert.AreEqual("commandcode", snapshot.ProviderId);
        Assert.AreEqual("Command Code · Pro", snapshot.Name);
        Assert.AreEqual(60, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("$18.00 of $30.00 · + $3.00 credits", snapshot.Primary.ResetDescription);
        Assert.AreEqual(15, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseJetBrains_MapsLocalQuotaXml()
    {
        var snapshot = JetBrainsProvider.ParseQuotaXml("""
        <application>
          <component name="AIAssistantQuotaManager2">
            <option name="quotaInfo" value="{&quot;type&quot;:&quot;MONTHLY&quot;,&quot;current&quot;:&quot;25&quot;,&quot;maximum&quot;:&quot;100&quot;,&quot;tariffQuota&quot;:{&quot;available&quot;:&quot;75&quot;},&quot;until&quot;:&quot;2030-01-01T00:00:00Z&quot;}"/>
            <option name="nextRefill" value="{&quot;next&quot;:&quot;2030-01-01T00:00:00Z&quot;,&quot;tariff&quot;:{&quot;amount&quot;:&quot;100&quot;,&quot;duration&quot;:&quot;monthly&quot;}}"/>
          </component>
        </application>
        """, "WebStorm 2025.2");

        Assert.AreEqual("jetbrains", snapshot.ProviderId);
        Assert.AreEqual("JetBrains AI · WebStorm 2025.2", snapshot.Name);
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("25 / 100 credits (75 available)", snapshot.Primary.ResetDescription);
        Assert.AreEqual(75, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseOllama_MapsSessionAndWeeklyUsage()
    {
        var snapshot = WebLoginService.ParseOllama("""
        {
          "planName": "pro",
          "accountEmail": "dev@example.com",
          "sessionUsedPercent": 33.5,
          "weeklyUsedPercent": 70,
          "sessionResetsAt": "2030-01-01T00:00:00Z",
          "weeklyResetsAt": "2030-01-07T00:00:00Z",
          "sessionWindowMinutes": 300
        }
        """);

        Assert.AreEqual("ollama", snapshot.ProviderId);
        Assert.AreEqual("Ollama · Pro", snapshot.Name);
        Assert.AreEqual(33.5, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(300, snapshot.Primary.WindowMinutes);
        Assert.AreEqual(70, snapshot.Secondary!.UsedPercent, 0.001);
    }

    [TestMethod]
    public void ParseAbacus_MapsComputePointsAndBilling()
    {
        var snapshot = WebLoginService.ParseAbacus("""
        {
          "computePoints": {
            "totalComputePoints": 1000,
            "computePointsLeft": 250
          },
          "billingInfo": {
            "currentTier": "enterprise",
            "nextBillingDate": "2030-01-01T00:00:00Z"
          }
        }
        """);

        Assert.AreEqual("abacus", snapshot.ProviderId);
        Assert.AreEqual("Abacus AI · Enterprise", snapshot.Name);
        Assert.AreEqual(75, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("750.0 / 1000.0 credits", snapshot.Primary.ResetDescription);
        Assert.AreEqual(250, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseStepFun_MapsFiveHourAndWeeklyWindows()
    {
        var snapshot = WebLoginService.ParseStepFun("""
        {
          "status": 1,
          "five_hour_usage_left_rate": 0.25,
          "weekly_usage_left_rate": 0.75,
          "five_hour_usage_reset_time": 1893456000,
          "weekly_usage_reset_time": "1894060800",
          "planName": "Step Plan"
        }
        """);

        Assert.AreEqual("stepfun", snapshot.ProviderId);
        Assert.AreEqual("StepFun · Step Plan", snapshot.Name);
        Assert.AreEqual(75, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(25, snapshot.Secondary!.UsedPercent, 0.001);
        Assert.AreEqual(300, snapshot.Primary.WindowMinutes);
    }

    [TestMethod]
    public void ParseOpenCode_MapsRollingAndWeeklyWindows()
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

        Assert.AreEqual("opencode", snapshot.ProviderId);
        Assert.AreEqual("5h Window", snapshot.Primary.Label);
        Assert.AreEqual(40, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("Weekly", snapshot.Secondary!.Label);
        Assert.AreEqual(20, snapshot.Secondary.UsedPercent, 0.001);
        Assert.AreEqual("Renews", snapshot.Tertiary!.Label);
    }

    [TestMethod]
    public void ParseOpenCodeGo_MapsMonthlyAndZenBalance()
    {
        var snapshot = WebLoginService.ParseOpenCodeGo("""
        {
          "billing": {
            "rollingUsage": { "used": 10, "limit": 100, "resetInSec": 1200 },
            "weeklyUsage": { "used": 20, "limit": 100, "resetInSec": 86400 },
            "monthlyUsage": { "used": 30, "limit": 100, "resetInSec": 2592000 }
          },
          "zenBalanceUSD": 12.5
        }
        """);

        Assert.AreEqual("opencodego", snapshot.ProviderId);
        Assert.AreEqual(10, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(20, snapshot.Secondary!.UsedPercent, 0.001);
        Assert.AreEqual(30, snapshot.Tertiary!.UsedPercent, 0.001);
        Assert.AreEqual(12.5, snapshot.Balance!.Total, 0.001);
    }

    [TestMethod]
    public void ParseMimo_WithExpiredFlag_DropsStalePlanAndPhantomQuotaRows()
    {
        var snapshot = WebLoginService.ParseMimo("""
        {
          "usage": {
            "data": {
              "monthUsage": {
                "items": [
                  { "name": "month_total_token", "used": 10, "limit": 100, "percent": 0.1 }
                ]
              },
              "usage": { "items": [] }
            }
          },
          "detail": {
            "data": {
              "planCode": "standard",
              "planName": "Standard",
              "currentPeriodEnd": "2030-01-01T00:00:00Z",
              "expired": true,
              "enableAutoRenew": false
            }
          }
        }
        """, DateTimeOffset.Parse("2029-01-01T00:00:00Z"));

        Assert.AreEqual("MiMo", snapshot.Name);
        Assert.AreEqual(EntitlementStatus.Expired, snapshot.EntitlementStatus);
        Assert.AreEqual("Plan expired", snapshot.Primary.Label);
        Assert.AreEqual(100, snapshot.Primary.UsedPercent, 0.001);
        Assert.IsNull(snapshot.Secondary);
        Assert.IsNull(snapshot.Tertiary);
    }

    [TestMethod]
    public void ParseMimo_WithPastPeriodEnd_TreatsFalseExpiredFlagAsExpired()
    {
        var snapshot = WebLoginService.ParseMimo("""
        {
          "usage": { "data": { "monthUsage": { "items": [] }, "usage": { "items": [] } } },
          "detail": {
            "data": {
              "planName": "Standard",
              "currentPeriodEnd": "2029-12-31T23:59:59Z",
              "expired": false
            }
          }
        }
        """, DateTimeOffset.Parse("2030-01-01T00:00:00Z"));

        Assert.AreEqual(EntitlementStatus.Expired, snapshot.EntitlementStatus);
        Assert.AreEqual("MiMo", snapshot.Name);
    }

    [TestMethod]
    public void ParseMimo_WithActivePlan_OnlyShowsQuotaItemsReturnedByApi()
    {
        var snapshot = WebLoginService.ParseMimo("""
        {
          "usage": {
            "data": {
              "monthUsage": {
                "items": [
                  { "name": "month_total_token", "used": 10, "limit": 100, "percent": 0.1 }
                ]
              },
              "usage": {
                "items": [
                  { "name": "plan_total_token", "used": 20, "limit": 200, "percent": 0.1 }
                ]
              }
            }
          },
          "detail": {
            "data": {
              "planName": "Standard",
              "currentPeriodEnd": "2030-02-01T00:00:00Z",
              "expired": false
            }
          }
        }
        """, DateTimeOffset.Parse("2030-01-01T00:00:00Z"));

        Assert.AreEqual("MiMo · Standard", snapshot.Name);
        Assert.AreEqual(EntitlementStatus.Active, snapshot.EntitlementStatus);
        Assert.AreEqual("Standard", snapshot.Primary.Label);
        Assert.AreEqual("Token Plan", snapshot.Secondary!.Label);
        Assert.IsNull(snapshot.Tertiary);
    }

    [TestMethod]
    public void ParseMimo_WithCompensationGrant_ShowsReturnedCompensationQuota()
    {
        var snapshot = WebLoginService.ParseMimo("""
        {
          "usage": {
            "data": {
              "monthUsage": {
                "items": [
                  { "name": "month_total_token", "used": 10, "limit": 100 }
                ]
              },
              "usage": {
                "items": [
                  { "name": "compensation_total_token", "used": 5, "limit": 50 }
                ]
              }
            }
          },
          "detail": {
            "data": {
              "planName": "Standard",
              "currentPeriodEnd": "2030-02-01T00:00:00Z",
              "expired": false
            }
          }
        }
        """, DateTimeOffset.Parse("2030-01-01T00:00:00Z"));

        Assert.AreEqual("Compensation", snapshot.Secondary!.Label);
        Assert.IsNull(snapshot.Tertiary);
    }

    [TestMethod]
    public void ParseMistral_ComputesSpendAndTokenTotals()
    {
        var snapshot = WebLoginService.ParseMistral("""
        {
          "completion": {
            "models": {
              "mistral-large-latest::mistral-large-2411": {
                "input": [
                  { "billing_metric": "mistral-large-2411", "billing_group": "input", "value_paid": 1000 }
                ],
                "output": [
                  { "billing_metric": "mistral-large-2411", "billing_group": "output", "value_paid": 500 }
                ]
              }
            }
          },
          "currency": "EUR",
          "currency_symbol": "€",
          "end_date": "2030-01-31T23:59:59.999Z",
          "prices": [
            { "billing_metric": "mistral-large-2411", "billing_group": "input", "price": "0.0000017" },
            { "billing_metric": "mistral-large-2411", "billing_group": "output", "price": "0.0000051" }
          ]
        }
        """);

        Assert.AreEqual("mistral", snapshot.ProviderId);
        Assert.AreEqual("Monthly spend", snapshot.Primary.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Primary.Kind);
        Assert.AreEqual("€0.0043 this month", snapshot.Primary.ValueText);
        Assert.AreEqual("1500 tokens · 1 models", snapshot.Secondary!.ValueText);
        Assert.IsNull(snapshot.Balance);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
