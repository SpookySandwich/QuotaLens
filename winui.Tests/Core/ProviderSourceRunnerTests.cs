using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ProviderSourceRunnerTests
{
    [TestMethod]
    public async Task FetchAsync_UsesSelectedSourceWhenItIsAvailable()
    {
        var web = new FakeSource(ProviderSourceMode.Web, available: true, label: "web-data");
        var app = new FakeSource(ProviderSourceMode.App, available: true, label: "app-data");
        var config = new MapConfig(new Dictionary<string, string>
        {
            ["kimi.provider_source"] = "web",
        });

        var snapshot = await ProviderSourceRunner.FetchAsync(
            new UnusedProvider(),
            new IProviderSource[] { app, web },
            "kimi",
            config,
            CancellationToken.None);

        Assert.AreEqual("web-data", snapshot.Name);
        Assert.AreEqual("web", snapshot.SourceState?.RequestedSourceId);
        Assert.AreEqual("web", snapshot.SourceState?.EffectiveSourceId);
        Assert.IsFalse(snapshot.SourceState!.UsedFallback);
    }

    [TestMethod]
    public async Task FetchAsync_SelectedUnavailableSource_DoesNotFallThrough()
    {
        var web = new FakeSource(ProviderSourceMode.Web, available: true, label: "web-data");
        var recovery = new ProviderRecoveryAction(ProviderRecoveryKind.LaunchApp, "source.note");
        var app = new FakeSource(ProviderSourceMode.App, available: false, label: "app-data", recovery: recovery);
        var config = new MapConfig(new Dictionary<string, string>
        {
            ["kimi.provider_source"] = "app",
        });

        var error = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            ProviderSourceRunner.FetchAsync(
                new UnusedProvider(),
                new IProviderSource[] { app, web },
                "kimi",
                config,
                CancellationToken.None));

        Assert.AreEqual(recovery, error.RecoveryAction);
    }

    [TestMethod]
    public async Task FetchAsync_AutomaticModeFallsThroughToFirstAvailableSource()
    {
        var app = new FakeSource(ProviderSourceMode.App, available: false, label: "app-data");
        var web = new FakeSource(ProviderSourceMode.Web, available: true, label: "web-data");

        var snapshot = await ProviderSourceRunner.FetchAsync(
            new UnusedProvider(),
            new IProviderSource[] { app, web },
            "kimi",
            new EmptyConfig(),
            CancellationToken.None);

        Assert.AreEqual("web-data", snapshot.Name);
        Assert.IsNull(snapshot.SourceState?.RequestedSourceId);
        Assert.AreEqual("web", snapshot.SourceState?.EffectiveSourceId);
        Assert.IsTrue(snapshot.SourceState!.UsedFallback);
        Assert.IsNull(snapshot.RecoveryAction);
    }

    [TestMethod]
    public async Task FetchAsync_WhenSelectedSourceFails_DoesNotSwitchToAnotherLogin()
    {
        var app = new FakeSource(
            ProviderSourceMode.App,
            available: true,
            label: "app-data",
            fail: new ProviderException("Not available: HTTP 401"));
        var web = new FakeSource(ProviderSourceMode.Web, available: true, label: "web-data");
        var config = new MapConfig(new Dictionary<string, string>
        {
            ["kimi.provider_source"] = "app",
        });

        var error = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            ProviderSourceRunner.FetchAsync(
                new UnusedProvider(),
                new IProviderSource[] { app, web },
                "kimi",
                config,
                CancellationToken.None));

        StringAssert.Contains(error.Message, "401");
    }

    [TestMethod]
    public async Task FetchAsync_WithoutAvailableSource_CarriesPreferredSourceRecovery()
    {
        var recovery = new ProviderRecoveryAction(ProviderRecoveryKind.LaunchApp, "source.note");
        var app = new FakeSource(ProviderSourceMode.App, available: false, recovery: recovery);

        var error = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            ProviderSourceRunner.FetchAsync(
                new UnusedProvider(),
                new IProviderSource[] { app },
                "kimi",
                new EmptyConfig(),
                CancellationToken.None));

        Assert.AreEqual(recovery, error.RecoveryAction);
    }

    [TestMethod]
    public async Task FetchAsync_AuthenticationFailure_CarriesSourceRecovery()
    {
        var recovery = new ProviderRecoveryAction(ProviderRecoveryKind.LaunchApp, "source.note");
        var app = new FakeSource(
            ProviderSourceMode.App,
            available: true,
            fail: new ProviderException("Login required", ProviderErrorKind.AuthenticationRequired),
            recovery: recovery);

        var error = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            ProviderSourceRunner.FetchAsync(
                new UnusedProvider(),
                new IProviderSource[] { app },
                "kimi",
                new EmptyConfig(),
                CancellationToken.None));

        Assert.AreEqual(recovery, error.RecoveryAction);
        Assert.AreEqual(ProviderErrorKind.AuthenticationRequired, error.Kind);
    }

    [TestMethod]
    public async Task FetchAsync_LegacySelection_ResolvesAndReportsCanonicalMode()
    {
        var app = new FakeSource(
            ProviderSourceMode.App,
            available: true,
            label: "app-data",
            legacyConfigValues: new[] { "ide" });
        var config = new MapConfig(new Dictionary<string, string>
        {
            ["gemini.provider_source"] = "ide",
        });

        var snapshot = await ProviderSourceRunner.FetchAsync(
            new UnusedProvider(),
            new IProviderSource[] { app },
            "gemini",
            config,
            CancellationToken.None);

        Assert.AreEqual("app", snapshot.SourceState?.RequestedSourceId);
        Assert.AreEqual("app", snapshot.SourceState?.EffectiveSourceId);
    }

    [TestMethod]
    public async Task FetchAsync_DuplicateMode_IsRejectedBeforeProviderLogicRuns()
    {
        var sources = new IProviderSource[]
        {
            new FakeSource(ProviderSourceMode.App, available: true),
            new FakeSource(ProviderSourceMode.App, available: true),
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ProviderSourceRunner.FetchAsync(
                new UnusedProvider(),
                sources,
                "gemini",
                new EmptyConfig(),
                CancellationToken.None));
    }

    private sealed class FakeSource : IProviderSource
    {
        private readonly bool _available;
        private readonly string _label;
        private readonly Exception? _fail;
        private readonly ProviderRecoveryAction? _recovery;

        public FakeSource(
            ProviderSourceMode mode,
            bool available,
            string label = "",
            Exception? fail = null,
            ProviderRecoveryAction? recovery = null,
            IReadOnlyList<string>? legacyConfigValues = null)
        {
            Mode = mode;
            _available = available;
            _label = label;
            _fail = fail;
            _recovery = recovery;
            LegacyConfigValues = legacyConfigValues ?? Array.Empty<string>();
        }

        public ProviderSourceMode Mode { get; }
        public IReadOnlyList<string> LegacyConfigValues { get; }
        public ProviderRecoveryAction? UnavailableRecovery => _recovery;
        public bool IsAvailable(string instanceId, IConfig config) => _available;

        public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
        {
            if (_fail is not null)
                throw _fail;
            if (!_available)
                throw new ProviderException("Login required: " + Mode.DisplayName());
            return Task.FromResult(new ProviderSnapshot { ProviderId = "kimi", Name = _label });
        }
    }

    private sealed class UnusedProvider : IProvider
    {
        public string Type => "kimi";
        public string Name => "Kimi";
        public string SourceLabel => "unused";
        public Confidence Confidence => Confidence.Official;

        public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
            throw new AssertFailedException("provider FetchAsync should not run when sources exist");
    }

    private sealed class EmptyConfig : IConfig
    {
        public string Get(string key, string fallback = "") => fallback;
        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;
        public bool HasScoped(string instanceId, string key) => false;
        public bool GetBool(string key, bool fallback = false) => fallback;
    }

    private sealed class MapConfig : IConfig
    {
        private readonly Dictionary<string, string> _values;

        public MapConfig(Dictionary<string, string> values) => _values = values;

        public string Get(string key, string fallback = "") =>
            _values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            Get($"{instanceId}.{key}", fallback);

        public bool HasScoped(string instanceId, string key) =>
            _values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) =>
            _values.TryGetValue(key, out var value) ? value == "true" : fallback;
    }
}
