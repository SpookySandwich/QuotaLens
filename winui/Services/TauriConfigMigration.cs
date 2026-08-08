using System.IO;
using System.Text;
using System.Text.Json;
using QuotaLens.Core;

namespace QuotaLens.Services;

/// <summary>
/// One-time migration from the OLD Tauri WebView2 localStorage into the native
/// QuotaLens config. The original Tauri build persisted config in the WebView2
/// renderer's localStorage (a LevelDB store), under the keys:
///   * <c>quotalens_config</c>     — a JSON object string (string → string).
///   * <c>ql_extra_providers</c>   — a JSON array string of { id, type, name }.
///
/// This helper reads the raw LevelDB <c>*.ldb</c> / <c>*.log</c> files (no LevelDB
/// dependency), locates those keys, and extracts the balanced JSON value that
/// follows each one. It is intentionally defensive: anything unexpected (missing
/// directory, missing keys, corrupted/compressed records, malformed JSON) results
/// in <c>null</c> rather than an exception.
///
/// Integration calls <see cref="TryLoad"/> exactly once, only when the native
/// config.json does NOT yet exist, then seeds the freshly-built ConfigService with
/// the returned data.
/// </summary>
public static class TauriConfigMigration
{
    private const string ConfigKey = "quotalens_config";
    private const string ExtrasKey = "ql_extra_providers";

    /// <summary>The extracted, sanitized result of a migration scan.</summary>
    public sealed record Result(
        IReadOnlyDictionary<string, string> Config,
        IReadOnlyList<ProviderInstance> Extras);

    /// <summary>
    /// Default LevelDB location for the old Tauri WebView2 localStorage:
    /// <c>%LOCALAPPDATA%\com.quotalens.app\EBWebView\Default\Local Storage\leveldb</c>.
    /// </summary>
    public static string DefaultLevelDbDir
    {
        get
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            return Path.Combine(localAppData, "com.quotalens.app", "EBWebView",
                "Default", "Local Storage", "leveldb");
        }
    }

    /// <summary>
    /// Scans the old Tauri LevelDB store and returns the migrated config + extra
    /// instances, or <c>null</c> if nothing usable was found. Never throws.
    /// </summary>
    public static Result? TryLoad() => TryLoad(DefaultLevelDbDir);

    /// <summary>Testable overload that scans a specific LevelDB directory.</summary>
    public static Result? TryLoad(string levelDbDir)
    {
        try
        {
            if (string.IsNullOrEmpty(levelDbDir) || !Directory.Exists(levelDbDir))
                return null;

            // Concatenate every sstable / write-ahead-log file. LevelDB keeps the
            // most recent record last, so scanning the combined stream and taking
            // the LAST valid parse mirrors "newest wins".
            string[] files;
            try
            {
                files = Directory.GetFiles(levelDbDir, "*.ldb")
                    .Concat(Directory.GetFiles(levelDbDir, "*.log"))
                    .OrderBy(f => SafeWriteTime(f))
                    .ToArray();
            }
            catch
            {
                return null;
            }

            if (files.Length == 0)
                return null;

            Dictionary<string, string>? config = null;
            List<ProviderInstance>? extras = null;

            foreach (var file in files)
            {
                string decoded;
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    // Latin1 (ISO-8859-1) maps every byte 1:1 to a char, so byte
                    // offsets stay aligned and the embedded ASCII strings survive.
                    decoded = Encoding.Latin1.GetString(bytes);
                }
                catch
                {
                    continue;
                }

                // Take the LAST valid parse across all files (most recent value).
                var cfg = ExtractLastJsonObject(decoded, ConfigKey);
                if (cfg != null)
                    config = ParseConfig(cfg);

                var arr = ExtractLastJsonArray(decoded, ExtrasKey);
                if (arr != null)
                {
                    var parsed = ParseExtras(arr);
                    if (parsed != null)
                        extras = parsed;
                }
            }

            if (config == null && extras == null)
                return null;

            return new Result(
                config ?? new Dictionary<string, string>(),
                extras ?? new List<ProviderInstance>());
        }
        catch
        {
            // Absolutely never let migration crash startup.
            return null;
        }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// Finds every occurrence of <paramref name="key"/> in <paramref name="haystack"/>,
    /// captures the balanced <c>{...}</c> object that follows each, attempts a strict
    /// JSON parse, and returns the raw text of the LAST one that parses (or null).
    /// </summary>
    private static string? ExtractLastJsonObject(string haystack, string key)
        => ExtractLast(haystack, key, '{', '}');

    /// <summary>Same as <see cref="ExtractLastJsonObject"/> but for a <c>[...]</c> array.</summary>
    private static string? ExtractLastJsonArray(string haystack, string key)
        => ExtractLast(haystack, key, '[', ']');

    private static string? ExtractLast(string haystack, string key, char open, char close)
    {
        string? last = null;
        var searchFrom = 0;
        while (true)
        {
            var keyIdx = haystack.IndexOf(key, searchFrom, StringComparison.Ordinal);
            if (keyIdx < 0)
                break;
            searchFrom = keyIdx + key.Length;

            // Scan forward to the first opening bracket after the key.
            var openIdx = haystack.IndexOf(open, searchFrom);
            if (openIdx < 0)
                continue;

            var slice = CaptureBalanced(haystack, openIdx, open, close);
            if (slice == null)
                continue;

            // Only keep it if it is strictly valid JSON of the right kind.
            if (IsValidJson(slice))
                last = slice; // later occurrences overwrite earlier ones
        }
        return last;
    }

    /// <summary>
    /// Captures a balanced bracket region starting at <paramref name="start"/>,
    /// respecting quoted strings and escapes. Returns null if it never balances
    /// (e.g. truncated record). Bracket characters inside a JSON string don't count.
    /// </summary>
    private static string? CaptureBalanced(string s, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else
            {
                if (c == '"') inString = true;
                else if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                        return s.Substring(start, i - start + 1);
                }
            }
        }
        return null;
    }

    private static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses the migrated config object into a string→string dictionary. Strips the
    /// schema_version marker and skips any value that is not a JSON string.
    /// </summary>
    private static Dictionary<string, string>? ParseConfig(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var dict = new Dictionary<string, string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // The old store kept a "config_schema_version" key; drop it.
                if (prop.Name == "config_schema_version")
                    continue;
                // Only import keys whose value is a string (matches the original
                // string→string config map).
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var value = prop.Value.GetString();
                    if (value != null)
                        dict[prop.Name] = value;
                }
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the ql_extra_providers array into ProviderInstance records. Entries
    /// missing id/type are skipped; name falls back to the catalog/type.
    /// </summary>
    private static List<ProviderInstance>? ParseExtras(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<ProviderInstance>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;

                var id = GetStringProp(el, "id");
                var type = GetStringProp(el, "type");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type))
                    continue;

                var name = GetStringProp(el, "name");
                if (string.IsNullOrEmpty(name))
                {
                    var t = Catalog.Types.FirstOrDefault(x => x.Id == type);
                    name = t?.Name ?? type;
                }

                list.Add(new ProviderInstance(id, type, name));
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProp(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
