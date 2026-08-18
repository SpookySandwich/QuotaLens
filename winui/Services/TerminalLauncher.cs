using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using QuotaLens.Core;

namespace QuotaLens.Services;

public enum TerminalLaunchOutcome
{
    Started,
    CliMissing,
    LaunchFailed,
}

/// <summary>Result of a login-terminal launch: the outcome plus the process to await.</summary>
public sealed record TerminalLaunchResult(TerminalLaunchOutcome Outcome, Process? Process);

/// <summary>
/// Opens a VISIBLE terminal running a provider's sign-in command, so signing in is a
/// button press instead of an instruction to go do it yourself.
///
/// The command is passed as a base64 <c>-EncodedCommand</c> payload rather than a shell
/// string. That is not incidental: <c>cmd /k "..."</c> mis-parses paths containing spaces,
/// <c>&amp;</c> or <c>^</c>, and it opens a <c>.ps1</c> shim in an editor instead of running
/// it. Base64's alphabet contains no character any command-line parser treats specially,
/// so the payload survives byte-identical however it is re-parsed on the way to the shell.
/// </summary>
public static class TerminalLauncher
{
    public static TerminalLaunchResult TryLaunchLogin(
        string providerType,
        string instanceId,
        IConfig config)
    {
        if (!ProviderLoginCatalog.TryGet(providerType, out var descriptor))
            return new(TerminalLaunchOutcome.CliMissing, null);

        if (!TryResolveCli(descriptor, instanceId, config, out var binary))
        {
            AppLog.Warn($"login: {providerType} CLI not resolved (command '{descriptor.CliCommand}'); offering install page");
            return new(TerminalLaunchOutcome.CliMissing, null);
        }

        var arguments = LoginArguments(descriptor, instanceId, config);
        var encoded = EncodeLoginScript(binary, arguments, descriptor.ProviderType);
        AppLog.Info($"login: launching {binary} {string.Join(" ", arguments)} for {providerType}");

        var startInfo = BuildStartInfo(encoded);
        try
        {
            var process = Process.Start(startInfo);
            if (process is not null)
            {
                AppLog.Info($"login: {providerType} terminal started ({Path.GetFileName(startInfo.FileName)})");
                return new(TerminalLaunchOutcome.Started, process);
            }
        }
        catch (Exception e)
        {
            AppLog.Warn($"login: {providerType} terminal launch failed: {e.Message}");
        }

        return new(TerminalLaunchOutcome.LaunchFailed, null);
    }

    /// <summary>
    /// Opens the selected source's ordinary interactive CLI with no login arguments.
    /// This is the dashboard launch action, distinct from the setup action above.
    /// </summary>
    public static TerminalLaunchResult TryLaunchCli(
        string providerType,
        string instanceId,
        IConfig config)
    {
        if (!ProviderLoginCatalog.TryGet(providerType, out var descriptor))
            return new(TerminalLaunchOutcome.CliMissing, null);

        if (!TryResolveCli(descriptor, instanceId, config, out var binary))
        {
            AppLog.Warn($"launch: {providerType} CLI not resolved (command '{descriptor.CliCommand}')");
            return new(TerminalLaunchOutcome.CliMissing, null);
        }

        var encoded = EncodeCliScript(binary, providerType);
        AppLog.Info($"launch: opening interactive {providerType} CLI at {binary}");

        var startInfo = BuildStartInfo(encoded);
        try
        {
            var process = Process.Start(startInfo);
            if (process is not null)
            {
                AppLog.Info($"launch: {providerType} CLI terminal started ({Path.GetFileName(startInfo.FileName)})");
                return new(TerminalLaunchOutcome.Started, process);
            }
        }
        catch (Exception error)
        {
            AppLog.Warn($"launch: {providerType} CLI terminal failed: {error.Message}");
        }

        return new(TerminalLaunchOutcome.LaunchFailed, null);
    }

    /// <summary>
    /// Resolves the CLI, preferring a user-configured path, then PATH.
    /// <see cref="HiddenCliProcess.ResolveBinary"/> returns the bare name unchanged when
    /// nothing matches, so the existence check is load-bearing — a non-empty result is
    /// NOT success.
    /// </summary>
    internal static bool TryResolveCli(
        ProviderLoginDescriptor descriptor,
        string instanceId,
        IConfig config,
        out string path,
        Func<string, string>? resolve = null,
        Func<string, bool>? fileExists = null)
    {
        resolve ??= binary => HiddenCliProcess.ResolveBinary(binary);
        fileExists ??= File.Exists;
        path = "";

        foreach (var candidate in Candidates(descriptor, instanceId, config))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string resolved;
            try
            {
                resolved = resolve(Environment.ExpandEnvironmentVariables(candidate));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(resolved) && fileExists(resolved))
            {
                path = resolved;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the native terminal executable whose icon represents CLI launches.
    /// Windows Terminal is preferred; Windows PowerShell is the built-in fallback.
    /// </summary>
    internal static string? ResolveTerminalIconExecutable(
        string? localAppData = null,
        Func<string, string>? resolve = null,
        Func<string, bool>? fileExists = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        resolve ??= binary => HiddenCliProcess.ResolveBinary(binary);
        fileExists ??= File.Exists;

        var candidates = new List<string>
        {
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe"),
        };
        try
        {
            candidates.Add(resolve("wt.exe"));
        }
        catch (ArgumentException)
        {
            // PATH resolution is optional; the app-execution alias above is primary.
        }

        candidates.Add(Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe"));

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(fileExists);
    }

    private static IEnumerable<string> Candidates(
        ProviderLoginDescriptor descriptor,
        string instanceId,
        IConfig config)
    {
        yield return config.GetScoped(instanceId, descriptor.CliPathFieldKey);

        yield return descriptor.CliCommand;
    }

    internal static IReadOnlyList<string> LoginArguments(
        ProviderLoginDescriptor descriptor,
        string instanceId,
        IConfig config)
    {
        var arguments = descriptor.LoginArgs.ToList();
        if (string.IsNullOrWhiteSpace(descriptor.ProfileFieldKey))
            return arguments;

        var profile = config.GetScoped(instanceId, descriptor.ProfileFieldKey!);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            arguments.Add("--profile");
            arguments.Add(profile);
        }

        return arguments;
    }

    /// <summary>
    /// Builds the UTF-16LE base64 payload PowerShell's -EncodedCommand expects. Pure:
    /// the same inputs always produce the same string, so it is directly testable.
    /// </summary>
    internal static string EncodeLoginScript(
        string binary,
        IReadOnlyList<string> arguments,
        string providerType)
    {
        // Single-quoted PowerShell literals: the only escape needed is '' for a quote,
        // and `& $binary @args` never re-parses the path as text.
        var argumentList = arguments.Count == 0
            ? "@()"
            : "@(" + string.Join(", ", arguments.Select(Quote)) + ")";

        // Built line-by-line rather than as an interpolated literal: the script is
        // PowerShell, so it is full of braces that would fight C# interpolation.
        var lines = new[]
        {
            "$ErrorActionPreference = 'Continue'",
            "$binary = " + Quote(binary),
            "$cliArguments = " + argumentList,
            "Write-Host " + Quote($"QuotaLens: signing in to {providerType}...") + " -ForegroundColor Cyan",
            "Write-Host ''",
            "& $binary @cliArguments",
            "Write-Host ''",
            "if ($LASTEXITCODE -eq 0) {",
            "    Write-Host 'Sign-in finished. QuotaLens will refresh automatically.' -ForegroundColor Green",
            "    Start-Sleep -Seconds 2",
            "    exit 0",
            "} else {",
            "    Write-Host \"Sign-in exited with code $LASTEXITCODE. Press Enter to close this window.\" -ForegroundColor Yellow",
            "    Read-Host",
            "}",
        };
        var script = string.Join(Environment.NewLine, lines);

        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>Builds the safe encoded script used by the everyday CLI launch.</summary>
    internal static string EncodeCliScript(string binary, string providerType)
    {
        var lines = new[]
        {
            "$ErrorActionPreference = 'Continue'",
            "$binary = " + Quote(binary),
            "Write-Host " + Quote($"QuotaLens: opening {providerType} CLI...") + " -ForegroundColor Cyan",
            "Write-Host ''",
            "& $binary",
            "$exitCode = $LASTEXITCODE",
            "if ($null -eq $exitCode -or $exitCode -eq 0) {",
            "    exit 0",
            "} else {",
            "    Write-Host \"CLI exited with code $exitCode. Press Enter to close this window.\" -ForegroundColor Yellow",
            "    Read-Host",
            "}",
        };
        var script = string.Join(Environment.NewLine, lines);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Builds — never starts — the start info, so shape can be asserted in tests.
    /// UseShellExecute gives the child its own console; without it the sign-in prompt
    /// would inherit (and hide inside) whatever console launched QuotaLens. Launched
    /// directly (not via Windows Terminal) so the caller holds the process handle and
    /// can await its exit once sign-in finishes.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string encoded)
    {
        var powershell = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            powershell = "powershell.exe";

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = true,
            CreateNoWindow = false,
            FileName = powershell,
        };
        foreach (var argument in PowerShellArguments(encoded))
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    // Deliberately NOT -NonInteractive: signing in is interactive by definition. The
    // window closes itself on success (exit 0) and stays open on failure (Read-Host).
    private static IEnumerable<string> PowerShellArguments(string encoded) =>
        ["-NoLogo", "-NoProfile", "-EncodedCommand", encoded];
}
