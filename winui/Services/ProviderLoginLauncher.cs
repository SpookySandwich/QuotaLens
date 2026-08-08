using QuotaLens.Core;

namespace QuotaLens.Services;

/// <summary>
/// Starts a provider's sign-in for the user instead of telling them to run something
/// themselves. Previously hardcoded to Claude, which is why every other CLI-backed
/// provider showed "Login required… run &lt;x&gt;" with no button to press.
/// </summary>
public static class ProviderLoginLauncher
{
    public static bool IsSupported(string providerType) =>
        ProviderLoginCatalog.Descriptors.ContainsKey(providerType);

    /// <summary>The CLI this provider signs in with, for button labels and messages.</summary>
    public static string? CliCommandFor(string providerType) =>
        ProviderLoginCatalog.TryGet(providerType, out var descriptor) ? descriptor.CliCommand : null;

    public static string? InstallUrlFor(string providerType) =>
        ProviderLoginCatalog.TryGet(providerType, out var descriptor) ? descriptor.InstallUrl : null;

    /// <summary>
    /// Extra instruction for CLIs that sign in via a command typed inside their own REPL
    /// rather than an argv verb, so the terminal alone would leave the user stranded.
    /// </summary>
    public static string? InteractiveHintKeyFor(string providerType) =>
        ProviderLoginCatalog.TryGet(providerType, out var descriptor) ? descriptor.InteractiveHintKey : null;

    /// <summary>True when the CLI is actually present, so the card can offer install instead.</summary>
    public static bool IsCliInstalled(string providerType, string instanceId, IConfig config) =>
        ProviderLoginCatalog.TryGet(providerType, out var descriptor)
        && TerminalLauncher.TryResolveCli(descriptor, instanceId, config, out _);

    public static TerminalLaunchOutcome TryLaunch(string providerType, string instanceId, IConfig config) =>
        IsSupported(providerType)
            ? TerminalLauncher.TryLaunchLogin(providerType, instanceId, config)
            : TerminalLaunchOutcome.CliMissing;

    /// <summary>
    /// Opens the page where the provider's CLI is obtained. This is what makes the
    /// sign-in button honest when the CLI is not installed: without it the click
    /// silently does nothing, which is exactly the dead end the button exists to remove.
    /// </summary>
    public static bool TryOpenInstallPage(string providerType)
    {
        var url = InstallUrlFor(providerType);
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts sign-in, falling back to the install page when the CLI is missing, so a
    /// click always produces a visible result. Returns false only when nothing could be done.
    /// </summary>
    public static bool TryStartLoginOrInstall(string providerType, string instanceId, IConfig config)
    {
        if (!IsSupported(providerType))
            return false;

        return TryLaunch(providerType, instanceId, config) switch
        {
            TerminalLaunchOutcome.Started => true,
            TerminalLaunchOutcome.CliMissing => TryOpenInstallPage(providerType),
            _ => false,
        };
    }
}
