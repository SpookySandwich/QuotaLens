using System.Diagnostics;
using QuotaLens.Core;

namespace QuotaLens.Services;

/// <summary>Opens a provider-declared desktop application in the foreground.</summary>
public sealed class AppProviderLaunchAction : IProviderLaunchAction
{
    private readonly string _providerType;
    private readonly bool _allowDefaultEditorFallback;

    public AppProviderLaunchAction(string providerType)
        : this(providerType, allowDefaultEditorFallback: false)
    {
    }

    internal AppProviderLaunchAction(string providerType, bool allowDefaultEditorFallback)
    {
        _providerType = providerType;
        _allowDefaultEditorFallback = allowDefaultEditorFallback;
    }

    public ProviderLaunchInfo? GetInfo(string instanceId, IConfig config)
    {
        var target = Target(config);
        if (target is null)
            return null;

        var configuredPath = IdeLauncher.ConfiguredPath(
            _providerType,
            instanceId,
            target,
            config);
        if (!IdeLauncher.TryResolveLaunchPath(
                _providerType,
                target,
                configuredPath,
                out var executablePath))
        {
            return null;
        }

        return new ProviderLaunchInfo(
            target.DisplayNameFor(executablePath),
            LaunchIconService.GetOrCreateIconPath(_providerType, target, configuredPath));
    }

    public void Launch(string instanceId, IConfig config)
    {
        var target = RequiredTarget(config);
        IdeLauncher.LaunchIde(
            _providerType,
            instanceId,
            target,
            config,
            background: false);
    }

    internal DesktopAppLaunchSession LaunchSession(
        string instanceId,
        IConfig config,
        bool background)
    {
        var target = RequiredTarget(config);
        return IdeLauncher.LaunchIdeSession(
            _providerType,
            instanceId,
            target,
            config,
            background);
    }

    private ProviderLaunchTarget RequiredTarget(IConfig config) =>
        Target(config)
        ?? throw new ProviderException(
            $"Not configured: {Catalog.ProviderName(_providerType)} has no app launch target.",
            ProviderErrorKind.Misconfigured);

    private ProviderLaunchTarget? Target(IConfig config)
    {
        if (Catalog.LaunchTargets.TryGetValue(_providerType, out var declared))
            return declared;

        return _allowDefaultEditorFallback
            ? Catalog.LaunchTargetFor(_providerType, config)
            : null;
    }
}

/// <summary>Opens a visible terminal running the selected source's normal CLI.</summary>
public sealed class CliProviderLaunchAction : IProviderLaunchAction
{
    private readonly string _providerType;
    private readonly ProviderLoginDescriptor _descriptor;
    private readonly Func<string?> _terminalIconPath;

    public CliProviderLaunchAction(string providerType)
        : this(providerType, ResolveTerminalIconPath)
    {
    }

    internal CliProviderLaunchAction(string providerType, Func<string?> terminalIconPath)
    {
        if (!ProviderLoginCatalog.TryGet(providerType, out _descriptor!))
            throw new ArgumentException(
                $"Provider '{providerType}' has no verified interactive CLI descriptor.",
                nameof(providerType));

        _providerType = providerType;
        _terminalIconPath = terminalIconPath;
    }

    public ProviderLaunchInfo? GetInfo(string instanceId, IConfig config) =>
        TerminalLauncher.TryResolveCli(_descriptor, instanceId, config, out _)
            ? new ProviderLaunchInfo(
                $"{Catalog.ProviderName(_providerType)} CLI",
                _terminalIconPath())
            : null;

    public void Launch(string instanceId, IConfig config)
    {
        var result = TerminalLauncher.TryLaunchCli(_providerType, instanceId, config);
        if (result.Outcome == TerminalLaunchOutcome.Started)
            return;

        throw new ProviderException(
            result.Outcome == TerminalLaunchOutcome.CliMissing
                ? $"Not configured: {Catalog.ProviderName(_providerType)} CLI was not found."
                : $"Not configured: Could not start {Catalog.ProviderName(_providerType)} CLI.",
            ProviderErrorKind.Misconfigured);
    }

    private static string? ResolveTerminalIconPath()
    {
        var executable = TerminalLauncher.ResolveTerminalIconExecutable();
        return executable is null
            ? null
            : LaunchIconService.GetOrCreateIconPath(executable);
    }
}

/// <summary>Opens the selected browser-backed source's configured website.</summary>
public sealed class WebProviderLaunchAction : IProviderLaunchAction
{
    private readonly string _providerType;
    private readonly string _urlFieldKey;

    public WebProviderLaunchAction(string providerType, string urlFieldKey)
    {
        _providerType = providerType;
        _urlFieldKey = urlFieldKey;
    }

    public ProviderLaunchInfo? GetInfo(string instanceId, IConfig config) =>
        ResolveUrl(instanceId, config) is null
            ? null
            : new ProviderLaunchInfo($"{Catalog.ProviderName(_providerType)} Web");

    public void Launch(string instanceId, IConfig config)
    {
        var url = ResolveUrl(instanceId, config)
            ?? throw new ProviderException(
                $"Not configured: {Catalog.ProviderName(_providerType)} website URL is missing or invalid.",
                ProviderErrorKind.Misconfigured);

        try
        {
            Process.Start(BuildStartInfo(url));
        }
        catch (Exception error) when (error is not ProviderException)
        {
            throw new ProviderException(
                $"Failed to open {Catalog.ProviderName(_providerType)} website: {error.Message}",
                ProviderErrorKind.Misconfigured,
                error);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string url) => new()
    {
        FileName = url,
        UseShellExecute = true,
    };

    internal string? ResolveUrl(string instanceId, IConfig config)
    {
        var configured = TextUtil.Clean(config.GetScoped(instanceId, _urlFieldKey));
        var candidate = configured ?? Catalog.DefaultLoginUrlFor(_providerType);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }
}
