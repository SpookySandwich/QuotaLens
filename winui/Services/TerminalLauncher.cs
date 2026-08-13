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
    public static TerminalLaunchOutcome TryLaunchLogin(
        string providerType,
        string instanceId,
        IConfig config)
    {
        if (!ProviderLoginCatalog.TryGet(providerType, out var descriptor))
            return TerminalLaunchOutcome.CliMissing;

        if (!TryResolveCli(descriptor, instanceId, config, out var binary))
        {
            AppLog.Warn($"login: {providerType} CLI not resolved (command '{descriptor.CliCommand}'); offering install page");
            return TerminalLaunchOutcome.CliMissing;
        }

        var arguments = LoginArguments(descriptor, instanceId, config);
        var encoded = EncodeLoginScript(binary, arguments, descriptor.ProviderType);
        AppLog.Info($"login: launching {binary} {string.Join(" ", arguments)} for {providerType}");

        // Windows Terminal first, console host as fallback. An app-execution alias can
        // exist yet fail to launch (stale alias after the Store app is removed), so a
        // wt failure must fall through rather than be reported as terminal failure.
        foreach (var startInfo in CandidateStartInfos(encoded, descriptor.ProviderType))
        {
            try
            {
                // Never WaitForExit: wt.exe hands off to the Windows Terminal process and
                // exits immediately, so its exit code says nothing about the sign-in.
                if (Process.Start(startInfo) is not null)
                {
                    AppLog.Info($"login: {providerType} terminal started ({Path.GetFileName(startInfo.FileName)})");
                    return TerminalLaunchOutcome.Started;
                }
            }
            catch (Win32Exception)
            {
                // Try the next host.
            }
            catch (Exception)
            {
                // Try the next host.
            }
        }

        AppLog.Error($"login: {providerType} terminal launch failed for binary {binary}");
        return TerminalLaunchOutcome.LaunchFailed;
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

    private static IEnumerable<string> Candidates(
        ProviderLoginDescriptor descriptor,
        string instanceId,
        IConfig config)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.CliPathFieldKey))
            yield return config.GetScoped(instanceId, descriptor.CliPathFieldKey!);

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
            "    Write-Host 'Sign-in finished. You can close this window - QuotaLens will pick it up.' -ForegroundColor Green",
            "} else {",
            "    Write-Host \"Sign-in exited with code $LASTEXITCODE. This window stays open so you can read the error.\" -ForegroundColor Yellow",
            "}",
        };
        var script = string.Join(Environment.NewLine, lines);

        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    internal static IEnumerable<ProcessStartInfo> CandidateStartInfos(string encoded, string providerType)
    {
        var windowsTerminal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(windowsTerminal))
            yield return BuildStartInfo(windowsTerminal, encoded, providerType);

        yield return BuildStartInfo(null, encoded, providerType);
    }

    /// <summary>
    /// Builds — never starts — the start info, so shape can be asserted in tests.
    /// UseShellExecute gives the child its own console; without it the sign-in prompt
    /// would inherit (and hide inside) whatever console launched QuotaLens.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string? windowsTerminalPath, string encoded, string providerType)
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
        };

        if (windowsTerminalPath is null)
        {
            startInfo.FileName = powershell;
            foreach (var argument in PowerShellArguments(encoded))
                startInfo.ArgumentList.Add(argument);
            return startInfo;
        }

        startInfo.FileName = windowsTerminalPath;
        startInfo.ArgumentList.Add("new-tab");
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add($"QuotaLens · {providerType}");
        // The -- terminator is required, or wt parses -NoLogo as one of its own options.
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(powershell);
        foreach (var argument in PowerShellArguments(encoded))
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    // -NoExit keeps the window open so the user can read a failure. Deliberately NOT
    // -NonInteractive: signing in is interactive by definition.
    private static IEnumerable<string> PowerShellArguments(string encoded) =>
        ["-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", encoded];
}
