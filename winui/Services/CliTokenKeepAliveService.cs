using System.Globalization;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Services;

/// <summary>
/// One provider's silent keep-alive schedule. The command itself must be a measured,
/// non-prompt-bearing refresh verb (the same argv as <see cref="CliRefreshCommands"/>);
/// this record only describes WHEN to run it.
/// </summary>
public sealed record CliTokenKeepAliveDescriptor(
    string ProviderType,
    string CliCommand,
    IReadOnlyList<string> Arguments,
    TimeSpan Interval,
    TimeSpan Timeout,
    string? CliPathFieldKey = null);

/// <summary>
/// The closed set of proactive keep-alive schedules. Adding a provider here is
/// deliberately heavyweight: the argv must first be proven silent (no prompt, no
/// browser, no quota spend) and entered in <see cref="CliRefreshCommands"/>.
/// </summary>
public static class CliTokenKeepAliveCatalog
{
    public static IReadOnlyDictionary<string, CliTokenKeepAliveDescriptor> Descriptors { get; } =
        new Dictionary<string, CliTokenKeepAliveDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            // grok access tokens live ~37h (measured); the CLI session (refresh token)
            // lasts about a week. 'grok sessions list' silently renews the access token
            // when the CLI runs, so a daily hidden run keeps the cached bearer usable
            // without any user interaction. The ~4s run costs nothing measurable.
            ["grok"] = new(
                "grok",
                "grok",
                CliRefreshCommands.Grok,
                Interval: TimeSpan.FromDays(1),
                Timeout: TimeSpan.FromSeconds(45),
                CliPathFieldKey: "grok_path"),
        };
}

/// <summary>
/// Runs each provider's silent keep-alive command at most once per its interval, so
/// CLI sessions stay fresh while the user never opens a terminal. Persisted in config
/// under <c>token_keepalive_last.&lt;type&gt;</c> so the schedule survives restarts.
///
/// Rules that keep this layer boring:
///  - runs only for providers the user actually has an instance of;
///  - a provider with no configured instance is skipped entirely;
///  - every run is hidden, output is discarded, and nothing is ever surfaced in the UI;
///  - the timestamp is persisted BEFORE the run, so a repeatedly failing command
///    (missing CLI, broken install) is retried once per interval instead of every tick;
///  - failures are logged and swallowed — keep-alive must never break a refresh.
/// </summary>
public sealed class CliTokenKeepAliveService
{
    internal const string LastRunKeyPrefix = "token_keepalive_last.";

    private readonly IConfigService _config;
    private readonly Func<CliTokenRefresher.Request, CancellationToken, Task<CliTokenRefresher.RefreshOutcome>> _runAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CliTokenKeepAliveService(IConfigService config)
        : this(config, runAsync: null)
    {
    }

    internal CliTokenKeepAliveService(
        IConfigService config,
        Func<CliTokenRefresher.Request, CancellationToken, Task<CliTokenRefresher.RefreshOutcome>>? runAsync)
    {
        _config = config;
        _runAsync = runAsync ?? DefaultRunAsync;
    }

    private static async Task<CliTokenRefresher.RefreshOutcome> DefaultRunAsync(
        CliTokenRefresher.Request request,
        CancellationToken ct) =>
        await CliTokenRefresher.RefreshAsync(request, () => null, ct).ConfigureAwait(false);

    /// <summary>Runs every due keep-alive once. Never throws; overlapping calls are skipped.</summary>
    public async Task RunDueAsync(CancellationToken ct = default)
    {
        if (!_gate.Wait(0))
            return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var descriptor in CliTokenKeepAliveCatalog.Descriptors.Values)
            {
                try
                {
                    if (!HasInstance(descriptor.ProviderType))
                        continue;

                    if (!IsDue(descriptor, now, _config.Get(LastRunKeyPrefix + descriptor.ProviderType)))
                        continue;

                    await RunOnceAsync(descriptor, now, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    AppLog.Warn($"keepalive: {descriptor.ProviderType} failed: {e.Message}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A provider is due when it has never run, the stored timestamp is unreadable,
    /// or its interval has elapsed since the last attempt.
    /// </summary>
    internal static bool IsDue(CliTokenKeepAliveDescriptor descriptor, DateTimeOffset now, string? lastRunIso)
    {
        if (string.IsNullOrWhiteSpace(lastRunIso))
            return true;

        return !DateTimeOffset.TryParse(
            lastRunIso,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var lastRun)
            || now - lastRun >= descriptor.Interval;
    }

    private bool HasInstance(string providerType) =>
        _config.Instances.Any(instance =>
            string.Equals(instance.Type, providerType, StringComparison.OrdinalIgnoreCase));

    private async Task RunOnceAsync(CliTokenKeepAliveDescriptor descriptor, DateTimeOffset now, CancellationToken ct)
    {
        // Persist first: a broken CLI then retries once per interval, not every tick.
        _config.Set(LastRunKeyPrefix + descriptor.ProviderType, now.ToString("O", CultureInfo.InvariantCulture));
        try
        {
            await _config.SaveAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort persistence; the in-memory value still gates this session.
        }

        var binary = ResolveBinary(descriptor);
        AppLog.Info($"keepalive: {descriptor.ProviderType} running '{binary} {string.Join(" ", descriptor.Arguments)}'");

        var request = new CliTokenRefresher.Request
        {
            Binary = binary,
            Arguments = descriptor.Arguments,
            Timeout = descriptor.Timeout,
            UseNeutralWorkingDirectory = true,
        };

        var outcome = await _runAsync(request, ct).ConfigureAwait(false);
        AppLog.Info($"keepalive: {descriptor.ProviderType} outcome={outcome}");
    }

    /// <summary>
    /// Resolves the binary the same way the provider does (scoped config, then its
    /// environment keys, then the bare command left for PATH resolution).
    /// </summary>
    private string ResolveBinary(CliTokenKeepAliveDescriptor descriptor)
    {
        if (descriptor.CliPathFieldKey is not null)
        {
            foreach (var instance in _config.Instances)
            {
                if (!string.Equals(instance.Type, descriptor.ProviderType, StringComparison.OrdinalIgnoreCase))
                    continue;

                return ProviderConfig.ResolveCliPath(
                    instance.Id,
                    _config,
                    descriptor.ProviderType,
                    descriptor.CliPathFieldKey,
                    descriptor.CliCommand);
            }
        }

        return descriptor.CliCommand;
    }
}
