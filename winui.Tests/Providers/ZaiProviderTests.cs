using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class ZaiProviderTests
{
    private const string SampleBalanceJson = """
        {
          "code": 0,
          "msg": "",
          "data": {
            "server_time": 1786853623,
            "plans": [
              { "user_plan_id": "upl_1", "plan_id": "plan-start", "name": "ZCode Start Plan", "status": "active" }
            ],
            "balances": [
              { "user_plan_id": "upl_1", "plan_id": "plan-start", "entitlement_id": "e1", "show_name": "GLM-5.3",
                "entitlement_priority": 110, "total_units": 100000000, "used_units": 72473764,
                "remaining_units": 27526236, "period": "one_time", "period_start": 1786808758,
                "period_end": 1786928400, "expires_at": 1786928400 },
              { "user_plan_id": "upl_1", "plan_id": "plan-start", "entitlement_id": "e2", "show_name": "GLM-5-Turbo",
                "entitlement_priority": 80, "total_units": 2000000, "used_units": 0,
                "remaining_units": 2000000, "period": "daily", "period_start": 1786809600,
                "period_end": 1786895999, "expires_at": 1786895999 }
            ]
          }
        }
        """;

    [TestMethod]
    public void ParseBalance_OrdersByPriorityAndMapsWindows()
    {
        var snapshot = ZaiProvider.ParseBalance(SampleBalanceJson);

        Assert.AreEqual("z.ai", snapshot.Name);
        Assert.AreEqual("ZCode Start Plan", snapshot.PlanName);
        Assert.AreEqual("ZCode CLI", snapshot.SourceLabel);

        Assert.AreEqual("GLM-5.3", snapshot.Primary.Label);
        Assert.AreEqual(Quota.UtilizationToUsedPercent(72473764.0 / 100000000), snapshot.Primary.UsedPercent);
        Assert.IsNull(snapshot.Primary.WindowMinutes); // one_time grant
        Assert.AreEqual("72.5M / 100M tokens", snapshot.Primary.DetailText);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeSeconds(1786928400).ToString("o"),
            snapshot.Primary.ResetsAt);

        Assert.IsNotNull(snapshot.Secondary);
        Assert.AreEqual("GLM-5-Turbo", snapshot.Secondary!.Label);
        Assert.AreEqual(0.0, snapshot.Secondary.UsedPercent);
        Assert.AreEqual(1439, snapshot.Secondary.WindowMinutes); // daily window
    }

    [TestMethod]
    public void ParseBalance_WithApiErrorCode_ThrowsNotAvailable()
    {
        var ex = Assert.ThrowsExactly<ProviderException>(
            () => ZaiProvider.ParseBalance("""{ "code": 401, "msg": "session expired" }"""));

        StringAssert.Contains(ex.Message, "session expired");
    }

    [TestMethod]
    public async Task FetchAsync_WithLocalSession_ReturnsPlanSnapshot()
    {
        var provider = new ZaiProvider(
            cliIsAvailable: () => true,
            sendBalanceAsync: (_, _) => Task.FromResult(BalanceResponse()),
            readSessionToken: () => "session-jwt",
            apiIsAvailable: () => true,
            apiFetchAsync: (_, _, _) => throw new AssertFailedException("API key must not be used with a local session"));

        var snapshot = await provider.FetchAsync("zai", new EmptyConfig(), CancellationToken.None);

        Assert.AreEqual("ZCode Start Plan", snapshot.PlanName);
        Assert.AreEqual("ZCode CLI", snapshot.SourceLabel);
    }

    [TestMethod]
    public async Task FetchAsync_WithRejectedSession_ThrowsLoginRequired()
    {
        var provider = new ZaiProvider(
            cliIsAvailable: () => true,
            sendBalanceAsync: (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            readSessionToken: () => "session-jwt");

        var ex = await Assert.ThrowsExactlyAsync<ProviderException>(
            () => provider.FetchAsync("zai", new EmptyConfig(), CancellationToken.None));

        StringAssert.Contains(ex.Message, "Login required");
    }

    [TestMethod]
    public async Task FetchAsync_WithoutLocalSession_FallsBackToApiKey()
    {
        var apiCalls = 0;
        var provider = new ZaiProvider(
            cliIsAvailable: () => false,
            sendBalanceAsync: (_, _) => throw new AssertFailedException("CLI must not be used without a session"),
            readSessionToken: () => null,
            apiIsAvailable: () => true,
            apiFetchAsync: (_, _, _) =>
            {
                apiCalls++;
                return Task.FromResult(new ProviderSnapshot
                {
                    ProviderId = "zai",
                    Name = "z.ai",
                    SourceLabel = "z.ai API",
                    Primary = new RateWindow { Label = "Tokens", UsedPercent = 40 },
                });
            });

        var snapshot = await provider.FetchAsync("zai", new EmptyConfig(), CancellationToken.None);

        Assert.AreEqual(1, apiCalls);
        Assert.AreEqual("z.ai API", snapshot.SourceLabel);
    }

    [TestMethod]
    public void Sources_CliFirstWithDeclarativeRecovery()
    {
        var provider = new ZaiProvider(
            cliIsAvailable: () => false,
            sendBalanceAsync: (_, _) => throw new AssertFailedException("unused"),
            readSessionToken: () => null);

        CollectionAssert.AreEqual(
            new[] { "cli", "web" },
            provider.Sources.Select(source => source.Mode.ConfigValue()).ToArray());
        var cli = provider.Sources.Single(source => source.Mode == ProviderSourceMode.Cli);
        Assert.AreEqual("zcode.cliSourceNote", cli.AttentionNote);
        Assert.AreEqual("zcode.cliSourceNote", cli.UnavailableRecovery?.DescriptionKey);
        Assert.IsNull(provider.Sources.Single(source => source.Mode == ProviderSourceMode.Web).UnavailableRecovery);
    }

    private static HttpResponseMessage BalanceResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(SampleBalanceJson, Encoding.UTF8, "application/json"),
    };

    private sealed class EmptyConfig : IConfig
    {
        public string Get(string key, string fallback = "") => fallback;
        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;
        public bool HasScoped(string instanceId, string key) => false;
        public bool GetBool(string key, bool fallback = false) => fallback;
    }
}
