using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

/// <summary>
/// Covers the proactive CLI token keep-alive layer: scheduling decisions, instance
/// gating, timestamp persistence, and the never-crash contract.
/// </summary>
[TestClass]
public sealed class CliTokenKeepAliveServiceTests
{
    [TestMethod]
    public void Catalog_GrokUsesTheMeasuredSilentRefreshCommand()
    {
        var descriptor = CliTokenKeepAliveCatalog.Descriptors["grok"];

        CollectionAssert.AreEqual(CliRefreshCommands.Grok, descriptor.Arguments.ToArray());
        Assert.AreEqual("grok", descriptor.CliCommand);
        Assert.AreEqual("grok_path", descriptor.CliPathFieldKey);
        Assert.IsTrue(descriptor.Interval > TimeSpan.Zero);
    }

    [TestMethod]
    public void IsDue_WithNoTimestamp_IsTrue()
    {
        Assert.IsTrue(CliTokenKeepAliveService.IsDue(Descriptor(), DateTimeOffset.UtcNow, null));
        Assert.IsTrue(CliTokenKeepAliveService.IsDue(Descriptor(), DateTimeOffset.UtcNow, ""));
    }

    [TestMethod]
    public void IsDue_WithRecentTimestamp_IsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = Descriptor(TimeSpan.FromDays(1));

        Assert.IsFalse(CliTokenKeepAliveService.IsDue(descriptor, now, now.AddHours(-12).ToString("O")));
    }

    [TestMethod]
    public void IsDue_AfterIntervalOrWithGarbageTimestamp_IsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var descriptor = Descriptor(TimeSpan.FromDays(1));

        Assert.IsTrue(CliTokenKeepAliveService.IsDue(descriptor, now, now.AddHours(-25).ToString("O")));
        Assert.IsTrue(CliTokenKeepAliveService.IsDue(descriptor, now, "not-a-timestamp"));
    }

    [TestMethod]
    public async Task RunDueAsync_WhenDue_RunsOnceAndPersistsTheTimestamp()
    {
        var config = new FakeConfigService(instances: [new ProviderInstance("grok-1", "grok", "Grok")]);
        var runs = new List<CliTokenRefresher.Request>();
        var service = new CliTokenKeepAliveService(config, (request, _) =>
        {
            runs.Add(request);
            return Task.FromResult(CliTokenRefresher.RefreshOutcome.Unchanged);
        });

        await service.RunDueAsync();
        await service.RunDueAsync();

        Assert.HasCount(1, runs);
        var run = runs[0];
        Assert.AreEqual("grok", run.Binary);
        CollectionAssert.AreEqual(CliRefreshCommands.Grok, run.Arguments.ToArray());
        Assert.IsTrue(run.UseNeutralWorkingDirectory);
        Assert.IsTrue(run.Timeout > TimeSpan.Zero);

        Assert.IsTrue(config.Values.ContainsKey(CliTokenKeepAliveService.LastRunKeyPrefix + "grok"));
        Assert.IsTrue(config.SaveCount >= 1);
    }

    [TestMethod]
    public async Task RunDueAsync_WithoutAnInstance_SkipsSilently()
    {
        var config = new FakeConfigService(instances: [new ProviderInstance("claude", "claude", "Claude")]);
        var runs = 0;
        var service = new CliTokenKeepAliveService(config, (_, _) =>
        {
            runs++;
            return Task.FromResult(CliTokenRefresher.RefreshOutcome.Unchanged);
        });

        await service.RunDueAsync();

        Assert.AreEqual(0, runs);
    }

    [TestMethod]
    public async Task RunDueAsync_WhenTheRunThrows_NeverCrashesAndStillPersists()
    {
        var config = new FakeConfigService(instances: [new ProviderInstance("grok-1", "grok", "Grok")]);
        var service = new CliTokenKeepAliveService(config, (_, _) =>
            throw new InvalidOperationException("cli exploded"));

        await service.RunDueAsync();

        // The timestamp is persisted before the run, so the broken CLI is retried
        // once per interval rather than on every tick — and the caller never throws.
        Assert.IsTrue(config.Values.ContainsKey(CliTokenKeepAliveService.LastRunKeyPrefix + "grok"));
    }

    [TestMethod]
    public void GrokFingerprint_ReadsAuthFileText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotalens-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "auth.json");
            File.WriteAllText(path, "{\"token\":\"abc\"}");

            Assert.AreEqual("{\"token\":\"abc\"}", GrokProvider.ReadAuthFileFingerprint(directory));
            Assert.IsNull(GrokProvider.ReadAuthFileFingerprint(Path.Combine(directory, "missing")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static CliTokenKeepAliveDescriptor Descriptor(TimeSpan? interval = null) => new(
        "grok",
        "grok",
        ["sessions", "list"],
        interval ?? TimeSpan.FromDays(1),
        TimeSpan.FromSeconds(45));

    private sealed class FakeConfigService(
        IReadOnlyList<ProviderInstance> instances) : IConfigService
    {
        public Dictionary<string, string> Values { get; } = new();
        public int SaveCount { get; private set; }

        public IReadOnlyList<ProviderInstance> Instances { get; } = instances;

        public IReadOnlyDictionary<string, string> All => Values;

        public double RefreshMs => 1800_000;

        public string Get(string key, string fallback = "") =>
            Values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            fallback;

        public bool HasScoped(string instanceId, string key) => false;

        public bool GetBool(string key, bool fallback = false) => fallback;

        public void Set(string key, string value) => Values[key] = value;

        public void SetMany(IReadOnlyDictionary<string, string> values)
        {
            foreach (var (key, value) in values)
                Values[key] = value;
        }

        public void Remove(string key) => Values.Remove(key);

        public Task SaveAsync()
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public int ImportEnvironment(string instanceId) => 0;

        public ProviderInstance AddInstance(string providerType) =>
            throw new NotSupportedException();

        public void RemoveInstance(string id)
        {
        }
    }
}
