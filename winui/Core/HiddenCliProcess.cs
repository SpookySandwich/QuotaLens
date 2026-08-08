using System.Diagnostics;
using System.Text;

namespace QuotaLens.Core;

/// <summary>
/// Creates hidden, redirected CLI processes on Windows, including commands installed
/// as .cmd/.bat/.ps1 shims. Script arguments are carried in the child environment instead
/// of being concatenated into executable command text.
/// </summary>
internal static class HiddenCliProcess
{
    private const string BinaryVariable = "QUOTALENS_CLI_BINARY";
    private const string ArgumentVariablePrefix = "QUOTALENS_CLI_ARG_";
    private const string ArgumentCountVariable = "QUOTALENS_CLI_ARG_COUNT";
    private static readonly string[] DefaultExecutableExtensions = [".exe", ".com", ".cmd", ".bat"];
    private static readonly string EncodedScriptCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes("""
        $ErrorActionPreference = 'Stop'
        try {
            $binary = [Environment]::GetEnvironmentVariable('QUOTALENS_CLI_BINARY', 'Process')
            $count = [int][Environment]::GetEnvironmentVariable('QUOTALENS_CLI_ARG_COUNT', 'Process')
            $cliArguments = for ($index = 0; $index -lt $count; $index++) {
                [Environment]::GetEnvironmentVariable("QUOTALENS_CLI_ARG_$index", 'Process')
            }
            & $binary @cliArguments
            exit $LASTEXITCODE
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """));

    public static ProcessStartInfo CreateStartInfo(string binary, IEnumerable<string> arguments)
    {
        var resolvedBinary = ResolveBinary(binary);
        var resolvedArguments = arguments.ToArray();
        return IsScriptShim(resolvedBinary)
            ? CreateScriptStartInfo(resolvedBinary, resolvedArguments)
            : CreateNativeStartInfo(resolvedBinary, resolvedArguments);
    }

    internal static string ResolveBinary(
        string binary,
        IEnumerable<string>? searchDirectories = null,
        IEnumerable<string>? executableExtensions = null)
    {
        if (string.IsNullOrWhiteSpace(binary))
            throw new ArgumentException("CLI path cannot be empty.", nameof(binary));

        var clean = binary.Trim().Trim('"');
        if (File.Exists(clean))
            return Path.GetFullPath(clean);

        var extensions = (executableExtensions ?? ExecutableExtensions())
            .Select(NormalizeExtension)
            .Where(extension => extension.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasDirectory = clean.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;
        if (hasDirectory || Path.IsPathRooted(clean))
        {
            foreach (var candidate in Candidates(clean, extensions))
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            return clean;
        }

        foreach (var directory in searchDirectories ?? PathDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string basePath;
            try
            {
                basePath = Path.Combine(directory.Trim().Trim('"'), clean);
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var candidate in Candidates(basePath, extensions))
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
        }

        // Native CreateProcess still performs normal executable lookup. Keeping the
        // unresolved value also preserves the provider's actionable launch error.
        return clean;
    }

    private static ProcessStartInfo CreateNativeStartInfo(string binary, IReadOnlyList<string> arguments)
    {
        var startInfo = BaseStartInfo(binary);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static ProcessStartInfo CreateScriptStartInfo(string binary, IReadOnlyList<string> arguments)
    {
        EnsureEnvironmentSafe(binary, "CLI path");
        foreach (var argument in arguments)
            EnsureEnvironmentSafe(argument, "CLI argument");

        var windowsPowerShell = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(windowsPowerShell))
            windowsPowerShell = "powershell.exe";

        var startInfo = BaseStartInfo(windowsPowerShell);
        startInfo.Environment[BinaryVariable] = binary;
        startInfo.Environment[ArgumentCountVariable] = arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < arguments.Count; index++)
            startInfo.Environment[ArgumentVariablePrefix + index] = arguments[index];

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(EncodedScriptCommand);
        return startInfo;
    }

    private static ProcessStartInfo BaseStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    private static IEnumerable<string> Candidates(string path, IReadOnlyList<string> extensions)
    {
        if (Path.HasExtension(path))
        {
            yield return path;
            yield break;
        }

        foreach (var extension in extensions)
            yield return path + extension;
    }

    private static IEnumerable<string> PathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> ExecutableExtensions()
    {
        var configured = (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return configured.Concat(DefaultExecutableExtensions);
    }

    private static string NormalizeExtension(string extension)
    {
        var clean = extension.Trim();
        return clean.StartsWith('.') ? clean : "." + clean;
    }

    private static bool IsScriptShim(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase));

    private static void EnsureEnvironmentSafe(string value, string description)
    {
        if (value.Contains('\0'))
            throw new InvalidOperationException($"{description} contains a null character and cannot be passed to a child process.");
    }
}
