using static QuotaLens.Core.StringValues;

namespace QuotaLens.Core;

/// <summary>
/// Detects whether a local provider can be added without first asking the user
/// for paths or credentials. The catalog owns the declarative probes; this type
/// owns the filesystem, environment, and PATH checks those probes require.
/// </summary>
public static class ProviderLocalSetup
{
    public static bool NeedsSetup(
        string instanceId,
        string providerType,
        IConfig config,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? commandExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, string?>? environmentValue = null)
    {
        var normalized = Catalog.FindType(providerType)?.Id ?? providerType;
        if (!Catalog.LocalSetupProbes.TryGetValue(normalized, out var probe))
            return false;

        fileExists ??= File.Exists;
        commandExists ??= CommandExistsOnPath;
        directoryExists ??= Directory.Exists;
        environmentValue ??= EnvironmentValue;

        return !probe.Sources.Any(source => SourceIsSatisfied(
            source,
            instanceId,
            config,
            fileExists,
            commandExists,
            directoryExists,
            environmentValue));
    }

    internal static bool FilePathExists(string path, Func<string, bool> fileExists)
    {
        if (fileExists(path))
            return true;

        if (!ContainsWildcard(path))
            return false;

        var directory = Path.GetDirectoryName(path);
        var pattern = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(pattern))
            return false;

        return GlobbedFiles(directory, pattern).Any();
    }

    internal static bool CommandExistsOnPath(string command)
    {
        if (Path.IsPathFullyQualified(command))
            return File.Exists(command);

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = CommandExtensions(command);
        foreach (var directory in paths)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                    return true;
            }
        }

        return false;
    }

    private static bool SourceIsSatisfied(
        ProviderLocalSetupSource source,
        string instanceId,
        IConfig config,
        Func<string, bool> fileExists,
        Func<string, bool> commandExists,
        Func<string, bool> directoryExists,
        Func<string, string?> environmentValue) =>
        source.Requirements.Length > 0
        && source.Requirements.All(requirement => RequirementIsSatisfied(
            requirement,
            instanceId,
            config,
            fileExists,
            commandExists,
            directoryExists,
            environmentValue));

    private static bool RequirementIsSatisfied(
        ProviderLocalSetupRequirement requirement,
        string instanceId,
        IConfig config,
        Func<string, bool> fileExists,
        Func<string, bool> commandExists,
        Func<string, bool> directoryExists,
        Func<string, string?> environmentValue)
    {
        if (requirement.Values.Length == 0)
            return false;

        var checks = requirement.Values.Select(value => requirement.Kind switch
        {
            ProviderLocalSetupRequirementKind.ScopedConfig =>
                !string.IsNullOrWhiteSpace(config.GetScoped(instanceId, value)),
            ProviderLocalSetupRequirementKind.Environment =>
                !string.IsNullOrWhiteSpace(environmentValue(value)),
            ProviderLocalSetupRequirementKind.FilePath =>
                FilePathExists(ExpandPath(value), fileExists),
            ProviderLocalSetupRequirementKind.DirectoryPath =>
                directoryExists(ExpandPath(value)),
            ProviderLocalSetupRequirementKind.PathExecutable =>
                commandExists(value),
            ProviderLocalSetupRequirementKind.ScopedConfigFilePath =>
                ConfiguredFilePathExists(config.GetScoped(instanceId, value), requirement.PathTemplates, fileExists),
            ProviderLocalSetupRequirementKind.EnvironmentFilePath =>
                ConfiguredFilePathExists(environmentValue(value), requirement.PathTemplates, fileExists),
            _ => false,
        });

        return requirement.RequireAll ? checks.All(BooleanIdentity) : checks.Any(BooleanIdentity);
    }

    private static bool ConfiguredFilePathExists(
        string? configuredValue,
        IReadOnlyList<string> pathTemplates,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return false;

        var cleanedValue = ExpandPath(configuredValue);
        foreach (var template in pathTemplates)
        {
            var candidate = template.Contains("{value}", StringComparison.Ordinal)
                ? template.Replace("{value}", cleanedValue, StringComparison.Ordinal)
                : Path.Combine(cleanedValue, template);
            if (FilePathExists(ExpandPath(candidate), fileExists))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> GlobbedFiles(string directoryPattern, string filePattern)
    {
        foreach (var directory in GlobbedDirectories(directoryPattern))
        {
            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(directory, filePattern);
            }
            catch
            {
                continue;
            }

            foreach (var match in matches)
                yield return match;
        }
    }

    private static IEnumerable<string> GlobbedDirectories(string directoryPattern)
    {
        var normalized = directoryPattern.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(normalized);
        var start = string.IsNullOrWhiteSpace(root) ? "." : root;
        var remainder = string.IsNullOrWhiteSpace(root)
            ? normalized
            : normalized[root.Length..];
        var segments = remainder
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in GlobbedDirectories(start, segments, 0))
            yield return directory;
    }

    private static IEnumerable<string> GlobbedDirectories(string current, IReadOnlyList<string> segments, int index)
    {
        if (index >= segments.Count)
        {
            if (Directory.Exists(current))
                yield return current;
            yield break;
        }

        var segment = segments[index];
        if (!ContainsWildcard(segment))
        {
            var next = Path.Combine(current, segment);
            if (!Directory.Exists(next))
                yield break;

            foreach (var directory in GlobbedDirectories(next, segments, index + 1))
                yield return directory;
            yield break;
        }

        if (!Directory.Exists(current))
            yield break;

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(current, segment);
        }
        catch
        {
            yield break;
        }

        foreach (var child in children)
        {
            foreach (var directory in GlobbedDirectories(child, segments, index + 1))
                yield return directory;
        }
    }

    private static string ExpandPath(string path) =>
        Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static bool BooleanIdentity(bool value) => value;

    private static string? EnvironmentValue(string key) =>
        FirstNonEmpty(
            Environment.GetEnvironmentVariable(key),
            Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine));



    private static bool ContainsWildcard(string path) =>
        path.Contains('*') || path.Contains('?');

    private static IEnumerable<string> CommandExtensions(string command)
    {
        if (Path.HasExtension(command))
        {
            yield return "";
            yield break;
        }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathext)
            ? new[] { ".COM", ".EXE", ".BAT", ".CMD" }
            : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var extension in extensions)
            yield return extension;
        yield return "";
    }
}
