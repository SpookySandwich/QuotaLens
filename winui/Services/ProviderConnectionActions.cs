using QuotaLens.Core;

namespace QuotaLens.Services;

/// <summary>Starts an interactive CLI sign-in declared by a CLI-backed source.</summary>
public sealed class CliProviderConnectionAction : IProviderConnectionAction
{
    private readonly string _providerType;

    public CliProviderConnectionAction(string providerType)
    {
        if (!ProviderLoginCatalog.TryGet(providerType, out var descriptor))
            throw new ArgumentException($"Provider '{providerType}' has no verified CLI sign-in descriptor.", nameof(providerType));

        _providerType = providerType;
        PlacementFieldKey = descriptor.CliPathFieldKey;
    }

    public ProviderConnectionActionKind Kind => ProviderConnectionActionKind.SignIn;
    public string LabelKey => "editProvider.signIn";
    public string PlacementFieldKey { get; }

    public bool ShouldOffer(ProviderConnectionState state) =>
        state.AuthenticationRequired
        || state.AvailabilityKnown && !state.IsAvailable;

    public async Task<bool> StartAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        var launch = ProviderLoginLauncher.TryLaunch(_providerType, instanceId, config);
        if (launch.Outcome == TerminalLaunchOutcome.CliMissing)
        {
            ProviderLoginLauncher.TryOpenInstallPage(_providerType);
            return false;
        }

        if (launch.Outcome != TerminalLaunchOutcome.Started)
            throw new ProviderException(
                $"Not configured: Could not start {_providerType} sign-in.",
                ProviderErrorKind.Misconfigured);

        if (launch.Process is not null)
            await launch.Process.WaitForExitAsync(ct).ConfigureAwait(false);

        return true;
    }
}

/// <summary>Opens the visible WebView login owned by a browser-backed source.</summary>
public sealed class WebProviderConnectionAction : IProviderConnectionAction
{
    private readonly string _providerType;

    public WebProviderConnectionAction(string providerType, string placementFieldKey)
    {
        _providerType = providerType;
        PlacementFieldKey = placementFieldKey;
    }

    public ProviderConnectionActionKind Kind => ProviderConnectionActionKind.SignIn;
    public string LabelKey => "editProvider.signIn";
    public string PlacementFieldKey { get; }

    public bool ShouldOffer(ProviderConnectionState state) =>
        state.AuthenticationRequired
        || state.AvailabilityKnown && !state.IsAvailable;

    public async Task<bool> StartAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var service = WebLoginService.Instance
            ?? throw new ProviderException(
                "Not configured: Browser login service is not initialized.",
                ProviderErrorKind.Misconfigured);

        var captured = await service.OpenLoginAsync(instanceId, _providerType, config).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return captured;
    }
}

/// <summary>
/// Opens a desktop app for an App-backed source. Any source may opt into background
/// startup; shared launch and readiness behavior does not branch on provider identity.
/// </summary>
public sealed class AppProviderConnectionAction : IProviderConnectionAction
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private readonly string _providerType;
    private readonly bool _launchInBackground;
    private readonly string? _autoLaunchConfigKey;
    private readonly Func<bool> _isReady;
    private readonly Func<string, IConfig, bool, IAsyncDisposable?> _launch;
    private readonly SemaphoreSlim _startupGate = new(1, 1);

    public AppProviderConnectionAction(
        string providerType,
        string placementFieldKey,
        Func<bool> isReady,
        bool launchInBackground = false,
        string? autoLaunchConfigKey = null,
        IReadOnlyList<string>? verificationFieldKeys = null)
        : this(
            providerType,
            placementFieldKey,
            isReady,
            launchInBackground,
            autoLaunchConfigKey,
            verificationFieldKeys,
            Launch)
    {
    }

    internal AppProviderConnectionAction(
        string providerType,
        string placementFieldKey,
        Func<bool> isReady,
        bool launchInBackground,
        string? autoLaunchConfigKey,
        IReadOnlyList<string>? verificationFieldKeys,
        Func<string, string, IConfig, bool, IAsyncDisposable?> launch)
    {
        _providerType = providerType;
        PlacementFieldKey = placementFieldKey;
        _isReady = isReady;
        _launchInBackground = launchInBackground;
        _autoLaunchConfigKey = autoLaunchConfigKey;
        VerificationFieldKeys = verificationFieldKeys ?? new[] { placementFieldKey };
        // The catalog target is keyed by provider type, while non-global target paths
        // are keyed by instance. Preserve both identities instead of conflating them.
        _launch = (instanceId, config, background) =>
            launch(providerType, instanceId, config, background);
    }

    public ProviderConnectionActionKind Kind => ProviderConnectionActionKind.OpenApp;
    public string LabelKey => "editProvider.openApp";
    public string ProgressLabelKey => "editProvider.startingApp";
    public string PlacementFieldKey { get; }
    public IReadOnlyList<string> VerificationFieldKeys { get; }
    public bool RequiresVerifiedData => true;
    public TimeSpan VerificationTimeout => TimeSpan.FromMinutes(10);

    public bool ShouldOffer(ProviderConnectionState state) => !state.IsVerified;

    public bool ShouldConnectAfterConfigChange(
        string fieldKey,
        string instanceId,
        IConfig config) =>
        !string.IsNullOrWhiteSpace(_autoLaunchConfigKey)
        && string.Equals(fieldKey, _autoLaunchConfigKey, StringComparison.OrdinalIgnoreCase)
        && ScopedBool(config, instanceId, _autoLaunchConfigKey!);

    public async Task<bool> StartAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_launchInBackground)
            await EnsureReadyAsync(instanceId, config, ct).ConfigureAwait(false);
        else
            await DisposeLaunchAsync(_launch(instanceId, config, false)).ConfigureAwait(false);
        return true;
    }

    public async Task PrepareAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_autoLaunchConfigKey)
            || !ScopedBool(config, instanceId, _autoLaunchConfigKey!)
            || _isReady())
        {
            return;
        }

        await EnsureReadyAsync(instanceId, config, ct).ConfigureAwait(false);
    }

    private async Task EnsureReadyAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        await _startupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_isReady())
                return;

            await using var launch = _launch(instanceId, config, true);
            var deadline = DateTimeOffset.UtcNow + StartupTimeout;
            while (!_isReady() && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);

            if (!_isReady())
                throw new ProviderException(
                    $"Not available: {_providerType} app started, but its local data service did not become ready.");
        }
        finally
        {
            _startupGate.Release();
        }
    }

    private static async Task DisposeLaunchAsync(IAsyncDisposable? launch)
    {
        if (launch is not null)
            await launch.DisposeAsync().ConfigureAwait(false);
    }

    private static bool ScopedBool(IConfig config, string instanceId, string key) =>
        config.GetScoped(instanceId, key).Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    private static IAsyncDisposable Launch(
        string providerType,
        string instanceId,
        IConfig config,
        bool background) =>
        new AppProviderLaunchAction(providerType).LaunchSession(instanceId, config, background);
}
