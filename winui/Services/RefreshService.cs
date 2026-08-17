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
    private readonly CliTokenKeepAliveService _keepAlive;
    private readonly List<ProviderSourceFileWatcher> _sourceWatchers = new();
    private DispatcherQueueTimer? _timer;

    public IConfigService Config { get; }

    private readonly SnapshotStore _store;

    public RefreshService(IConfigService config, DispatcherQueue ui)
        : this(config, ui, new SnapshotStore(DefaultSnapshotDirectory()))
    {
    }

    internal RefreshService(IConfigService config, DispatcherQueue ui, SnapshotStore store)
    {
        Config = config;
        _ui = ui;
        _store = store;
        _keepAlive = new CliTokenKeepAliveService(config);
        foreach (var t in Catalog.Types)
            _byType[t.Id] = ProviderRegistry.Create(t.Id);
        // Seed cards with last-known data so a restart shows a stale snapshot while the
        // first refresh runs, instead of an empty/error state.
        foreach (var inst in Config.Instances)
            _snapshots[inst.Id] = store.Load(inst.Id, inst.Type);
    }

    internal static string DefaultSnapshotDirectory(string? localAppData = null) =>
        System.IO.Path.Combine(
            localAppData
                ?? Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuotaLens", "Snapshots");

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
        _store.Save(id, instance?.Type ?? Catalog.ProviderTypeFromId(id), snap);
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
        AppLog.Info($"refresh: {instanceId} ({inst.Type}) start");
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
                    AppLog.Info(
                        $"refresh: {instanceId} ({inst.Type}) ok " +
                        $"used={snap.Primary.UsedPercent:F1}% balance={snap.Balance?.Total ?? 0:0.##} " +
                        $"source='{snap.SourceLabel}'");
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

                    AppLog.Warn($"refresh: {instanceId} ({inst.Type}) failed: {ex.GetType().Name}: {ex.Message}");
                    Store(instanceId, ErrorSnapshotFor(
                        inst,
                        provider,
                        ex.Message,
                        ex is ProviderException providerError ? providerError.Kind : ProviderErrorKind.Unknown,
                        ex is ProviderException sourceError ? sourceError.RecoveryAction : null));
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
        if (_timer is not null)
            RestartSourceWatchers();
        return inst;
    }

    public void RemoveInstance(string instanceId)
    {
        var instance = Instances.FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));
        Config.RemoveInstance(instanceId);
        _snapshots.TryRemove(instanceId, out _);
        _store.Delete(instanceId);
        if (instance is not null && WebLoginService.IsSupported(instance.Type))
            WebLoginService.Instance?.RemoveInstanceData(instance.Id, instance.Type);
        if (_timer is not null)
            RestartSourceWatchers();
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

        var path = target.ConfigKey is null ? "" : Config.Get(target.ConfigKey);
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
            var launch = ProviderLoginLauncher.TryLaunch(type, instanceId, Config);
            if (launch.Outcome == TerminalLaunchOutcome.Started)
            {
                // The login terminal closes itself once sign-in finishes; await that, then
                // refresh so the card reflects the fresh login state without a manual click.
                _ = RefreshAfterLoginAsync(launch.Process, instanceId);
                return true;
            }

            if (launch.Outcome == TerminalLaunchOutcome.CliMissing)
            {
                // No CLI to sign in with — open the install page so the button always
                // does something visible rather than silently failing.
                ProviderLoginLauncher.TryOpenInstallPage(type);
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

    private async Task RefreshAfterLoginAsync(Process? process, string instanceId)
    {
        if (process is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timed out waiting for the user to finish signing in; refresh anyway.
            }
            catch (Exception)
            {
                // Process handle already gone — refresh regardless.
            }
        }

        await RefreshAsync(instanceId);
    }

    /// <summary>Start the periodic auto-refresh (uses Config.RefreshMs).</summary>
    public void StartAutoRefresh()
    {
        _timer?.Stop();
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30_000, Config.RefreshMs));
        _timer.Tick += (_, _) =>
        {
            // Silent per-provider CLI token keep-alive, checked every tick and run
            // at most once per the provider's interval (see CliTokenKeepAliveCatalog).
            _ = _keepAlive.RunDueAsync();
            _ = RefreshAllAsync();
        };
        _timer.Start();

        // First keep-alive check runs immediately at startup, not after one interval.
        _ = _keepAlive.RunDueAsync();

        RestartSourceWatchers();
    }

    private void RestartSourceWatchers()
    {
        foreach (var watcher in _sourceWatchers)
            watcher.Dispose();
        _sourceWatchers.Clear();

        var watchedSources = Instances
            .Where(instance => _byType.ContainsKey(instance.Type))
            .SelectMany(instance => _byType[instance.Type].Sources.SelectMany(source =>
                source.WatchPaths(instance.Id, Config)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => (Path: Path.GetFullPath(path), InstanceId: instance.Id))))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var group in watchedSources)
        {
            var instanceIds = group.Select(item => item.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var watcher = new ProviderSourceFileWatcher(group.Key, () =>
                _ui.TryEnqueue(() =>
                {
                    foreach (var instanceId in instanceIds)
                        _ = RefreshAsync(instanceId);
                }));
            watcher.Start();
            _sourceWatchers.Add(watcher);
        }
    }

    internal static ProviderSnapshot ErrorSnapshotFor(
        ProviderInstance instance,
        IProvider provider,
        string error,
        ProviderErrorKind errorKind = ProviderErrorKind.Unknown,
        ProviderRecoveryAction? recoveryAction = null)
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
        snapshot.RecoveryAction = recoveryAction;
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
