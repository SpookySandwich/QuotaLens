using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.ViewModels;

[TestClass]
public sealed class ProviderAddFlowTests
{
    [TestMethod]
    public async Task AddAsync_OpensConfigurationForEveryAddableProvider()
    {
        foreach (var providerType in Catalog.AddableTypes)
        {
            var service = new FakeProviderService();
            var configured = false;

            var instance = await ProviderAddFlow.AddAsync(
                service,
                providerType,
                _ =>
                {
                    configured = true;
                    return Task.FromResult(true);
                });

            Assert.IsTrue(configured, $"{providerType.Id} must open the configuration page.");
            Assert.IsNotNull(instance, providerType.Id);
            Assert.IsFalse(service.AddRefreshImmediately, providerType.Id);
            Assert.AreEqual(0, service.RemovedIds.Count, providerType.Id);
            CollectionAssert.AreEqual(new[] { instance!.Id }, service.RefreshedIds.ToArray(), providerType.Id);
        }
    }

    [TestMethod]
    public async Task AddAsync_WhenConfigurationIsCancelled_RemovesProvisionalInstance()
    {
        foreach (var id in new[] { "opencode", "cursor", "claude", "qoder", "deepseek", "kimi" })
        {
            var service = new FakeProviderService();

            var instance = await ProviderAddFlow.AddAsync(
                service,
                Catalog.FindType(id)!,
                _ => Task.FromResult(false));

            Assert.IsNull(instance, id);
            CollectionAssert.AreEqual(new[] { $"{id}-new" }, service.RemovedIds.ToArray(), id);
            Assert.AreEqual(0, service.RefreshedIds.Count, id);
        }
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
