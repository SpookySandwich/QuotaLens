using System.Text.Json;
using QuotaLens.Core;

namespace QuotaLens.Services;

/// <summary>
/// Persists the latest healthy snapshot per provider instance so a restart can show
/// last-known data (marked stale) instead of an empty or error card while the first
/// refresh runs. One JSON file per instance under %LOCALAPPDATA%\QuotaLens\Snapshots.
/// Failed fetches are never persisted, so an error never overwrites the last good data.
/// </summary>
public sealed class SnapshotStore
{
    private const int Version = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed record Envelope(int Version, string ProviderType, ProviderSnapshot Snapshot);

    private readonly string _directory;

    public SnapshotStore(string directory) => _directory = directory;

    public ProviderSnapshot? Load(string instanceId, string providerType)
    {
        var path = PathFor(instanceId);
        try
        {
            if (!File.Exists(path))
                return null;

            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), JsonOptions);
            if (envelope?.Snapshot?.Primary is null)
                return null;

            // A card re-pointed at a different provider type must not inherit the old cache.
            if (!string.Equals(envelope.ProviderType, providerType, StringComparison.OrdinalIgnoreCase))
                return null;

            return envelope.Snapshot;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string instanceId, string providerType, ProviderSnapshot snapshot)
    {
        // Only healthy snapshots are cached; a failed refresh keeps the previous good data.
        if (!string.IsNullOrEmpty(snapshot.Error))
            return;

        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(new Envelope(Version, providerType, snapshot), JsonOptions);
            File.WriteAllText(PathFor(instanceId), json);
        }
        catch
        {
            // Last-known data is useful but non-critical; the next refresh recreates it.
        }
    }

    public void Delete(string instanceId)
    {
        try
        {
            var path = PathFor(instanceId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private string PathFor(string instanceId) =>
        Path.Combine(_directory, Sanitize(instanceId) + ".json");

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }
}
