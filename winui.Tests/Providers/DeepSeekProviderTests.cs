using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class DeepSeekProviderTests
{
    [TestMethod]
    public void ParseUsageSummary_AggregatesTodayMonthTopModelAndCategories()
    {
        var now = DateTimeOffset.Parse("2030-01-15T12:00:00Z");

        var summary = DeepSeekProvider.ParseUsageSummary(
            Json("""
            {
              "code": 0,
              "data": {
                "biz_code": 0,
                "biz_data": {
                  "total": [
                    {
                      "model": "deepseek-chat",
                      "usage": [
                        { "type": "PROMPT_CACHE_HIT_TOKEN", "amount": "100" },
                        { "type": "PROMPT_CACHE_MISS_TOKEN", "amount": "200" },
                        { "type": "RESPONSE_TOKEN", "amount": "300" },
                        { "type": "REQUEST", "amount": "4" }
                      ]
                    },
                    {
                      "model": "deepseek-reasoner",
                      "usage": [
                        { "type": "PROMPT_CACHE_MISS_TOKEN", "amount": "900" },
                        { "type": "RESPONSE_TOKEN", "amount": "100" }
                      ]
                    }
                  ],
                  "days": [
                    {
                      "date": "2030-01-15",
                      "data": [
                        {
                          "model": "deepseek-chat",
                          "usage": [
                            { "type": "PROMPT_CACHE_HIT_TOKEN", "amount": "10" },
                            { "type": "PROMPT_CACHE_MISS_TOKEN", "amount": "20" },
                            { "type": "RESPONSE_TOKEN", "amount": "30" },
                            { "type": "REQUEST", "amount": "2" }
                          ]
                        }
                      ]
                    },
                    {
                      "date": "2030-01-10",
                      "data": [
                        {
                          "model": "deepseek-chat",
                          "usage": [
                            { "type": "RESPONSE_TOKEN", "amount": "40" },
                            { "type": "REQUEST", "amount": "1" }
                          ]
                        }
                      ]
                    },
                    {
                      "date": "2029-12-31",
                      "data": [
                        {
                          "model": "old",
                          "usage": [
                            { "type": "RESPONSE_TOKEN", "amount": "999" }
                          ]
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """),
            Json("""
            {
              "code": 0,
              "data": {
                "biz_code": 0,
                "biz_data": [
                  {
                    "currency": "USD",
                    "total": [
                      {
                        "model": "deepseek-chat",
                        "usage": [
                          { "type": "PROMPT_CACHE_HIT_TOKEN", "amount": "0.01" },
                          { "type": "PROMPT_CACHE_MISS_TOKEN", "amount": "0.02" },
                          { "type": "RESPONSE_TOKEN", "amount": "0.03" }
                        ]
                      },
                      {
                        "model": "deepseek-reasoner",
                        "usage": [
                          { "type": "PROMPT_CACHE_MISS_TOKEN", "amount": "0.09" },
                          { "type": "RESPONSE_TOKEN", "amount": "0.01" }
                        ]
                      }
                    ],
                    "days": [
                      {
                        "date": "2030-01-15",
                        "data": [
                          {
                            "model": "deepseek-chat",
                            "usage": [
                              { "type": "PROMPT_CACHE_HIT_TOKEN", "amount": "0.001" },
                              { "type": "PROMPT_CACHE_MISS_TOKEN", "amount": "0.002" },
                              { "type": "RESPONSE_TOKEN", "amount": "0.003" }
                            ]
                          }
                        ]
                      },
                      {
                        "date": "2030-01-10",
                        "data": [
                          {
                            "model": "deepseek-chat",
                            "usage": [
                              { "type": "RESPONSE_TOKEN", "amount": "0.004" }
                            ]
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
            }
            """),
            now);

        Assert.AreEqual(60, summary.TodayTokens);
        Assert.AreEqual(100, summary.CurrentMonthTokens);
        Assert.AreEqual(0.006, summary.TodayCost!.Value, 0.0001);
        Assert.AreEqual(0.010, summary.CurrentMonthCost!.Value, 0.0001);
        Assert.AreEqual(2, summary.RequestCount);
        Assert.AreEqual(3, summary.CurrentMonthRequestCount);
        Assert.AreEqual("deepseek-reasoner", summary.TopModel);
        Assert.AreEqual("USD", summary.Currency);
        Assert.AreEqual(100, summary.CategoryBreakdown.Single(c => c.Category == "PROMPT_CACHE_HIT_TOKEN").Tokens);
        Assert.AreEqual(1100, summary.CategoryBreakdown.Single(c => c.Category == "PROMPT_CACHE_MISS_TOKEN").Tokens);
        Assert.AreEqual(400, summary.CategoryBreakdown.Single(c => c.Category == "RESPONSE_TOKEN").Tokens);
    }

    [TestMethod]
    public void ApplyUsageSummary_AddsUsageWindowsWithoutReplacingBalance()
    {
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "deepseek",
            Name = "DeepSeek",
            Primary = new RateWindow
            {
                Label = "Balance",
                UsedPercent = 0,
                DetailText = "$50.00",
            },
            Balance = new BalanceInfo { Currency = "USD", Total = 50, Paid = 40, Granted = 10 },
        };

        DeepSeekProvider.ApplyUsageSummary(
            snapshot,
            new DeepSeekProvider.DeepSeekUsageSummary(
                123,
                456,
                1.23,
                4.56,
                7,
                8,
                "deepseek-chat",
                new[]
                {
                    new DeepSeekProvider.DeepSeekCategoryUsage("RESPONSE_TOKEN", 321, 0.32),
                },
                "USD",
                DateTimeOffset.Parse("2030-01-15T12:00:00Z")));

        Assert.AreEqual("Balance", snapshot.Primary.Label);
        Assert.AreEqual("$50.00", snapshot.Primary.DetailText);
        Assert.AreEqual("Today usage", snapshot.Secondary!.Label);
        Assert.AreEqual(RateWindowKind.Informational, snapshot.Secondary.Kind);
        Assert.AreEqual("123 tokens · 7 requests · $1.23", snapshot.Secondary.ValueText);
        Assert.AreEqual("Month usage", snapshot.Tertiary!.Label);
        Assert.AreEqual("456 tokens · 8 requests · $4.56", snapshot.Tertiary.ValueText);
        Assert.IsTrue(snapshot.AdditionalWindows.Any(window => window.Label == "Top model" && window.ValueText == "deepseek-chat"));
        Assert.IsTrue(snapshot.AdditionalWindows.Any(window => window.Label == "Response tokens" && window.ValueText == "321 tokens · $0.32"));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
