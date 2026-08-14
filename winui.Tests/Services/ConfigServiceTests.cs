using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class ConfigServiceTests
{
    [TestMethod]
    public void FreshInstall_WithNoLegacyMigration_StartsWithNoProviderInstances()
    {
        var dir = NewTempDir();

        try
        {
            var config = new ConfigService(dir, () => null);

            Assert.AreEqual(0, config.Instances.Count);
            Assert.AreEqual("true", config.Get("provider_instances_explicit"));
            Assert.AreEqual(0, ReadInstances(dir).Count);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void ExistingWinUiInstallWithoutExplicitInstanceMarker_MigratesImplicitCatalogInstances()
    {
        var dir = NewTempDir();

        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "instances.json"), "[]");

            var config = new ConfigService(dir, () => null);

            Assert.AreEqual(Catalog.AddableTypes.Count, config.Instances.Count);
            CollectionAssert.AreEqual(
                Catalog.AddableTypes.Select(type => type.Id).ToArray(),
                config.Instances.Select(instance => instance.Id).ToArray());
            Assert.AreEqual("true", config.Get("provider_instances_explicit"));
            Assert.AreEqual(Catalog.AddableTypes.Count, ReadInstances(dir).Count);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void Load_WithLegacyBareProviderKey_MigratesKeyToDefaultInstanceOnly()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
                ["deepseek_key"] = "sk-legacy",
            });
            WriteInstances(dir, new[]
            {
                new ProviderInstance("deepseek", "deepseek", "DeepSeek"),
            });

            var config = new ConfigService(dir, () => null);
            var extra = config.AddInstance("deepseek");

            Assert.AreEqual("sk-legacy", config.GetScoped("deepseek", "deepseek_key"));
            Assert.AreEqual("", config.GetScoped(extra.Id, "deepseek_key"));
            Assert.AreEqual("", config.Get("deepseek_key"));
            Assert.AreEqual("true", config.Get("provider_scoped_config_v2"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void RemoveInstance_RemovesScopedConfigForThatInstance()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
                ["provider_scoped_config_v2"] = "true",
                ["deepseek-one.deepseek_key"] = "sk-one",
                ["deepseek-two.deepseek_key"] = "sk-two",
            });
            WriteInstances(dir, new[]
            {
                new ProviderInstance("deepseek-one", "deepseek", "DeepSeek One"),
                new ProviderInstance("deepseek-two", "deepseek", "DeepSeek Two"),
            });
            var config = new ConfigService(dir, () => null);

            config.RemoveInstance("deepseek-one");

            Assert.AreEqual("", config.GetScoped("deepseek-one", "deepseek_key"));
            Assert.AreEqual("sk-two", config.GetScoped("deepseek-two", "deepseek_key"));
            Assert.IsFalse(ReadConfig(dir).ContainsKey("deepseek-one.deepseek_key"));
            Assert.IsTrue(ReadConfig(dir).ContainsKey("deepseek-two.deepseek_key"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public async Task Remove_ConfigKey_RemovesPersistedOverride()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
                ["plan_value_rules.claude"] = "pro=21",
            });
            var config = new ConfigService(dir, () => null);

            config.Remove("plan_value_rules.claude");
            await config.SaveAsync();

            Assert.AreEqual("", config.Get("plan_value_rules.claude"));
            Assert.IsFalse(ReadConfig(dir).ContainsKey("plan_value_rules.claude"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void GetScoped_WithBareProviderKey_DoesNotFallbackToBareKey()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
                ["provider_scoped_config_v2"] = "true",
                ["deepseek_key"] = "sk-bare",
            });
            WriteInstances(dir, Array.Empty<ProviderInstance>());

            var config = new ConfigService(dir, () => null);

            Assert.AreEqual("", config.GetScoped("deepseek-new", "deepseek_key"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void AddInstance_WithRequiredProviderFields_SeedsBlankScopedConfig()
    {
        var dir = NewTempDir();

        try
        {
            var config = new ConfigService(dir, () => null);

            foreach (var providerType in Catalog.RequiredFields.Keys)
            {
                var instance = config.AddInstance(providerType);

                foreach (var key in Catalog.RequiredFields[providerType])
                {
                    Assert.IsTrue(config.HasScoped(instance.Id, key), $"{providerType}.{key} should be scoped when the instance is added.");
                    Assert.AreEqual("", config.GetScoped(instance.Id, key), $"{providerType}.{key} should be seeded blank.");
                    Assert.IsTrue(ReadConfig(dir).ContainsKey($"{instance.Id}.{key}"));
                }
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void AddInstance_WithUnknownProviderType_Throws()
    {
        var dir = NewTempDir();

        try
        {
            var config = new ConfigService(dir, () => null);

            Assert.ThrowsExactly<ArgumentException>(() => config.AddInstance("missing-provider"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void LoadInstances_SkipsUnknownProviderTypes()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
                ["provider_scoped_config_v2"] = "true",
            });
            WriteInstances(dir, new[]
            {
                new ProviderInstance("known", "deepseek", "DeepSeek"),
                new ProviderInstance("unknown", "missing-provider", "Missing"),
            });

            var config = new ConfigService(dir, () => null);

            Assert.AreEqual(1, config.Instances.Count);
            Assert.AreEqual("known", config.Instances[0].Id);
            Assert.AreEqual("deepseek", config.Instances[0].Type);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void LoadInstances_NormalizesProviderTypeAndSeedsRequiredFields()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
                ["provider_scoped_config_v2"] = "true",
            });
            WriteInstances(dir, new[]
            {
                new ProviderInstance("work", "DeepSeek", ""),
            });

            var config = new ConfigService(dir, () => null);

            Assert.AreEqual(1, config.Instances.Count);
            Assert.AreEqual("work", config.Instances[0].Id);
            Assert.AreEqual("deepseek", config.Instances[0].Type);
            Assert.AreEqual("DeepSeek", config.Instances[0].Name);
            Assert.IsTrue(config.HasScoped("work", "deepseek_key"));
            Assert.AreEqual("", config.GetScoped("work", "deepseek_key"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void ExistingExplicitEmptyInstanceStore_RemainsEmpty()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
            });
            File.WriteAllText(Path.Combine(dir, "instances.json"), "[]");

            var config = new ConfigService(dir, () => null);

            Assert.AreEqual(0, config.Instances.Count);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void ExistingWinUiInstallWithOldExtraInstances_MigratesCatalogInstancesAndExtras()
    {
        var dir = NewTempDir();

        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
            WriteInstances(dir, new[]
            {
                new ProviderInstance("claude-extra", "claude", "Claude Extra"),
            });

            var config = new ConfigService(dir, () => null);

            Assert.AreEqual(Catalog.AddableTypes.Count + 1, config.Instances.Count);
            Assert.IsTrue(config.Instances.Any(instance => instance.Id == "claude"));
            Assert.IsTrue(config.Instances.Any(instance => instance.Id == "claude-extra"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void RemoveInstance_AllowsRemovingLastInstanceOfType()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
            });
            WriteInstances(dir, new[]
            {
                new ProviderInstance("claude-one", "claude", "Claude"),
            });
            var config = new ConfigService(dir, () => null);

            config.RemoveInstance("claude-one");

            Assert.AreEqual(0, config.Instances.Count);
            Assert.AreEqual(0, ReadInstances(dir).Count);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void LoadInstances_WithLegacyAlibabaAliases_CollapsesToSingleAlibaba()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
                ["provider_scoped_config_v2"] = "true",
            });
            WriteInstances(dir, new[]
            {
                new ProviderInstance("alibaba-main", "alibaba", "Alibaba"),
                new ProviderInstance("alibabacloud-old", "alibabacloud", "Alibaba Cloud"),
                new ProviderInstance("alibabatokenplan-old", "alibabatokenplan", "Alibaba Token Plan"),
                new ProviderInstance("deepseek-main", "deepseek", "DeepSeek"),
            });

            var config = new ConfigService(dir, () => null);

            Assert.AreEqual(2, config.Instances.Count);
            Assert.IsTrue(config.Instances.Any(instance => instance.Id == "alibaba-main" && instance.Type == "alibaba"));
            Assert.IsTrue(config.Instances.Any(instance => instance.Id == "deepseek-main" && instance.Type == "deepseek"));
            Assert.IsFalse(config.Instances.Any(instance => Catalog.IsInternalProviderType(instance.Type)));
            Assert.AreEqual("true", config.Get("provider_internal_aliases_v1"));
            Assert.IsFalse(ReadInstances(dir).Any(instance => Catalog.IsInternalProviderType(instance.Type)));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void LoadInstances_WithOnlyLegacyAlibabaAlias_KeepsOneAlibabaCard()
    {
        var dir = NewTempDir();

        try
        {
            WriteConfig(dir, new Dictionary<string, string>
            {
                ["provider_instances_explicit"] = "true",
                ["provider_scoped_config_v2"] = "true",
            });
            WriteInstances(dir, new[]
            {
                new ProviderInstance("alibabatokenplan-old", "alibabatokenplan", "Alibaba Token Plan"),
            });

            var config = new ConfigService(dir, () => null);

            Assert.AreEqual(1, config.Instances.Count);
            Assert.AreEqual("alibaba", config.Instances[0].Id);
            Assert.AreEqual("alibaba", config.Instances[0].Type);
            Assert.AreEqual("Alibaba", config.Instances[0].Name);
            Assert.AreEqual("true", config.Get("provider_internal_aliases_v1"));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [TestMethod]
    public void ProviderTypeFromId_WithHyphenatedGeneratedInstanceId_ReturnsFullProviderType()
    {
        Assert.AreEqual("codex-lb", Catalog.ProviderTypeFromId("codex-lb-1234abcd"));
    }



    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static void WriteConfig(string dir, Dictionary<string, string> config)
    {
        File.WriteAllText(Path.Combine(dir, "config.json"), JsonSerializer.Serialize(config));
    }

    private static Dictionary<string, string> ReadConfig(string dir)
    {
        var path = Path.Combine(dir, "config.json");
        var json = File.Exists(path) ? File.ReadAllText(path) : "{}";
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private static void WriteInstances(string dir, IEnumerable<ProviderInstance> instances)
    {
        File.WriteAllText(Path.Combine(dir, "instances.json"), JsonSerializer.Serialize(instances));
    }

    private static List<ProviderInstance> ReadInstances(string dir)
    {
        var path = Path.Combine(dir, "instances.json");
        var json = File.Exists(path) ? File.ReadAllText(path) : "[]";
        return JsonSerializer.Deserialize<List<ProviderInstance>>(json) ?? new List<ProviderInstance>();
    }
}
