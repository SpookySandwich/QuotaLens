using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Services;

/// <summary>
/// The IProviderService implementation: owns provider instances + their snapshots,
/// runs the refresh scheduler with rate-limit backoff, and raises UI-thread events.
/// </summary>
public sealed class RefreshService : IProviderService
{
    private const int MaxRateLimitRetryAttempts = 5;

    private static readonly TimeSpan MinimumRefreshIndicatorDuration = TimeSpan.FromSeconds(1);

    private readonly DispatcherQueue _ui;
    private readonly Dictionary<string, IProvider> _byType = new();
    private readonly ConcurrentDictionary<string, ProviderSnapshot?> _snapshots = new();
    private readonly ConcurrentDictionary<string, bool> _refreshing = new();
    // Web-login providers share one WebView2 profile → never run them concurrently.
    private static readonly SemaphoreSlim _webviewGate = new(1, 1);
    private DispatcherQueueTimer? _timer;

    public IConfigService Config { get; }

    public RefreshService(IConfigService config, DispatcherQueue ui)
    {
        Config = config;
        _ui = ui;
        foreach (var t in Catalog.Types)
            _byType[t.Id] = ProviderRegistry.Create(t.Id);
        foreach (var inst in Config.Instances)
            _snapshots[inst.Id] = null;
    }

    public IReadOnlyList<ProviderInstance> Instances => Config.Instances;
    public ProviderSnapshot? GetSnapshot(string instanceId) => _snapshots.TryGetValue(instanceId, out var s) ? s : null;
    public bool IsRefreshing(string instanceId) => _refreshing.TryGetValue(instanceId, out var r) && r;

    public event EventHandler<ProviderSnapshot>? SnapshotUpdated;
    public event EventHandler<string>? RefreshingChanged;
    public event EventHandler? InstancesChanged;
    public event EventHandler<(string Id, int SecondsLeft, int Attempt)>? RateLimited;

    private void OnUi(Action a)
    {
        if (_ui.HasThreadAccess) a();
        else _ui.TryEnqueue(() => a());
    }

    private bool TryBeginRefreshing(string id)
    {
        if (!_refreshing.TryAdd(id, true))
            return false;

        OnUi(() => RefreshingChanged?.Invoke(this, id));
        return true;
    }

    private void EndRefreshing(string id)
    {
        _refreshing.TryRemove(id, out _);
        OnUi(() => RefreshingChanged?.Invoke(this, id));
    }

    internal static TimeSpan RemainingRefreshIndicatorDelay(TimeSpan elapsed) =>
        elapsed >= MinimumRefreshIndicatorDuration
            ? TimeSpan.Zero
            : MinimumRefreshIndicatorDuration - elapsed;

    internal static bool IsRetryableRateLimit(Exception error) =>
        error is ProviderException { Kind: ProviderErrorKind.RateLimited };

    internal static bool ShouldKeepExistingSnapshotOnRateLimit(Exception error, ProviderSnapshot? existingSnapshot) =>
        IsRetryableRateLimit(error) && existingSnapshot is { Error: null };

    private void Store(string id, ProviderSnapshot snap)
    {
        var instance = Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        snap.Name = ProviderSnapshotIdentity.ComposeTitle(
            instance?.Type ?? Catalog.ProviderTypeFromId(id),
            instance?.Name ?? Catalog.ProviderName(instance?.Type ?? snap.ProviderId),
            snap);
        snap.ProviderId = id;
        _snapshots[id] = snap;
        OnUi(() => SnapshotUpdated?.Invoke(this, snap));
    }

    public async Task RefreshAllAsync()
    {
        var tasks = Instances.Select(i => RefreshAsync(i.Id)).ToArray();
        await Task.WhenAll(tasks);
    }

    public async Task RefreshAsync(string instanceId)
    {
        var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst is null) return;
        if (!_byType.TryGetValue(inst.Type, out var provider)) return;

        if (UnconfiguredSnapshotFor(inst, provider, Config) is { } unconfigured)
        {
            Store(instanceId, unconfigured);
            return;
        }

        var isWebview = WebLoginService.IsSupported(inst.Type);
        if (!TryBeginRefreshing(instanceId))
            return;

        var refreshStartedAt = Stopwatch.GetTimestamp();
        try
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    ProviderSnapshot snap;
                    if (isWebview)
                    {
                        await _webviewGate.WaitAsync();
                        try { snap = await provider.FetchAsync(instanceId, Config, CancellationToken.None); }
                        finally { _webviewGate.Release(); }
                    }
                    else
                    {
                        snap = await provider.FetchAsync(instanceId, Config, CancellationToken.None);
                    }
                    ProviderSnapshotMetadata.Apply(provider, snap);
                    Store(instanceId, snap);
                    return;
                }
                catch (Exception ex)
                {
                    if (IsRetryableRateLimit(ex) && attempt < MaxRateLimitRetryAttempts)
                    {
                        attempt++;
                        int wait = (int)Math.Pow(2, attempt + 1); // 4,8,16,32,64
                        OnUi(() => RateLimited?.Invoke(this, (instanceId, wait, attempt)));
                        try { await Task.Delay(wait * 1000); } catch { }
                        continue;
                    }

                    if (ShouldKeepExistingSnapshotOnRateLimit(ex, GetSnapshot(instanceId)))
                        return;

                    Store(instanceId, ErrorSnapshotFor(
                        inst,
                        provider,
                        ex.Message,
                        ex is ProviderException providerError ? providerError.Kind : ProviderErrorKind.Unknown));
                    return;
                }
            }
        }
        finally
        {
            var remainingIndicatorDelay = RemainingRefreshIndicatorDelay(Stopwatch.GetElapsedTime(refreshStartedAt));
            if (remainingIndicatorDelay > TimeSpan.Zero)
                await Task.Delay(remainingIndicatorDelay);
            EndRefreshing(instanceId);
        }
    }

    public ProviderInstance AddInstance(string providerType, bool refreshImmediately = true)
    {
        var inst = Config.AddInstance(providerType);
        _snapshots[inst.Id] = null;
        OnUi(() => InstancesChanged?.Invoke(this, EventArgs.Empty));
        if (refreshImmediately)
            _ = RefreshAsync(inst.Id);
        return inst;
    }

    public void RemoveInstance(string instanceId)
    {
        var instance = Instances.FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));
        Config.RemoveInstance(instanceId);
        _snapshots.TryRemove(instanceId, out _);
        if (instance is not null && WebLoginService.IsSupported(instance.Type))
            WebLoginService.Instance?.RemoveInstanceData(instance.Id, instance.Type);
        OnUi(() => InstancesChanged?.Invoke(this, EventArgs.Empty));
    }

    public void LaunchIde(string instanceId)
    {
        var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst is null)
            return;

        var type = inst.Type;
        var target = Catalog.LaunchTargetFor(type, Config);
        if (target == null)
            return;

        var path = target.ConfigKey is null
            ? ""
            : target.ConfigKey == Catalog.DefaultLaunchEditorPathKey
                ? Config.Get(target.ConfigKey)
                : Config.GetScoped(instanceId, target.ConfigKey);
        try { IdeLauncher.LaunchIde(type, target, string.IsNullOrWhiteSpace(path) ? null : path); }
        catch { /* UI swallows launch errors (matches original) */ }
    }

    public async Task<bool> OpenLoginAsync(string instanceId)
    {
        var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
        if (inst is null)
            return false;

        var type = inst.Type;
        if (ProviderLoginLauncher.IsSupported(type))
        {
            // Falls back to the CLI's install page when the binary is missing, so the
            // button always does something the user can see rather than silently failing.
            if (ProviderLoginLauncher.TryStartLoginOrInstall(type, instanceId, Config))
            {
                _ = RefreshAsync(instanceId);
                return true;
            }

            return false;
        }

        if (WebLoginService.Instance is null || !WebLoginService.IsSupported(type))
            return false;

        var captured = await WebLoginService.Instance.OpenLoginAsync(instanceId, type, Config);
        // Pull the freshly captured snapshot, or the latest login-required state,
        // into the card so the UI reflects the outcome immediately.
        if (captured)
        {
            try
            {
                var cached = await WebLoginService.Instance.FetchAsync(
                    instanceId,
                    type,
                    Config,
                    allowHiddenCapture: false);
                Store(instanceId, cached);
                return true;
            }
            catch (ProviderException)
            {
                // Fall through to the normal refresh path so the card shows the
                // latest login-required or stale-cache state.
            }
        }

        await RefreshAsync(instanceId);
        return captured;
    }

    /// <summary>Start the periodic auto-refresh (uses Config.RefreshMs).</summary>
    public void StartAutoRefresh()
    {
        _timer?.Stop();
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30_000, Config.RefreshMs));
        _timer.Tick += (_, _) => _ = RefreshAllAsync();
        _timer.Start();
    }

    internal static ProviderSnapshot ErrorSnapshotFor(
        ProviderInstance instance,
        IProvider provider,
        string error,
        ProviderErrorKind errorKind = ProviderErrorKind.Unknown)
    {
        var name = string.IsNullOrWhiteSpace(instance.Name)
            ? Catalog.ProviderName(instance.Type)
            : instance.Name;
        var snapshot = ProviderSnapshotMetadata.Apply(
            provider,
            ProviderSnapshot.ForError(
                instance.Id,
                Catalog.ProviderName(instance.Type),
                provider.SourceLabel,
                error));
        snapshot.Name = ProviderSnapshotIdentity.ComposeTitle(instance.Type, name, snapshot);
        snapshot.ErrorKind = errorKind;
        return snapshot;
    }

    internal static ProviderSnapshot? UnconfiguredSnapshotFor(
        ProviderInstance instance,
        IProvider provider,
        IConfig config) =>
        Catalog.IsProviderUnconfigured(instance.Id, config)
            ? ErrorSnapshotFor(
                instance,
                provider,
                $"Not configured: {Catalog.ProviderName(instance.Type)} settings are empty. Add credentials in Settings.")
            : null;
}
