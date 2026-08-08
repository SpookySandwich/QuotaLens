using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.ViewModels;

[TestClass]
public sealed class ProviderAddFlowTests
{
    [TestMethod]
    public async Task AddAsync_ForProviderWithoutSettings_AddsAndRefreshesImmediately()
    {
        var service = new FakeProviderService();
        var providerType = new ProviderType("test-no-settings", "Test");

        var instance = await ProviderAddFlow.AddAsync(
            service,
            providerType,
            _ => throw new InvalidOperationException("configure should not run"));

        Assert.IsNotNull(instance);
        Assert.AreEqual("test-no-settings", service.AddedProviderType);
        Assert.IsTrue(service.AddRefreshImmediately);
        Assert.AreEqual(0, service.RemovedIds.Count);
        Assert.AreEqual(0, service.RefreshedIds.Count);
    }

    [TestMethod]
    public async Task AddAsync_ForLocalProviderWithoutSetupProbe_AddsAndRefreshesWithoutOpeningConfiguration()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("claude")!,
            _ => throw new InvalidOperationException("optional settings should not block adding"));

        Assert.IsNotNull(instance);
        Assert.AreEqual("claude", service.AddedProviderType);
        Assert.IsFalse(service.AddRefreshImmediately);
        Assert.AreEqual(0, service.RemovedIds.Count);
        CollectionAssert.AreEqual(new[] { instance!.Id }, service.RefreshedIds.ToArray());
    }

    [TestMethod]
    public async Task AddAsync_ForLocalProviderWithConfiguredTool_AddsAndRefreshesWithoutOpeningConfiguration()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("qoder")!,
            _ => throw new InvalidOperationException("configured local tools should not open edit settings"),
            needsLocalSetup: _ => false);

        Assert.IsNotNull(instance);
        Assert.AreEqual("qoder", service.AddedProviderType);
        Assert.IsFalse(service.AddRefreshImmediately);
        Assert.AreEqual(0, service.RemovedIds.Count);
        CollectionAssert.AreEqual(new[] { instance!.Id }, service.RefreshedIds.ToArray());
    }

    [TestMethod]
    public async Task AddAsync_ForLocalProviderWithMissingTool_OpensConfigurationBeforeKeepingInstance()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("qoder")!,
            added =>
            {
                service.Config.Set($"{added.Id}.qoder_cli_path", @"C:\Tools\qodercli.exe");
                return Task.FromResult(true);
            },
            needsLocalSetup: _ => true);

        Assert.IsNotNull(instance);
        Assert.AreEqual("qoder", service.AddedProviderType);
        Assert.IsFalse(service.AddRefreshImmediately);
        Assert.AreEqual(0, service.RemovedIds.Count);
        Assert.AreEqual(@"C:\Tools\qodercli.exe", service.Config.GetScoped(instance!.Id, "qoder_cli_path"));
        CollectionAssert.AreEqual(new[] { instance.Id }, service.RefreshedIds.ToArray());
    }

    [TestMethod]
    public async Task AddAsync_ForLocalProviderWithMissingTool_WhenConfigurationIsCancelled_RemovesInstance()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("qoder")!,
            _ => Task.FromResult(false),
            needsLocalSetup: _ => true);

        Assert.IsNull(instance);
        Assert.AreEqual("qoder", service.AddedProviderType);
        Assert.IsFalse(service.AddRefreshImmediately);
        CollectionAssert.AreEqual(new[] { "qoder-new" }, service.RemovedIds.ToArray());
        Assert.AreEqual(0, service.RefreshedIds.Count);
    }

    [TestMethod]
    public async Task AddAsync_ForBrowserLoginProvider_OpensLoginBeforeKeepingInstance()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("kimi")!,
            _ => throw new InvalidOperationException("browser login should not open edit settings"),
            added =>
            {
                service.LoginIds.Add(added.Id);
                return Task.FromResult(true);
            });

        Assert.IsNotNull(instance);
        Assert.AreEqual("kimi", service.AddedProviderType);
        Assert.IsFalse(service.AddRefreshImmediately);
        CollectionAssert.AreEqual(new[] { instance!.Id }, service.LoginIds.ToArray());
        Assert.AreEqual(0, service.RemovedIds.Count);
        Assert.AreEqual(0, service.RefreshedIds.Count, "The login service refreshes the card after capture.");
    }

    [TestMethod]
    public async Task AddAsync_ForBrowserLoginProvider_WhenLoginIsCancelled_RemovesProvisionalInstance()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("kimi")!,
            _ => throw new InvalidOperationException("browser login should not open edit settings"),
            _ => Task.FromResult(false));

        Assert.IsNull(instance);
        Assert.AreEqual("kimi", service.AddedProviderType);
        Assert.IsFalse(service.AddRefreshImmediately);
        CollectionAssert.AreEqual(new[] { "kimi-new" }, service.RemovedIds.ToArray());
        Assert.AreEqual(0, service.RefreshedIds.Count);
    }

    [TestMethod]
    public void RequiresUserConfiguration_UsesRequiredFieldRulesInsteadOfAnyEditableField()
    {
        Assert.IsTrue(ProviderAddFlow.RequiresUserConfiguration("deepseek"));
        Assert.IsFalse(ProviderAddFlow.RequiresUserConfiguration("qoder"));
        Assert.IsFalse(ProviderAddFlow.RequiresUserConfiguration("kimi"));
    }

    [TestMethod]
    public void RequiresSetup_IncludesApiKeyAndBrowserLoginProviders()
    {
        Assert.IsTrue(ProviderAddFlow.RequiresSetup("deepseek"));
        Assert.IsTrue(ProviderAddFlow.RequiresSetup("kimi"));
        Assert.IsFalse(ProviderAddFlow.RequiresSetup("qoder"));
    }

    [TestMethod]
    public async Task AddAsync_ForConfiguredProvider_WhenDialogIsSaved_KeepsAndRefreshesAfterSave()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("deepseek")!,
            _ =>
            {
                service.Config.Set("save_completed", "true");
                return Task.FromResult(true);
            });

        Assert.IsNotNull(instance);
        Assert.AreEqual("deepseek", service.AddedProviderType);
        Assert.IsFalse(service.AddRefreshImmediately);
        Assert.AreEqual(0, service.RemovedIds.Count);
        CollectionAssert.AreEqual(new[] { instance!.Id }, service.RefreshedIds.ToArray());
        Assert.AreEqual("true", service.Config.Get("save_completed"));
    }

    [TestMethod]
    public async Task AddAsync_ForConfiguredProvider_WhenDialogIsCancelled_RemovesProvisionalInstance()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("deepseek")!,
            _ => Task.FromResult(false));

        Assert.IsNull(instance);
        Assert.AreEqual("deepseek", service.AddedProviderType);
        Assert.IsFalse(service.AddRefreshImmediately);
        CollectionAssert.AreEqual(new[] { "deepseek-new" }, service.RemovedIds.ToArray());
        Assert.AreEqual(0, service.RefreshedIds.Count);
    }

    [TestMethod]
    public async Task AddAsync_ForConfiguredProvider_WhenDialogFails_RemovesProvisionalInstance()
    {
        var service = new FakeProviderService();

        var instance = await ProviderAddFlow.AddAsync(
            service,
            Catalog.FindType("deepseek")!,
            _ => throw new InvalidOperationException("dialog failed"));

        Assert.IsNull(instance);
        CollectionAssert.AreEqual(new[] { "deepseek-new" }, service.RemovedIds.ToArray());
        Assert.AreEqual(0, service.RefreshedIds.Count);
    }

    private sealed class FakeProviderService : IProviderService
    {
        public FakeConfig ConfigImpl { get; } = new();
        public string AddedProviderType { get; private set; } = "";
        public bool AddRefreshImmediately { get; private set; }
        public List<string> RefreshedIds { get; } = new();
        public List<string> RemovedIds { get; } = new();
        public List<string> LoginIds { get; } = new();
        public List<ProviderInstance> InstanceStore { get; } = new();

        public IConfigService Config => ConfigImpl;
        public IReadOnlyList<ProviderInstance> Instances => InstanceStore;

        public event EventHandler<ProviderSnapshot>? SnapshotUpdated { add { } remove { } }
        public event EventHandler<string>? RefreshingChanged { add { } remove { } }
        public event EventHandler? InstancesChanged { add { } remove { } }
        public event EventHandler<(string Id, int SecondsLeft, int Attempt)>? RateLimited { add { } remove { } }

        public ProviderSnapshot? GetSnapshot(string instanceId) => null;
        public bool IsRefreshing(string instanceId) => false;
        public Task RefreshAllAsync() => Task.CompletedTask;

        public Task RefreshAsync(string instanceId)
        {
            RefreshedIds.Add(instanceId);
            return Task.CompletedTask;
        }

        public ProviderInstance AddInstance(string providerType, bool refreshImmediately = true)
        {
            AddedProviderType = providerType;
            AddRefreshImmediately = refreshImmediately;
            var instance = new ProviderInstance($"{providerType}-new", providerType, Catalog.ProviderName(providerType));
            InstanceStore.Add(instance);
            return instance;
        }

        public void RemoveInstance(string instanceId)
        {
            RemovedIds.Add(instanceId);
            InstanceStore.RemoveAll(instance => instance.Id == instanceId);
        }

        public void LaunchIde(string instanceId)
        {
        }

        public Task<bool> OpenLoginAsync(string instanceId)
        {
            LoginIds.Add(instanceId);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeConfig : IConfigService
    {
        private readonly Dictionary<string, string> _values = new();

        public IReadOnlyDictionary<string, string> All => _values;
        public IReadOnlyList<ProviderInstance> Instances { get; } = Array.Empty<ProviderInstance>();
        public double RefreshMs => 1_800_000;

        public string Get(string key, string fallback = "") =>
            _values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            _values.TryGetValue($"{instanceId}.{key}", out var value) ? value : fallback;

        public bool HasScoped(string instanceId, string key) =>
            _values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) =>
            _values.TryGetValue(key, out var value) ? value == "true" : fallback;

        public void Set(string key, string value) => _values[key] = value;
        public void SetMany(IReadOnlyDictionary<string, string> values)
        {
            foreach (var (key, value) in values)
                _values[key] = value;
        }

        public void Remove(string key) => _values.Remove(key);
        public Task SaveAsync() => Task.CompletedTask;
        public ProviderInstance AddInstance(string providerType) => new(providerType, providerType, providerType);
        public void RemoveInstance(string id)
        {
        }
    }
}
