namespace QuotaLens.Core;

/// <summary>
/// The only user-facing data-source modes. Provider implementations supply the
/// mechanics; shared selection, configuration, and UI code only sees these modes.
/// </summary>
public enum ProviderSourceMode
{
    App,
    Cli,
    Web,
}

public static class ProviderSourceModes
{
    public static string ConfigValue(this ProviderSourceMode mode) => mode switch
    {
        ProviderSourceMode.App => "app",
        ProviderSourceMode.Cli => "cli",
        ProviderSourceMode.Web => "web",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static string DisplayName(this ProviderSourceMode mode) => mode switch
    {
        ProviderSourceMode.App => "App",
        ProviderSourceMode.Cli => "CLI",
        ProviderSourceMode.Web => "Web",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static bool MatchesConfigValue(this IProviderSource source, string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (string.Equals(source.Mode.ConfigValue(), value, StringComparison.OrdinalIgnoreCase)
            || source.LegacyConfigValues.Contains(value, StringComparer.OrdinalIgnoreCase));

    public static IProviderSource? Find(
        this IEnumerable<IProviderSource> sources,
        string? configValue) =>
        sources.FirstOrDefault(source => source.MatchesConfigValue(configValue));
}

/// <summary>The one setup action a data source can expose in provider settings.</summary>
public enum ProviderConnectionActionKind
{
    SignIn,
    OpenApp,
}

/// <summary>
/// Normalized connection state supplied to a source-owned action. The dialog does not
/// infer behavior from provider IDs or from App/CLI/Web modes; each action decides how
/// its own availability, authentication, and verification state should be presented.
/// </summary>
public sealed record ProviderConnectionState(
    bool AvailabilityKnown,
    bool IsAvailable,
    bool IsVerified,
    bool AuthenticationRequired);

/// <summary>
/// Source-owned setup behavior. Implementations may open a desktop app, start a CLI
/// sign-in, or show a browser login, while the shared dialog handles placement,
/// progress, error display, and verification uniformly.
/// </summary>
public interface IProviderConnectionAction
{
    ProviderConnectionActionKind Kind { get; }

    /// <summary>I18n key for the helper button.</summary>
    string LabelKey { get; }

    /// <summary>I18n key shown beside shared progress while this action is running.</summary>
    string ProgressLabelKey => "card.connecting";

    /// <summary>Field immediately above the helper button.</summary>
    string PlacementFieldKey { get; }

    /// <summary>Draft fields whose edits invalidate an earlier successful fetch.</summary>
    IReadOnlyList<string> VerificationFieldKeys => new[] { PlacementFieldKey };

    /// <summary>Whether Done stays locked until this source has returned real data.</summary>
    bool RequiresVerifiedData => false;

    /// <summary>Maximum time to wait for a real source fetch after starting setup.</summary>
    TimeSpan VerificationTimeout => TimeSpan.FromSeconds(30);

    /// <summary>Delay between verification fetches while the external flow completes.</summary>
    TimeSpan VerificationRetryDelay => TimeSpan.FromSeconds(1);

    bool ShouldOffer(ProviderConnectionState state);

    /// <summary>
    /// Whether changing a draft field should immediately start this action and verify
    /// the source. This keeps automatic setup source-owned while shared dialogs remain
    /// independent of provider IDs, source modes, and provider-specific config keys.
    /// </summary>
    bool ShouldConnectAfterConfigChange(
        string fieldKey,
        string instanceId,
        IConfig config) => false;

    /// <summary>
    /// Starts the user-requested setup flow. False means the external flow was not
    /// started (for example, the user closed a browser login or an install page opened).
    /// </summary>
    Task<bool> StartAsync(string instanceId, IConfig config, CancellationToken ct);

    /// <summary>
    /// Optional non-interactive preparation run before a normal refresh. Sources use
    /// this for explicitly enabled background startup; the default never changes state.
    /// </summary>
    Task PrepareAsync(string instanceId, IConfig config, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Resolved metadata for the dashboard's everyday launch action.</summary>
public sealed record ProviderLaunchInfo(
    string DisplayName,
    string? IconPath = null);

/// <summary>
/// Source-owned everyday launch behavior. Connection actions remain responsible for
/// setup/sign-in; this action opens the tool represented by the selected data source.
/// </summary>
public interface IProviderLaunchAction
{
    /// <summary>
    /// Returns launch metadata only when the configured target can currently be
    /// resolved. A null result hides the dashboard launch action.
    /// </summary>
    ProviderLaunchInfo? GetInfo(string instanceId, IConfig config);

    /// <summary>Starts the selected source's normal, foreground user experience.</summary>
    void Launch(string instanceId, IConfig config);
}

/// <summary>Evaluates stored/draft source state without provider-specific UI branches.</summary>
public static class ProviderConnectionStates
{
    public static bool CanFinish(
        bool fieldsAreValid,
        IProviderConnectionAction? action,
        ProviderConnectionState state,
        bool connectionInProgress = false) =>
        !connectionInProgress
        && fieldsAreValid
        && (action?.RequiresVerifiedData != true || state.IsVerified);

    public static ProviderConnectionState Evaluate(
        IProviderSource source,
        string instanceId,
        IConfig draftConfig,
        string? persistedSourceId,
        ProviderSnapshot? snapshot,
        ProviderErrorKind? currentErrorKind = null,
        bool verifiedInDialog = false,
        bool singleSource = false)
    {
        var availabilityKnown = true;
        var isAvailable = false;
        try
        {
            isAvailable = source.IsAvailable(instanceId, draftConfig);
        }
        catch
        {
            availabilityKnown = false;
        }

        var snapshotMatches = SnapshotMatches(source, persistedSourceId, snapshot, singleSource);
        var authenticationRequired = currentErrorKind == ProviderErrorKind.AuthenticationRequired
            || snapshotMatches && snapshot?.ErrorKind == ProviderErrorKind.AuthenticationRequired;
        var isVerified = verifiedInDialog
            || snapshotMatches && snapshot is { Error: null };

        return new ProviderConnectionState(
            availabilityKnown,
            isAvailable,
            isVerified,
            authenticationRequired);
    }

    internal static bool SnapshotMatches(
        IProviderSource source,
        string? persistedSourceId,
        ProviderSnapshot? snapshot,
        bool singleSource = false)
    {
        if (snapshot is null)
            return false;

        if (snapshot.SourceState is { } state)
        {
            return source.MatchesConfigValue(state.RequestedSourceId)
                || source.MatchesConfigValue(state.EffectiveSourceId);
        }

        // Legacy/single-source snapshots predate SourceState. They belong to this source
        // when there is no selector, or when the persisted selector identifies it.
        return singleSource && string.IsNullOrWhiteSpace(persistedSourceId)
            || source.MatchesConfigValue(persistedSourceId);
    }
}

/// <summary>
/// A probe-able App, CLI, or Web origin. Providers describe availability and fetch
/// behavior here; shared layers never branch on provider identity.
/// </summary>
public interface IProviderSource
{
    ProviderSourceMode Mode { get; }

    /// <summary>Old stored values accepted until the settings page saves the canonical mode.</summary>
    IReadOnlyList<string> LegacyConfigValues => Array.Empty<string>();

    /// <summary>Config fields shown while this mode is selected.</summary>
    IReadOnlyList<string> ConfigFieldKeys => Array.Empty<string>();

    /// <summary>I18n key for a caveat shown below the source selector.</summary>
    string? AttentionNote => null;

    /// <summary>Recovery offered when this source cannot provide data.</summary>
    ProviderRecoveryAction? UnavailableRecovery => null;

    /// <summary>Optional source-owned setup helper used by every UI surface.</summary>
    IProviderConnectionAction? ConnectionAction => null;

    /// <summary>Optional source-owned everyday launch used by provider cards.</summary>
    IProviderLaunchAction? LaunchAction => null;

    /// <summary>Credential/session files whose changes should trigger a refetch.</summary>
    IReadOnlyList<string> WatchPaths(string instanceId, IConfig config) => Array.Empty<string>();

    bool IsAvailable(string instanceId, IConfig config);

    Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct);
}

/// <summary>
/// Declarative source implementation used by every multi-source provider. This keeps
/// App/CLI/Web metadata and orchestration identical while provider-specific work stays
/// in small availability/fetch delegates.
/// </summary>
public sealed class ProviderSource : IProviderSource
{
    private readonly Func<string, IConfig, bool> _isAvailable;
    private readonly Func<string, IConfig, CancellationToken, Task<ProviderSnapshot>> _fetchAsync;
    private readonly Func<string, IConfig, IReadOnlyList<string>> _watchPaths;

    public ProviderSource(
        ProviderSourceMode mode,
        Func<string, IConfig, bool> isAvailable,
        Func<string, IConfig, CancellationToken, Task<ProviderSnapshot>> fetchAsync,
        IReadOnlyList<string>? configFieldKeys = null,
        IReadOnlyList<string>? legacyConfigValues = null,
        string? attentionNote = null,
        ProviderRecoveryAction? unavailableRecovery = null,
        IProviderConnectionAction? connectionAction = null,
        IProviderLaunchAction? launchAction = null,
        Func<string, IConfig, IReadOnlyList<string>>? watchPaths = null)
    {
        Mode = mode;
        _isAvailable = isAvailable;
        _fetchAsync = fetchAsync;
        ConfigFieldKeys = configFieldKeys ?? Array.Empty<string>();
        LegacyConfigValues = legacyConfigValues ?? Array.Empty<string>();
        AttentionNote = attentionNote;
        UnavailableRecovery = unavailableRecovery;
        ConnectionAction = connectionAction;
        LaunchAction = launchAction;
        _watchPaths = watchPaths ?? ((_, _) => Array.Empty<string>());
    }

    public ProviderSourceMode Mode { get; }
    public IReadOnlyList<string> LegacyConfigValues { get; }
    public IReadOnlyList<string> ConfigFieldKeys { get; }
    public string? AttentionNote { get; }
    public ProviderRecoveryAction? UnavailableRecovery { get; }
    public IProviderConnectionAction? ConnectionAction { get; }
    public IProviderLaunchAction? LaunchAction { get; }

    public IReadOnlyList<string> WatchPaths(string instanceId, IConfig config) =>
        _watchPaths(instanceId, config);

    public bool IsAvailable(string instanceId, IConfig config) =>
        _isAvailable(instanceId, config);

    public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
        _fetchAsync(instanceId, config, ct);
}
