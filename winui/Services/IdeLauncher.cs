using System.Diagnostics;
using System.IO;
using QuotaLens.Core;
using Windows.Management.Deployment;

namespace QuotaLens.Services;

/// <summary>
/// Launches a provider's GUI application. Quota probes may use CLIs, but the
/// card launch button should resolve the desktop app from catalog metadata.
/// </summary>
public static class IdeLauncher
{
    public static void LaunchIde(string providerId, ProviderLaunchTarget target, string? customPath)
    {
        var exePath = ResolveLaunchPath(providerId, target, customPath);

        // Launch the IDE. UseShellExecute=true so the path is resolved like the Rust
        // tokio::process::Command::new spawn (no piping/redirection of the IDE process).
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            throw new ProviderException($"Failed to launch {exePath}: {e.Message}", e);
        }
    }

    internal static string ResolveLaunchPath(
        string providerId,
        ProviderLaunchTarget target,
        string? customPath,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, string, IEnumerable<string>>? enumerateDirectories = null,
        Func<string, string?>? packageInstallLocation = null)
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;
        enumerateDirectories ??= EnumerateMatchingDirectories;
        packageInstallLocation ??= ResolvePackageInstallLocation;

        if (!string.IsNullOrWhiteSpace(customPath))
            return ResolveCustomPath(target, customPath, fileExists, directoryExists, enumerateDirectories);

        var packagedAppExecutable = ResolvePackagedAppExecutable(target, fileExists, packageInstallLocation);
        if (packagedAppExecutable != null)
            return packagedAppExecutable;

        foreach (var path in target.DefaultPaths.Select(ExpandPath).SelectMany(path => ExpandPathCandidates(path, directoryExists, enumerateDirectories)))
        {
            if (fileExists(path))
                return path;

            var executable = ResolveDirectoryExecutable(path, target, fileExists, directoryExists);
            if (executable != null)
                return executable;
        }

        throw new ProviderException($"{target.DisplayName} app not found. Please set the app path in settings.");
    }

    internal static bool TryResolveLaunchPath(
        string providerId,
        ProviderLaunchTarget target,
        string? customPath,
        out string launchPath,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, string, IEnumerable<string>>? enumerateDirectories = null,
        Func<string, string?>? packageInstallLocation = null)
    {
        try
        {
            launchPath = ResolveLaunchPath(providerId, target, customPath, fileExists, directoryExists, enumerateDirectories, packageInstallLocation);
            return true;
        }
        catch (ProviderException)
        {
            launchPath = "";
            return false;
        }
    }

    private static string ResolveCustomPath(
        ProviderLaunchTarget target,
        string customPath,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, string, IEnumerable<string>> enumerateDirectories)
    {
        var expanded = ExpandPath(customPath);
        foreach (var path in ExpandPathCandidates(expanded, directoryExists, enumerateDirectories))
        {
            var executable = ResolveDirectoryExecutable(path, target, fileExists, directoryExists);
            if (executable != null)
                return executable;

            if (fileExists(path))
                return path;
        }

        throw new ProviderException($"{target.DisplayName} app not found at {expanded}. Please set the app path in settings.");
    }

    private static string? ResolveDirectoryExecutable(
        string path,
        ProviderLaunchTarget target,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        if (!directoryExists(path))
            return null;

        foreach (var executableName in target.DirectoryExecutableNames)
        {
            var candidate = Path.Combine(path, executableName);
            if (fileExists(candidate))
                return candidate;
        }

        return null;
    }

    private static string ExpandPath(string path)
        => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static string? ResolvePackagedAppExecutable(
        ProviderLaunchTarget target,
        Func<string, bool> fileExists,
        Func<string, string?> packageInstallLocation)
    {
        if (string.IsNullOrWhiteSpace(target.PackageFamilyName) ||
            string.IsNullOrWhiteSpace(target.PackageExecutableRelativePath))
        {
            return null;
        }

        var installLocation = packageInstallLocation(target.PackageFamilyName);
        if (string.IsNullOrWhiteSpace(installLocation))
            return null;

        var executable = Path.Combine(installLocation, target.PackageExecutableRelativePath);
        return fileExists(executable) ? executable : null;
    }

    private static string? ResolvePackageInstallLocation(string packageFamilyName)
    {
        try
        {
            var manager = new PackageManager();
            return manager.FindPackagesForUser(string.Empty)
                .Where(package => string.Equals(package.Id.FamilyName, packageFamilyName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(package => package.Id.Version.Major)
                .ThenByDescending(package => package.Id.Version.Minor)
                .ThenByDescending(package => package.Id.Version.Build)
                .ThenByDescending(package => package.Id.Version.Revision)
                .Select(package => package.InstalledLocation.Path)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ExpandPathCandidates(
        string path,
        Func<string, bool> directoryExists,
        Func<string, string, IEnumerable<string>> enumerateDirectories)
    {
        if (!HasWildcard(path))
        {
            yield return path;
            yield break;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            yield break;

        var segments = path[root.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> candidates = new[] { root };

        foreach (var segment in segments)
        {
            candidates = HasWildcard(segment)
                ? candidates.SelectMany(candidate => directoryExists(candidate)
                    ? enumerateDirectories(candidate, segment)
                    : Array.Empty<string>())
                : candidates.Select(candidate => Path.Combine(candidate, segment));
        }

        foreach (var candidate in candidates)
            yield return candidate;
    }

    private static IEnumerable<string> EnumerateMatchingDirectories(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, pattern)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool HasWildcard(string path) => path.IndexOfAny(new[] { '*', '?' }) >= 0;
}
