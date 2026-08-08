using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class RefreshServiceTests
{
    [TestMethod]
    public void RemainingRefreshIndicatorDelay_KeepsFastRefreshVisibleForOneSecond()
    {
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(750),
            RefreshService.RemainingRefreshIndicatorDelay(TimeSpan.FromMilliseconds(250)));
    }

    [TestMethod]
    public void RemainingRefreshIndicatorDelay_DoesNotDelayLongRefreshes()
    {
        Assert.AreEqual(
            TimeSpan.Zero,
            RefreshService.RemainingRefreshIndicatorDelay(TimeSpan.FromSeconds(1)));

        Assert.AreEqual(
            TimeSpan.Zero,
            RefreshService.RemainingRefreshIndicatorDelay(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public void IsRetryableRateLimit_WithTypedRateLimitError_ReturnsTrue()
    {
        var error = ProviderException.RateLimited("HTTP 429");

        Assert.IsTrue(RefreshService.IsRetryableRateLimit(error));
    }

    [TestMethod]
    public void IsRetryableRateLimit_WithWrappedAuthenticationMessage_ReturnsFalse()
    {
        var error = new ProviderException(
            "Claude usage endpoint was rate limited. CLI said: 401 Invalid authentication credentials");

        Assert.IsFalse(RefreshService.IsRetryableRateLimit(error));
    }

    [TestMethod]
    public void ShouldKeepExistingSnapshotOnRateLimit_WithUsableSnapshot_ReturnsTrue()
    {
        var existing = new ProviderSnapshot
        {
            ProviderId = "claude",
            Name = "Claude Code",
        };

        Assert.IsTrue(RefreshService.ShouldKeepExistingSnapshotOnRateLimit(
            ProviderException.RateLimited("HTTP 429"),
            existing));
    }

    [TestMethod]
    public void ShouldKeepExistingSnapshotOnRateLimit_WithExistingError_ReturnsFalse()
    {
        var existing = ProviderSnapshot.ForError("claude", "Claude Code", "Claude CLI", "old error");

        Assert.IsFalse(RefreshService.ShouldKeepExistingSnapshotOnRateLimit(
            ProviderException.RateLimited("HTTP 429"),
            existing));
    }

    [TestMethod]
    public void ErrorSnapshotFor_UsesInstanceNameAndKeepsInstanceId()
    {
        var instance = new ProviderInstance("work", "qoder", "Work Qoder");
        var provider = new FakeProvider("qoder", "Qoder", "Qoder CLI");

        var snapshot = RefreshService.ErrorSnapshotFor(instance, provider, "No token");

        Assert.AreEqual("work", snapshot.ProviderId);
        Assert.AreEqual("Work Qoder", snapshot.Name);
        Assert.AreEqual("Qoder CLI", snapshot.SourceLabel);
        Assert.AreEqual("No token", snapshot.Error);
    }

    [TestMethod]
    public void ErrorSnapshotFor_WithBlankInstanceName_FallsBackToCatalogProviderName()
    {
        var instance = new ProviderInstance("work", "qoder", "");
        var provider = new FakeProvider("qoder", "Qoder", "Qoder CLI");

        var snapshot = RefreshService.ErrorSnapshotFor(instance, provider, "No token");

        Assert.AreEqual("work", snapshot.ProviderId);
        Assert.AreEqual("Qoder", snapshot.Name);
    }

    [TestMethod]
    public void ErrorSnapshotFor_WithSeparatorInCustomName_DoesNotInventOrDuplicatePlan()
    {
        var instance = new ProviderInstance("work", "claude", "Claude Code · Work");
        var provider = new FakeProvider("claude", "Claude Code", "Anthropic OAuth API");

        var snapshot = RefreshService.ErrorSnapshotFor(instance, provider, "Offline");

        Assert.AreEqual("Claude Code · Work", snapshot.Name);
        Assert.IsNull(snapshot.PlanName);
    }

    [TestMethod]
    public void UnconfiguredSnapshotFor_WithBlankRequiredScopedFields_ReturnsNotConfiguredSnapshot()
    {
        var instance = new ProviderInstance("deepseek-new", "deepseek", "DeepSeek");
        var provider = new FakeProvider("deepseek", "DeepSeek", "DeepSeek API");
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["deepseek-new.deepseek_key"] = "",
        });

        var snapshot = RefreshService.UnconfiguredSnapshotFor(instance, provider, config);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual("deepseek-new", snapshot!.ProviderId);
        Assert.AreEqual("DeepSeek API", snapshot.SourceLabel);
        StringAssert.StartsWith(snapshot.Error, "Not configured:");
    }

    [TestMethod]
    public void UnconfiguredSnapshotFor_WithConfiguredRequiredScopedField_AllowsProviderRefresh()
    {
        var instance = new ProviderInstance("deepseek-new", "deepseek", "DeepSeek");
        var provider = new FakeProvider("deepseek", "DeepSeek", "DeepSeek API");
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["deepseek-new.deepseek_key"] = "sk-test",
        });

        var snapshot = RefreshService.UnconfiguredSnapshotFor(instance, provider, config);

        Assert.IsNull(snapshot);
    }

    private sealed class FakeProvider(
        string type,
        string name,
        string sourceLabel) : IProvider
    {
        public string Type { get; } = type;
        public string Name { get; } = name;
        public string SourceLabel { get; } = sourceLabel;
        public Confidence Confidence => Confidence.Official;

        public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
            Task.FromException<ProviderSnapshot>(new ProviderException("not implemented"));
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
