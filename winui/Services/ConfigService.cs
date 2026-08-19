using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Services;

/// <summary>
/// Native config layer for the WinUI port. Persists the flat string→string config
/// map and the explicit list of provider instances under %LOCALAPPDATA%\QuotaLens,
/// merging with <see cref="Catalog.DefaultConfig"/> on load. On first run
/// (no config.json) it performs a one-time migration from the old Tauri WebView2
/// localStorage via <see cref="TauriConfigMigration"/> when legacy data exists.
///
/// Implements <see cref="IConfig"/> so providers can read scoped per-instance keys
/// (mirrors the original frontend's scopedConfigKey read-with-fallback semantics).
/// </summary>
public sealed class ConfigService : IConfigService
{
    private const string ConfigFileName = "config.json";
    private const string InstancesFileName = "instances.json";
    private const string InstancesExplicitConfigKey = "provider_instances_explicit";
    private const string ScopedProviderConfigMigrationKey = "provider_scoped_config_v2";
    private const string InternalProviderAliasesMigrationKey = "provider_internal_aliases_v1";
    private const string ConfigKeyAliasesMigrationKey = "config_key_aliases_v1";
    private const string ZcodeSplitMigrationKey = "provider_zcode_split_v1";

    /// Default refresh interval when min_refresh_interval_secs is unset (seconds).
    private const int DefaultRefreshSecs = 1800;
    /// Hard floor on the refresh interval (milliseconds). Mirrors Math.max(30_000, ...).
    private const double RefreshFloorMs = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _dir;
    private readonly string _configPath;
    private readonly string _instancesPath;
    private readonly Func<TauriConfigMigration.Result?> _loadTauriMigration;

    private readonly Dictionary<string, string> _config = new();
    private readonly List<ProviderInstance> _instances = new();

    /// <summary>Creates a service backed by %LOCALAPPDATA%\QuotaLens.</summary>
    public ConfigService() : this(DefaultDir) { }

    /// <summary>Testable overload backed by an explicit directory.</summary>
    public ConfigService(string directory)
        : this(directory, TauriConfigMigration.TryLoad)
    {
    }

    internal ConfigService(string directory, Func<TauriConfigMigration.Result?> loadTauriMigration)
    {
        _dir = directory;
        _configPath = Path.Combine(_dir, ConfigFileName);
        _instancesPath = Path.Combine(_dir, InstancesFileName);
        _loadTauriMigration = loadTauriMigration;
        Load();
    }

    private static string DefaultDir
    {
        get
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            return Path.Combine(localAppData, "QuotaLens");
        }
    }

    // -- IConfig -----------------------------------------------------------

    public string Get(string key, string fallback = "")
        => _config.TryGetValue(key, out var v) ? v : fallback;

    public string GetScoped(string instanceId, string key, string fallback = "")
        => _config.TryGetValue(ScopedKey(instanceId, key), out var scoped) ? scoped : fallback;

    public bool HasScoped(string instanceId, string key)
        => _config.ContainsKey(ScopedKey(instanceId, key));

    public bool GetBool(string key, bool fallback = false)
    {
        if (!_config.TryGetValue(key, out var v))
            return fallback;
        return v.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            _ => false,
        };
    }

    // -- IConfigService ----------------------------------------------------

    public IReadOnlyDictionary<string, string> All => _config;

    public void Set(string key, string value)
    {
        // The original backend trims values on store (set_config); match that so
        // providers see the same trimmed values.
        _config[key] = value?.Trim() ?? "";
    }

    public void SetMany(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            Set(key, value);
    }

    public void Remove(string key)
    {
        _config.Remove(key);
    }


    public async Task SaveAsync()
    {
        Directory.CreateDirectory(_dir);
        _config[InstancesExplicitConfigKey] = "true";

        var configJson = JsonSerializer.Serialize(_config, JsonOptions);
        await File.WriteAllTextAsync(_configPath, configJson).ConfigureAwait(false);

        var instancesJson = JsonSerializer.Serialize(_instances, JsonOptions);
        await File.WriteAllTextAsync(_instancesPath, instancesJson).ConfigureAwait(false);
    }

    public IReadOnlyList<ProviderInstance> Instances => _instances;

    public ProviderInstance AddInstance(string providerType)
    {
        var type = Catalog.FindType(providerType)
            ?? throw new ArgumentException($"Unknown provider type: {providerType}", nameof(providerType));

        // Mirror the Rust add_instance: id = "{type}-{first 8 hex of a UUID v4}".
        var shortGuid = Guid.NewGuid().ToString("N").Substring(0, 8);
        var id = $"{type.Id}-{shortGuid}";

        var instance = new ProviderInstance(id, type.Id, type.Name);
        _instances.Add(instance);
        SeedBlankRequiredScopedConfig(instance);
        Persist();
        return instance;
    }

    public void RemoveInstance(string id)
    {
        var instance = _instances.FirstOrDefault(i => i.Id == id);
        // Mirror the Rust remove_instance: error if the id is unknown.
        if (instance == null)
            throw new InvalidOperationException($"Instance not found: {id}");

        _instances.Remove(instance);
        RemoveScopedConfig(instance.Id);
        Persist();
    }

    public double RefreshMs
    {
        get
        {
            // Mirror: Math.max(30_000, (parseInt(cfg || "1800") || 1800) * 1000).
            // JS `||` treats 0 (and NaN) as falsy → fall back to 1800; any other
            // value (incl. negatives) is used as-is and then floored at 30_000.
            var secs = DefaultRefreshSecs;
            if (_config.TryGetValue("min_refresh_interval_secs", out var raw) &&
                int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed != 0)
            {
                secs = parsed;
            }
            return Math.Max(RefreshFloorMs, (double)secs * 1000);
        }
    }

    // -- Loading & persistence --------------------------------------------

    private void Load()
    {
        var configExisted = File.Exists(_configPath);
        Dictionary<string, string>? stored = null;

        // 1. Seed defaults.
        foreach (var (key, value) in Catalog.DefaultConfig)
            _config[key] = value;

        TauriConfigMigration.Result? migrated = null;
        if (!configExisted)
        {
            // First run: try the one-time migration from the old Tauri store.
            migrated = _loadTauriMigration();
            if (migrated != null)
            {
                foreach (var (key, value) in migrated.Config)
                    _config[key] = value;
            }
        }
        else
        {
            // 2. Merge persisted config over the defaults.
            stored = LoadConfigFile();
            if (stored != null)
            {
                foreach (var (key, value) in stored)
                    _config[key] = value;
            }
        }

        // 3. Provider instances are now explicit user data. Existing installs used
        // implicit catalog defaults, so migrate those once and mark the store.
        var loadedInstances = !configExisted && migrated != null
            ? migrated.Extras
            : LoadInstancesFile();
        var hasExplicitInstanceStore = IsTrue(stored?.GetValueOrDefault(InstancesExplicitConfigKey));
        var shouldMigrateImplicitDefaults = migrated != null || (configExisted && !hasExplicitInstanceStore);

        if (shouldMigrateImplicitDefaults)
            AddCatalogInstances();

        AddInstances(loadedInstances);

        var shouldMigrateInternalProviderAliases = !IsTrue(_config.GetValueOrDefault(InternalProviderAliasesMigrationKey));
        var migratedInternalProviderAliases = shouldMigrateInternalProviderAliases && MigrateInternalProviderAliases();

        var shouldMigrateZcodeSplit = !IsTrue(_config.GetValueOrDefault(ZcodeSplitMigrationKey));
        var migratedZcodeSplit = shouldMigrateZcodeSplit && MigrateZcodeSplit();

        var loadedConfig = (IReadOnlyDictionary<string, string>?)stored ?? migrated?.Config;
        var shouldMigrateConfigKeyAliases = !IsTrue(_config.GetValueOrDefault(ConfigKeyAliasesMigrationKey));
        var migratedConfigKeyAliases = shouldMigrateConfigKeyAliases && MigrateConfigKeyAliases(loadedConfig);

        var shouldMigrateScopedProviderConfig = !IsTrue(_config.GetValueOrDefault(ScopedProviderConfigMigrationKey));
        if (shouldMigrateScopedProviderConfig)
            MigrateLegacyBareProviderConfigToScoped();

        foreach (var instance in _instances)
            SeedBlankRequiredScopedConfig(instance);

        // 4. Persist first-run stores and one-time migrations so future launches do
        // not recreate catalog instances after the user removes them.
        if (shouldMigrateInternalProviderAliases)
            _config[InternalProviderAliasesMigrationKey] = "true";
        if (shouldMigrateConfigKeyAliases)
            _config[ConfigKeyAliasesMigrationKey] = "true";
        if (shouldMigrateZcodeSplit)
            _config[ZcodeSplitMigrationKey] = "true";

        if (!configExisted
            || shouldMigrateImplicitDefaults
            || migratedInternalProviderAliases
            || migratedConfigKeyAliases
            || migratedZcodeSplit
            || shouldMigrateScopedProviderConfig
            || shouldMigrateInternalProviderAliases
            || shouldMigrateConfigKeyAliases
            || shouldMigrateZcodeSplit)
        {
            _config[InstancesExplicitConfigKey] = "true";
            _config[ScopedProviderConfigMigrationKey] = "true";
            Persist();
        }
    }

    private Dictionary<string, string>? LoadConfigFile()
    {
        try
        {
            var json = File.ReadAllText(_configPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return dict;
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyList<ProviderInstance> LoadInstancesFile()
    {
        try
        {
            if (!File.Exists(_instancesPath))
                return Array.Empty<ProviderInstance>();
            var json = File.ReadAllText(_instancesPath);
            var list = JsonSerializer.Deserialize<List<ProviderInstance>>(json);
            return (IReadOnlyList<ProviderInstance>?)list ?? Array.Empty<ProviderInstance>();
        }
        catch
        {
            return Array.Empty<ProviderInstance>();
        }
    }

    private void AddCatalogInstances()
    {
        foreach (var type in Catalog.AddableTypes)
            AddInstanceIfMissing(new ProviderInstance(type.Id, type.Id, type.Name));
    }

    private void AddInstances(IEnumerable<ProviderInstance> instances)
    {
        foreach (var instance in instances)
            AddInstanceIfMissing(instance);
    }

    private void AddInstanceIfMissing(ProviderInstance instance)
    {
        var type = Catalog.FindType(instance.Type);
        if (!ProviderInstanceIdentity.IsValid(instance.Id)
            || string.IsNullOrWhiteSpace(instance.Type)
            || type is null
            || _instances.Any(i => string.Equals(i.Id, instance.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var normalized = new ProviderInstance(
            instance.Id,
            type.Id,
            string.IsNullOrWhiteSpace(instance.Name) ? type.Name : instance.Name);
        _instances.Add(normalized);
    }

    private bool MigrateInternalProviderAliases()
    {
        var internalInstances = _instances
            .Where(instance => Catalog.IsInternalProviderType(instance.Type))
            .ToArray();
        if (internalInstances.Length == 0)
            return false;

        var firstInternalIndex = _instances.IndexOf(internalInstances[0]);
        var hasAlibaba = _instances.Any(instance =>
            string.Equals(instance.Type, "alibaba", StringComparison.OrdinalIgnoreCase));

        foreach (var instance in internalInstances)
            _instances.Remove(instance);

        if (!hasAlibaba)
        {
            var alibaba = Catalog.FindType("alibaba");
            if (alibaba != null)
                _instances.Insert(Math.Clamp(firstInternalIndex, 0, _instances.Count), new ProviderInstance(alibaba.Id, alibaba.Id, alibaba.Name));
        }

        return true;
    }

    private bool MigrateZcodeSplit()
    {
        if (_instances.Any(instance => string.Equals(instance.Type, "zcode", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!_instances.Any(instance => string.Equals(instance.Type, "zai", StringComparison.OrdinalIgnoreCase)))
            return false;

        var zcode = Catalog.FindType("zcode");
        if (zcode is null)
            return false;

        AddInstanceIfMissing(new ProviderInstance(zcode.Id, zcode.Id, zcode.Name));
        return true;
    }

    private void SeedBlankRequiredScopedConfig(ProviderInstance instance)
    {
        if (!Catalog.RequiredFields.TryGetValue(instance.Type, out var required))
            return;

        foreach (var key in required)
        {
            var scopedKey = ScopedKey(instance.Id, key);
            if (!_config.ContainsKey(scopedKey))
                _config[scopedKey] = "";
        }
    }

    private void MigrateLegacyBareProviderConfigToScoped()
    {
        foreach (var instance in _instances)
        {
            if (!string.Equals(instance.Id, instance.Type, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Catalog.Fields.TryGetValue(instance.Type, out var fields))
                continue;

            foreach (var key in fields.Select(field => field.Key).Distinct(StringComparer.OrdinalIgnoreCase))
                MigrateLegacyBareKey(instance.Id, key);
        }

        _config[ScopedProviderConfigMigrationKey] = "true";
    }

    private bool MigrateConfigKeyAliases(IReadOnlyDictionary<string, string>? loadedConfig)
    {
        var changed = false;
        foreach (var key in _config.Keys.ToArray())
        {
            foreach (var (oldKey, newKey) in Catalog.ConfigKeyAliases)
            {
                var isBare = string.Equals(key, oldKey, StringComparison.OrdinalIgnoreCase);
                var suffix = "." + oldKey;
                if (!isBare && !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var prefixLength = isBare ? 0 : key.Length - oldKey.Length;
                var replacement = key[..prefixLength] + newKey;
                var replacementWasExplicit = loadedConfig?.ContainsKey(replacement) == true;
                if (!replacementWasExplicit)
                    _config[replacement] = _config[key];

                _config.Remove(key);
                changed = true;
                break;
            }
        }

        return changed;
    }

    private void MigrateLegacyBareKey(string instanceId, string key)
    {
        if (!_config.TryGetValue(key, out var value))
            return;

        var defaultValue = Catalog.DefaultConfig.TryGetValue(key, out var catalogDefault)
            ? catalogDefault
            : "";
        var hasLegacyValue = !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, defaultValue, StringComparison.Ordinal);
        var scopedKey = ScopedKey(instanceId, key);

        if (hasLegacyValue && !_config.ContainsKey(scopedKey))
            _config[scopedKey] = value;

        if (Catalog.DefaultConfig.ContainsKey(key))
            _config[key] = defaultValue;
        else
            _config.Remove(key);
    }

    private void RemoveScopedConfig(string instanceId)
    {
        var prefix = instanceId + ".";
        foreach (var key in _config.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            _config.Remove(key);
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || value == "1"
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static string ScopedKey(string instanceId, string key) => $"{instanceId}.{key}";

    /// <summary>Synchronous best-effort persist used by mutating operations.</summary>
    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            _config[InstancesExplicitConfigKey] = "true";
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions));
            File.WriteAllText(_instancesPath, JsonSerializer.Serialize(_instances, JsonOptions));
        }
        catch
        {
            // Persistence failures must not crash the app; SaveAsync can retry later.
        }
    }
}
