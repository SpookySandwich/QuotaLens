using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class ClaudeProviderTests
{
    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task FetchAsync_WithRejectedOAuth_RequestsReauthenticationWithoutRetry(
        HttpStatusCode statusCode)
    {
        // Arrange
        var tokenReads = 0;
        var usageRequests = 0;
        var provider = new ClaudeProvider(
            () =>
            {
                tokenReads++;
                return OAuth("rejected-token", "max");
            },
            (_, _) =>
            {
                usageRequests++;
                return Task.FromResult(Response(statusCode));
            });

        // Act
        var exception = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            provider.FetchAsync("claude", new ThrowingConfig(), CancellationToken.None));

        // Assert
        Assert.AreEqual(1, tokenReads);
        Assert.AreEqual(1, usageRequests);
        StringAssert.StartsWith(exception.Message, "Login required:");
        StringAssert.Contains(exception.Message, "claude auth login");
    }

    [TestMethod]
    public async Task FetchAsync_WithRateLimit_ThrowsRetryableWithoutRetry()
    {
        // Arrange
        var tokenReads = 0;
        var usageRequests = 0;
        var provider = new ClaudeProvider(
            () =>
            {
                tokenReads++;
                return OAuth("rate-limited-token", "max");
            },
            (_, _) =>
            {
                usageRequests++;
                return Task.FromResult(Response(HttpStatusCode.TooManyRequests));
            });

        // Act
        var exception = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            provider.FetchAsync("claude", new ThrowingConfig(), CancellationToken.None));

        // Assert
        Assert.AreEqual(ProviderErrorKind.RateLimited, exception.Kind);
        Assert.AreEqual(1, tokenReads);
        Assert.AreEqual(1, usageRequests);
        StringAssert.Contains(exception.Message, "HTTP 429");
    }

    [TestMethod]
    public void ProviderSource_ContainsNoPromptBearingCliRefreshPath()
    {
        // The invariant is that refreshing never SENDS A PROMPT, because a prompt spends
        // the very quota being measured (the old 'claude -p ping'). Invoking the CLI is
        // itself fine — `claude mcp list` refreshes the cached token and sends nothing —
        // so this bans prompt-bearing invocation, not process invocation.
        var source = File.ReadAllText(FindRepositoryFile("winui", "Providers", "ClaudeProvider.cs"));

        foreach (var forbidden in new[]
        {
            "claude -p",
            "\"-p\"",
            "--print",
            "--output-format",
            "ping",
        })
        {
            Assert.IsFalse(
                source.Contains(forbidden, StringComparison.Ordinal),
                $"Claude refresh must never send a prompt, so the source must not contain '{forbidden}'.");
        }
    }

    [TestMethod]
    public void RefreshCommand_UsesOnlyNonPromptSubcommands()
    {
        // Guards the specific argv: anything that could carry a prompt (a bare word that
        // is not a known subcommand, or a print flag) would silently start costing quota.
        var allowed = new[] { "mcp", "list", "auth", "status", "doctor", "--version" };

        foreach (var argument in ClaudeProvider.RefreshCommandArgumentsForTesting)
        {
            Assert.IsTrue(
                allowed.Contains(argument, StringComparer.Ordinal),
                $"'{argument}' is not a known non-prompt CLI argument.");
        }
    }

    [TestMethod]
    public void ProviderContract_OAuthResponseSchema_IsUpstreamCompatibility()
    {
        // Arrange
        var contract = ProviderContracts.For("claude");

        // Act
        var source = contract.SourceFor("Anthropic OAuth API");

        // Assert
        Assert.AreEqual(ProviderSourceKind.UndocumentedApi, source.SourceKind);
        Assert.AreEqual(ProviderContractStability.UpstreamCompatibility, source.Stability);
        StringAssert.Contains(source.EvidenceUrl, ProviderContracts.AuditedUpstreamRevision);
    }

    [TestMethod]
    public async Task FetchAsync_WithStandardWindows_MapsFiveHourAndSevenDay()
    {
        // Arrange
        var provider = ProviderFor(UsageResponse(0.30));

        // Act
        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        // Assert
        Assert.AreEqual("Claude Code · Max", snapshot.Name);
        Assert.AreEqual("5h Pool", snapshot.Primary.Label);
        Assert.AreEqual(30.0, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual("7d Pool", snapshot.Secondary!.Label);
        Assert.AreEqual(10.0, snapshot.Secondary.UsedPercent, 0.001);
    }

    [TestMethod]
    [DataRow("max_5x", "Max 5x")]
    [DataRow("max-20x", "Max 20x")]
    [DataRow("team_standard", "Team Standard")]
    [DataRow("TEAM PREMIUM", "Team Premium")]
    public async Task FetchAsync_WithSpecificSubscriptionType_UsesCurrentPlanLabel(
        string subscriptionType,
        string expectedPlan)
    {
        // Arrange
        var provider = new ClaudeProvider(
            () => OAuth("fresh-token", subscriptionType),
            (_, _) => Task.FromResult(UsageResponse(0.30)));

        // Act
        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        // Assert
        Assert.AreEqual($"Claude Code · {expectedPlan}", snapshot.Name);
    }

    [TestMethod]
    public async Task FetchAsync_WithScopedWeeklyLimits_MapsModelsAndDeduplicatesById()
    {
        // Arrange
        var provider = ProviderFor(JsonResponse(
            """
            {
              "five_hour": { "utilization": 25, "resets_at": "2026-08-02T10:00:00Z" },
              "seven_day": { "utilization": 40, "resets_at": "2026-08-07T10:00:00Z" },
              "limits": [
                {
                  "kind": "weekly_scoped",
                  "group": "weekly",
                  "percent": 12.5,
                  "resets_at": "2026-08-08T00:00:00Z",
                  "scope": { "model": { "id": "claude/fable.5:promo", "display_name": "Fable" } }
                },
                {
                  "kind": "weekly_scoped",
                  "group": "weekly",
                  "percent": 99,
                  "resets_at": "2026-08-09T00:00:00Z",
                  "scope": { "model": { "id": "claude/fable.5:promo", "display_name": "Renamed Fable" } }
                },
                {
                  "kind": "weekly_scoped",
                  "group": "weekly",
                  "percent": 64,
                  "resets_at": "2026-08-10T00:00:00Z",
                  "scope": { "model": { "id": "claude/research", "display_name": "Research" } }
                },
                {
                  "kind": "weekly_scoped",
                  "group": "weekly",
                  "percent": 80,
                  "resets_at": "2026-08-10T00:00:00Z",
                  "scope": { "model": { "id": "claude/all_models", "display_name": "All models" } }
                },
                {
                  "kind": "weekly_scoped",
                  "group": "monthly",
                  "percent": 20,
                  "scope": { "model": { "id": "claude/monthly", "display_name": "Monthly" } }
                },
                {
                  "kind": "weekly_scoped",
                  "group": "weekly",
                  "percent": "not-a-number",
                  "scope": { "model": { "id": "claude/broken", "display_name": "Broken" } }
                },
                {
                  "kind": "weekly_scoped",
                  "group": "weekly",
                  "percent": 20,
                  "scope": { "model": { "id": "claude/unnamed", "display_name": " " } }
                }
              ]
            }
            """));

        // Act
        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        // Assert
        Assert.HasCount(2, snapshot.AdditionalWindows);
        Assert.AreEqual("Fable only", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(12.5, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.AreEqual(10080, snapshot.AdditionalWindows[0].WindowMinutes);
        Assert.AreEqual("2026-08-08T00:00:00Z", snapshot.AdditionalWindows[0].ResetsAt);
        Assert.AreEqual("Research only", snapshot.AdditionalWindows[1].Label);
        Assert.AreEqual(64, snapshot.AdditionalWindows[1].UsedPercent, 0.001);
    }

    [TestMethod]
    public async Task FetchAsync_WithLiveFableScopedLimitAndNullModelId_AddsFableWindow()
    {
        // Arrange
        var provider = ProviderFor(JsonResponse(
            """
            {
              "five_hour": { "utilization": 23, "resets_at": "2026-08-04T08:00:00Z" },
              "seven_day": { "utilization": 31, "resets_at": "2026-08-08T08:00:00Z" },
              "limits": [
                {
                  "kind": "weekly_scoped",
                  "group": "weekly",
                  "percent": 17,
                  "resets_at": "2026-08-09T08:00:00Z",
                  "is_active": true,
                  "severity": "normal",
                  "scope": {
                    "surface": "claude_code",
                    "model": { "id": null, "display_name": "Fable" }
                  }
                }
              ]
            }
            """));

        // Act
        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        // Assert
        Assert.HasCount(1, snapshot.AdditionalWindows);
        Assert.AreEqual("Fable only", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(17, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.AreEqual("2026-08-09T08:00:00Z", snapshot.AdditionalWindows[0].ResetsAt);
    }

    [TestMethod]
    public async Task FetchAsync_WithLegacyModelWindows_PreservesSonnetOpusAndDailyRoutines()
    {
        // Arrange
        var provider = ProviderFor(JsonResponse(
            """
            {
              "five_hour": { "utilization": 0.25, "resets_at": "2026-08-02T10:00:00Z" },
              "seven_day": { "utilization": 0.40, "resets_at": "2026-08-07T10:00:00Z" },
              "seven_day_sonnet": { "utilization": 0.42, "resets_at": "2026-08-08T10:00:00Z" },
              "seven_day_opus": { "utilization": 18, "resets_at": "2026-08-09T10:00:00Z" },
              "cowork": { "utilization": 0.07, "resets_at": "2026-08-10T10:00:00Z" }
            }
            """));

        // Act
        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        // Assert
        Assert.AreEqual(25, snapshot.Primary.UsedPercent, 0.001);
        Assert.AreEqual(40, snapshot.Secondary!.UsedPercent, 0.001);
        Assert.HasCount(3, snapshot.AdditionalWindows);
        Assert.AreEqual("Sonnet only", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(42, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
        Assert.AreEqual("Opus only", snapshot.AdditionalWindows[1].Label);
        Assert.AreEqual(18, snapshot.AdditionalWindows[1].UsedPercent, 0.001);
        Assert.AreEqual("Daily Routines", snapshot.AdditionalWindows[2].Label);
        Assert.AreEqual(7, snapshot.AdditionalWindows[2].UsedPercent, 0.001);
    }

    [TestMethod]
    [DataRow("seven_day_routines")]
    [DataRow("seven_day_claude_routines")]
    [DataRow("claude_routines")]
    [DataRow("routines")]
    [DataRow("routine")]
    [DataRow("seven_day_cowork")]
    [DataRow("cowork")]
    public async Task FetchAsync_WithDailyRoutinesAlias_MapsAdditionalWindow(string propertyName)
    {
        // Arrange
        var provider = ProviderFor(JsonResponse(
            $$"""
            {
              "five_hour": { "utilization": 10 },
              "{{propertyName}}": { "utilization": 15, "resets_at": "2026-08-10T10:00:00Z" }
            }
            """));

        // Act
        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        // Assert
        Assert.HasCount(1, snapshot.AdditionalWindows);
        Assert.AreEqual("Daily Routines", snapshot.AdditionalWindows[0].Label);
        Assert.AreEqual(15, snapshot.AdditionalWindows[0].UsedPercent, 0.001);
    }

    [TestMethod]
    public async Task FetchAsync_WithCreditBalance_RendersBalanceAlongsideUsageWindows()
    {
        // Arrange
        var provider = ProviderFor(UsageResponseWithCredits());

        // Act
        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        // Assert
        Assert.AreEqual("Claude Code · Max", snapshot.Name);
        Assert.IsNotNull(snapshot.Balance);
        Assert.AreEqual("USD", snapshot.Balance!.Currency);
        Assert.AreEqual(8.35, snapshot.Balance.Total, 0.001);
        Assert.AreEqual(1.65, snapshot.Balance.Paid, 0.001);
        Assert.AreEqual(10.0, snapshot.Balance.Granted, 0.001);
    }

    [TestMethod]
    public async Task FetchAsync_WithExtraUsageCredits_DoesNotTreatUsageCountersAsBalance()
    {
        // Arrange
        var provider = ProviderFor(UsageResponseWithExtraUsage());

        // Act
        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        // Assert
        Assert.IsNull(snapshot.Balance);
    }

    private static ClaudeProvider ProviderFor(HttpResponseMessage response) => new(
        () => OAuth("fresh-token", "max"),
        (_, _) => Task.FromResult(response));

    [TestMethod]
    public async Task FetchAsync_WithStaleTokenAndLiveSession_RefreshesViaCliAndRetries()
    {
        // The CLI refreshes its cached token lazily, so a rejected token usually means
        // "stale", not "signed out". Asking the CLI to refresh its own file costs no
        // quota (the command sends no prompt) and makes the card live again.
        var tokens = new Queue<string>(["stale-token", "fresh-token"]);
        var usageTokens = new List<string>();
        var refreshes = 0;

        var provider = new ClaudeProvider(
            () => LiveSessionOAuth(tokens.Count > 1 ? tokens.Dequeue() : tokens.Peek()),
            (token, _) =>
            {
                usageTokens.Add(token);
                return Task.FromResult(token == "fresh-token"
                    ? UsageResponse(0.25)
                    : Response(HttpStatusCode.Unauthorized));
            },
            (_, _) => { refreshes++; return Task.FromResult(true); });

        var snapshot = await provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None);

        Assert.AreEqual(1, refreshes);
        CollectionAssert.AreEqual(new[] { "stale-token", "fresh-token" }, usageTokens);
        Assert.AreEqual(25.0, snapshot.Primary.UsedPercent, 0.001);
    }

    [TestMethod]
    public async Task FetchAsync_WhenCliRefreshFails_ReportsStaleRatherThanSignedOut()
    {
        var refreshes = 0;
        var provider = new ClaudeProvider(
            () => LiveSessionOAuth("stale-token"),
            (_, _) => Task.FromResult(Response(HttpStatusCode.Unauthorized)),
            (_, _) => { refreshes++; return Task.FromResult(false); });

        var exception = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None));

        Assert.AreEqual(1, refreshes);
        // A live session must never be told to sign in again.
        StringAssert.StartsWith(exception.Message, "Not available:");
        Assert.IsFalse(exception.Message.Contains("Login required", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FetchAsync_WithoutStoredSession_NeverInvokesTheCli()
    {
        var refreshes = 0;
        var provider = new ClaudeProvider(
            () => OAuth("rejected-token", "max"),
            (_, _) => Task.FromResult(Response(HttpStatusCode.Unauthorized)),
            (_, _) => { refreshes++; return Task.FromResult(true); });

        var exception = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None));

        // Genuinely signed out: no CLI command can help, so do not spawn one.
        Assert.AreEqual(0, refreshes);
        StringAssert.StartsWith(exception.Message, "Login required:");
    }

    [TestMethod]
    public async Task FetchAsync_WhenRefreshYieldsTheSameToken_DoesNotRetryTheApi()
    {
        var usageRequests = 0;
        var provider = new ClaudeProvider(
            () => LiveSessionOAuth("stale-token"),
            (_, _) => { usageRequests++; return Task.FromResult(Response(HttpStatusCode.Unauthorized)); },
            (_, _) => Task.FromResult(true));

        await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None));

        // The CLI reported success but the token did not change; re-sending it would
        // just burn another rejected request.
        Assert.AreEqual(1, usageRequests);
    }

    private static ClaudeProvider.ClaudeOAuth LiveSessionOAuth(string token) => new()
    {
        AccessToken = token,
        SubscriptionType = "max",
        SessionCredential = "stored-session",
    };

    private static ClaudeProvider.ClaudeOAuth OAuth(string token, string subscriptionType) => new()
    {
        AccessToken = token,
        SubscriptionType = subscriptionType,
    };

    private static HttpResponseMessage UsageResponse(double utilization)
    {
        var utilizationJson = utilization.ToString(CultureInfo.InvariantCulture);
        return JsonResponse(
            $$"""
            {
              "five_hour": { "utilization": {{utilizationJson}}, "resets_at": "2026-05-29T00:00:00Z" },
              "seven_day": { "utilization": 0.10, "resets_at": "2026-06-01T00:00:00Z" }
            }
            """);
    }

    private static HttpResponseMessage UsageResponseWithCredits() => JsonResponse(
        """
        {
          "five_hour": { "utilization": 0.25, "resets_at": "2026-05-29T00:00:00Z" },
          "seven_day": { "utilization": 0.10, "resets_at": "2026-06-01T00:00:00Z" },
          "credits": {
            "remaining_credits": 8.35,
            "used_credits": 1.65,
            "total_credits": 10,
            "currency": "USD"
          }
        }
        """);

    private static HttpResponseMessage UsageResponseWithExtraUsage() => JsonResponse(
        """
        {
          "five_hour": { "utilization": 0.38, "resets_at": "2026-05-29T00:00:00Z" },
          "seven_day": { "utilization": 1.0, "resets_at": "2026-06-01T00:00:00Z" },
          "extra_usage": {
            "monthly_limit": 4000,
            "used_credits": 1607
          }
        }
        """);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Response(HttpStatusCode status) => new(status);

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativeSegments)} from the test output directory.");
    }

    [TestMethod]
    public async Task FetchAsync_WhenTokenStaleButSessionStored_DoesNotAskForLogin()
    {
        // Claude Code refreshes its cached token lazily, so .credentials.json is routinely
        // stale while the session is healthy. Telling the user to sign in there is wrong.
        var provider = new ClaudeProvider(
            () => new ClaudeProvider.ClaudeOAuth
            {
                AccessToken = "stale",
                SubscriptionType = "max",
                SessionCredential = "still-signed-in",
            },
            (_, _) => Task.FromResult(Response(HttpStatusCode.Unauthorized)));

        var ex = await Assert.ThrowsExactlyAsync<ProviderException>(
            () => provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None));

        StringAssert.Contains(ex.Message, "stale");
        Assert.IsFalse(
            ex.Message.Contains("Login required", StringComparison.Ordinal),
            "A signed-in session must not surface the sign-in action.");
    }

    [TestMethod]
    public async Task FetchAsync_WhenNoStoredSession_AsksForLogin()
    {
        var provider = new ClaudeProvider(
            () => new ClaudeProvider.ClaudeOAuth { AccessToken = "dead", SubscriptionType = "max" },
            (_, _) => Task.FromResult(Response(HttpStatusCode.Unauthorized)));

        var ex = await Assert.ThrowsExactlyAsync<ProviderException>(
            () => provider.FetchAsync("claude", new EmptyConfig(), CancellationToken.None));

        StringAssert.Contains(ex.Message, "Login required");
        StringAssert.Contains(ex.Message, "claude auth login");
    }

    private sealed class EmptyConfig : IConfig
    {
        public string Get(string key, string fallback = "") => fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;

        public bool HasScoped(string instanceId, string key) => false;

        public bool GetBool(string key, bool fallback = false) => fallback;
    }

    private sealed class ThrowingConfig : IConfig
    {
        public string Get(string key, string fallback = "") =>
            throw new InvalidOperationException("Claude usage refresh must not read CLI configuration.");

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            throw new InvalidOperationException("Claude usage refresh must not read CLI configuration.");

        public bool HasScoped(string instanceId, string key) =>
            throw new InvalidOperationException("Claude usage refresh must not read CLI configuration.");

        public bool GetBool(string key, bool fallback = false) =>
            throw new InvalidOperationException("Claude usage refresh must not read CLI configuration.");
    }
}
