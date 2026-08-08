using System.Diagnostics;
using Microsoft.Win32;

namespace QuotaLens.Services;

internal readonly record struct AppLaunchPolicy(
    bool AcquireSingleInstance,
    bool SignalExistingInstanceOnConflict,
    bool CreateTray,
    bool ActivateMainWindow,
    bool StartRefresh)
{
    public static AppLaunchPolicy FromArguments(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        var startHidden = StartupLaunchService.IsHiddenLaunch(values);
        var uiSmoke = StartupLaunchService.IsUiSmokeLaunch(values);
        return new AppLaunchPolicy(
            AcquireSingleInstance: !uiSmoke,
            SignalExistingInstanceOnConflict: !uiSmoke && !startHidden,
            CreateTray: !uiSmoke,
            ActivateMainWindow: uiSmoke || !startHidden,
            StartRefresh: !uiSmoke);
    }
}

/// <summary>Controls the current user's Windows logon startup entry.</summary>
public sealed class StartupLaunchService
{
    internal const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string DefaultValueName = "QuotaLens";
    internal const string StartHiddenArgument = "--startup-hidden";
    internal const string UiSmokeArgument = "--ui-smoke";

    private readonly string _runKeyPath;
    private readonly string _valueName;
    private readonly Func<string?> _processPath;

    public StartupLaunchService()
        : this(DefaultRunKeyPath, DefaultValueName, CurrentProcessPath)
    {
    }

    internal StartupLaunchService(string runKeyPath, string valueName, Func<string?> processPath)
    {
        _runKeyPath = runKeyPath;
        _valueName = valueName;
        _processPath = processPath;
    }

    public bool IsEnabled()
    {
        return ReadRunCommand() is { Length: > 0 };
    }

    public bool IsStartHiddenEnabled()
    {
        return ReadRunCommand() is { } command
            && command.TrimEnd().EndsWith($" {StartHiddenArgument}", StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled, bool startHidden)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true)
                ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
            key.SetValue(_valueName, BuildRunCommand(ResolvedProcessPath(), startHidden), RegistryValueKind.String);
            return;
        }

        using var writableKey = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
        writableKey?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    internal static string BuildRunCommand(string executablePath, bool startHidden) =>
        startHidden
            ? $"\"{executablePath}\" {StartHiddenArgument}"
            : $"\"{executablePath}\"";

    internal static bool IsHiddenLaunch(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.Equals(StartHiddenArgument, StringComparison.OrdinalIgnoreCase));

    internal static bool IsUiSmokeLaunch(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.Equals(UiSmokeArgument, StringComparison.OrdinalIgnoreCase));

    private string? ReadRunCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
        return key?.GetValue(_valueName) is string value && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private string ResolvedProcessPath()
    {
        var path = _processPath();
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Could not determine the QuotaLens executable path.");

        return Path.GetFullPath(path);
    }

    private static string? CurrentProcessPath() =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName;
}
