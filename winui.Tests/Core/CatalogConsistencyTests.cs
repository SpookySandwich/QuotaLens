using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.Providers;
using QuotaLens.Services;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class CatalogConsistencyTests
{
    [TestMethod]
    public void MultiSourceProviders_UseOnlyUniqueAppCliWebModes()
    {
        CollectionAssert.AreEqual(
            new[] { "app", "cli", "web" },
            Enum.GetValues<ProviderSourceMode>().Select(mode => mode.ConfigValue()).ToArray());

        foreach (var type in Catalog.Types)
        {
            var modes = ProviderRegistry.Create(type.Id).Sources.Select(source => source.Mode).ToArray();
            Assert.AreEqual(
                modes.Length,
                modes.Distinct().Count(),
                $"{type.Id} declares a duplicate App/CLI/Web source mode.");
        }
    }

    [TestMethod]
    public void MultiSourceConfiguration_IsDeclaredBySourceAndReferencesEditableFields()
    {
        foreach (var type in Catalog.Types)
        {
            var sources = ProviderRegistry.Create(type.Id).Sources;
            if (sources.Count == 0)
                continue;

            var editable = Catalog.Fields.TryGetValue(type.Id, out var fields)
                ? fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                foreach (var key in source.ConfigFieldKeys)
                    Assert.IsTrue(editable.Contains(key), $"{type.Id}.{source.Mode.DisplayName()} references missing field {key}.");
            }
        }
    }

    [TestMethod]
    public void GlobalAppPathFields_AlwaysExposeAnAutomaticDefault()
    {
        foreach (var (providerType, fields) in Catalog.Fields)
        {
            foreach (var field in fields.Where(field => field.IsGlobal && field.IsFilePath))
            {
                Assert.IsTrue(Catalog.LaunchTargets.TryGetValue(providerType, out var target));
                Assert.AreEqual(field.Key, target!.ConfigKey);
                Assert.IsTrue(
                    target.DefaultPaths.Length > 0 || !string.IsNullOrWhiteSpace(target.PackageFamilyName),
                    $"{providerType}.{field.Key} has no auto-detectable default path.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(field.Placeholder));
            }
        }
    }

    [TestMethod]
    public void CatalogTypes_AreUniqueAndEveryTypeIsRegistered()
    {
        var ids = Catalog.Types.Select(type => type.Id).ToArray();
        CollectionAssert.AreEqual(
            ids,
            ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            "Provider type ids must be unique.");

        var registered = ProviderRegistry.RegisteredTypes.OrderBy(id => id).ToArray();
        var cataloged = ids.OrderBy(id => id).ToArray();
        CollectionAssert.AreEqual(cataloged, registered, "Provider registry and catalog types must match exactly.");

        foreach (var type in Catalog.Types)
        {
            var provider = ProviderRegistry.Create(type.Id);
            Assert.AreEqual(type.Id, provider.Type, $"Registry provider type mismatch for {type.Id}.");
        }
    }

    [TestMethod]
    public void CatalogTypes_IncludeCodexBarProviderSet()
    {
        var codexBarProviders = new[]
        {
            "abacus",
            "alibaba",
            "alibabatokenplan",
            "amp",
            "antigravity",
            "augment",
            "azureopenai",
            "bedrock",
            "claude",
            "codebuff",
            "codex",
            "commandcode",
            "copilot",
            "crof",
            "cursor",
            "deepgram",
            "deepseek",
            "doubao",
            "elevenlabs",
            "factory",
            "gemini",
            "grok",
            "groq",
            "jetbrains",
            "kilo",
            "kimi",
            "kimik2",
            "kiro",
            "llmproxy",
            "manus",
            "mimo",
            "minimax",
            "mistral",
            "moonshot",
            "ollama",
            "opencode",
            "opencodego",
            "openai",
            "openrouter",
            "perplexity",
            "stepfun",
            "synthetic",
            "t3chat",
            "venice",
            "vertexai",
            "warp",
            "windsurf",
            "zai",
        };
        var cataloged = Catalog.Types.Select(type => type.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = ProviderRegistry.RegisteredTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var providerType in codexBarProviders)
        {
            Assert.IsTrue(cataloged.Contains(providerType), $"{providerType} from CodexBar is missing from Catalog.Types.");
            Assert.IsTrue(registered.Contains(providerType), $"{providerType} from CodexBar is missing from ProviderRegistry.");
        }
    }

    [TestMethod]
    public void ProviderRegistry_WithUnknownType_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ProviderRegistry.Create("missing-provider"));
    }

    [TestMethod]
    public void ProviderRegistry_FactoryCategories_DoNotOverlap()
    {
        // Custom providers that intentionally wrap another factory category. Kimi wraps
        // the WebView flow. z.ai API is a plain SimpleApi provider; ZCode is a separate
        // token-plan provider. Any other overlap is a registration bug.
        var webWrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kimi" };
        var simpleWrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var custom = ProviderRegistry.CustomProviderTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var simple = SimpleApiProvider.SupportedTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var web = WebLoginService.SupportedTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customExceptWrappers = custom.Except(webWrappers).Except(simpleWrappers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(webWrappers.IsSubsetOf(custom), "web-wrapper providers must have a custom factory");
        Assert.IsTrue(webWrappers.IsSubsetOf(web), "web-wrapper providers must keep their WebView fallback");
        Assert.IsTrue(simpleWrappers.IsSubsetOf(custom), "simple-wrapper providers must have a custom factory");
        Assert.IsTrue(simpleWrappers.IsSubsetOf(simple), "simple-wrapper providers must keep their API-key flow");
        CollectionAssert.AreEquivalent(
            simpleWrappers.ToList(),
            custom.Intersect(simple).ToList(),
            "custom/simple overlap must be exactly the intentional simple wrappers");
        AssertDisjoint(customExceptWrappers, web, "custom", "WebView");
        AssertDisjoint(simple, web, "simple API", "WebView");
    }

    [TestMethod]
    public void CatalogTables_OnlyReferenceKnownProviderTypes()
    {
        var known = Catalog.Types.Select(type => type.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        AssertKnownKeys(known, Catalog.Fields.Keys, nameof(Catalog.Fields));
        AssertKnownKeys(known, Catalog.RequiredFields.Keys, nameof(Catalog.RequiredFields));
        AssertKnownKeys(known, Catalog.RequiredFieldSets.Keys, nameof(Catalog.RequiredFieldSets));
        AssertKnownKeys(known, Catalog.LaunchTargets.Keys, nameof(Catalog.LaunchTargets));
        AssertKnownKeys(known, Catalog.SubscriptionProviderTypes, nameof(Catalog.SubscriptionProviderTypes));
        AssertKnownKeys(known, Catalog.PayAsYouGoProviderTypes, nameof(Catalog.PayAsYouGoProviderTypes));
        AssertKnownKeys(known, Catalog.DefaultPlanValueRules.Keys, nameof(Catalog.DefaultPlanValueRules));
        AssertKnownKeys(known, Catalog.DefaultPlanTokenRules.Keys, nameof(Catalog.DefaultPlanTokenRules));
    }

    [TestMethod]
    public void CatalogFields_HaveDefaultConfigRows()
    {
        var missing = new List<string>();

        foreach (var (providerType, fields) in Catalog.Fields)
        {
            foreach (var field in fields)
            {
                if (!Catalog.DefaultConfig.ContainsKey(field.Key))
                    missing.Add($"{providerType}.{field.Key}");
            }
        }

        Assert.AreEqual(
            "",
            string.Join(Environment.NewLine, missing),
            "Every edit field needs a default config row.");
    }

    [TestMethod]
    public void DefaultConfig_IsDerivedFromEditableFieldsAndGlobalSettings()
    {
        var editableKeys = Catalog.Fields
            .SelectMany(pair => pair.Value)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedKeys = editableKeys
            .Concat(new[]
            {
                Catalog.DefaultLaunchEditorPathKey,
                "empty_threshold_pct",
                "deprioritize_empty_providers",
                "hide_sensitive_info",
                "sort_priority_order",
                "language",
            })
            .Concat(Catalog.LaunchTargets.Values
                .Select(target => target.ConfigKey)
                .Where(key => key is not null)
                .Select(key => key!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexpected = Catalog.DefaultConfig.Keys
            .Where(key => !expectedKeys.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), unexpected, "Default config should not retain stale provider keys.");
    }

    [TestMethod]
    public void DefaultConfig_PreservesFieldDefaultOverrides()
    {
        Assert.AreEqual("http://127.0.0.1:2455", Catalog.DefaultConfig["codex_lb_url"]);
        Assert.AreEqual("false", Catalog.DefaultConfig["show_other_quota_groups"]);
        Assert.AreEqual("", Catalog.DefaultConfig["deepseek_key"]);
        Assert.AreEqual("5", Catalog.DefaultConfig["empty_threshold_pct"]);
    }

    [TestMethod]
    public void RequiredFields_AreEditableAndHaveDefaults()
    {
        foreach (var (providerType, requiredKeys) in Catalog.RequiredFields)
        {
            Assert.IsTrue(Catalog.Fields.TryGetValue(providerType, out var fields), $"{providerType} has required fields but no edit fields.");
            var editableKeys = fields!.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in requiredKeys)
            {
                Assert.IsTrue(editableKeys.Contains(key), $"{providerType}.{key} is required but not editable.");
                Assert.IsTrue(Catalog.DefaultConfig.ContainsKey(key), $"{providerType}.{key} is required but has no default config row.");
            }
        }
    }

    [TestMethod]
    public void RequiredFieldSets_AreEditableSeededFields()
    {
        var failures = new List<string>();

        foreach (var (providerType, fieldSets) in Catalog.RequiredFieldSets)
        {
            if (!Catalog.RequiredFields.TryGetValue(providerType, out var seeded))
            {
                failures.Add($"{providerType}: missing RequiredFields seed list");
                continue;
            }

            var seededKeys = seeded.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var set in fieldSets)
            {
                if (set.AnyOf.Length == 0)
                {
                    failures.Add($"{providerType}: empty required field set");
                    continue;
                }

                foreach (var key in set.AnyOf)
                {
                    if (!seededKeys.Contains(key))
                        failures.Add($"{providerType}.{key}: required set key is not seeded");
                }
            }
        }

        Assert.AreEqual(
            "",
            string.Join(Environment.NewLine, failures),
            "Every required field set key must also be seeded as blank scoped config.");
    }

    [TestMethod]
    public void RequiredFields_AreDerivedFromRequiredFieldSets()
    {
        foreach (var (providerType, fieldSets) in Catalog.RequiredFieldSets)
        {
            var expected = fieldSets
                .SelectMany(set => set.AnyOf)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var actual = Catalog.RequiredFields[providerType]
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            CollectionAssert.AreEqual(expected, actual, $"{providerType} required seed keys should be derived from required field sets.");
        }
    }

    [TestMethod]
    public void RequiredFieldSets_AreDerivedFromRequiredProviderFields()
    {
        var expected = Catalog.Fields
            .Select(pair => new
            {
                ProviderType = pair.Key,
                Keys = pair.Value
                    .Where(field => field.IsRequired)
                    .Select(field => field.Key)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            })
            .Where(provider => provider.Keys.Length > 0)
            .ToDictionary(provider => provider.ProviderType, provider => provider.Keys, StringComparer.OrdinalIgnoreCase);

        var actual = Catalog.RequiredFieldSets
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .SelectMany(set => set.AnyOf)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        CollectionAssert.AreEqual(
            expected.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
            actual.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (var providerType in expected.Keys)
            CollectionAssert.AreEqual(expected[providerType], actual[providerType], $"{providerType} required fields should come from ProviderField.IsRequired.");
    }

    [TestMethod]
    public void ReadOnlyProviderConfiguration_DoesNotRequireInferenceCredentials()
    {
        var emptyConfig = new FakeConfig(new Dictionary<string, string>());
        var armConfig = new FakeConfig(new Dictionary<string, string>
        {
            ["azureopenai.azureopenai_subscription_id"] = "subscription-id",
            ["azureopenai.azureopenai_location"] = "eastus",
        });

        Assert.IsTrue(Catalog.IsProviderUnconfigured("azureopenai", emptyConfig));
        Assert.IsFalse(Catalog.IsProviderUnconfigured("azureopenai", armConfig));
        Assert.IsFalse(Catalog.IsProviderUnconfigured("doubao", emptyConfig));
        Assert.AreEqual(ProviderSetupKind.ApiKey, Catalog.SetupKindFor("azureopenai"));
        Assert.AreEqual(ProviderSetupKind.LocalAppOrCli, Catalog.SetupKindFor("doubao"));
        Assert.IsTrue(Catalog.LocalSetupProbes.ContainsKey("doubao"));
    }

    [TestMethod]
    public void LaunchTargets_UseKnownGlobalConfigKeys()
    {
        // Launch paths are global (one per provider type), so they only need a default
        // in the global config — not a per-instance editable field.
        foreach (var (providerType, target) in Catalog.LaunchTargets)
        {
            if (target.ConfigKey is null)
                continue;

            Assert.IsTrue(Catalog.DefaultConfig.ContainsKey(target.ConfigKey), $"{providerType} launch target config key has no global default.");
        }
    }

    [TestMethod]
    public void WebViewProviders_HaveSupportedLoginUrlFields()
    {
        var failures = new List<string>();
        var supported = WebLoginService.SupportedTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var type in Catalog.Types)
        {
            var provider = ProviderRegistry.Create(type.Id);
            var isWebViewProvider = provider is WebViewLoginProvider
                // CLI-first provider that falls back to the WebView login flow.
                || provider is KimiProvider
                || provider.SourceLabel.EndsWith(" WebView", StringComparison.Ordinal);

            if (!isWebViewProvider)
            {
                if (supported.Contains(type.Id))
                    failures.Add($"{type.Id}: WebLoginService supports it but registry does not create a WebView provider");
                continue;
            }

            var expectedUrlKey = type.Id + "_url";
            if (!WebLoginService.IsSupported(type.Id))
                failures.Add($"{type.Id}: unsupported by WebLoginService");
            if (provider.Name != type.Name)
                failures.Add($"{type.Id}: WebView provider name '{provider.Name}' does not match catalog '{type.Name}'");
            if (!Catalog.Fields.TryGetValue(type.Id, out var fields)
                || !fields.Any(field => field.Key == expectedUrlKey))
                failures.Add($"{type.Id}: missing editable {expectedUrlKey}");
            if (!Catalog.DefaultConfig.ContainsKey(expectedUrlKey))
                failures.Add($"{type.Id}: missing default {expectedUrlKey}");
            if (string.IsNullOrWhiteSpace(Catalog.DefaultLoginUrlFor(type.Id)))
                failures.Add($"{type.Id}: missing catalog default login URL");
        }

        foreach (var providerType in supported)
        {
            if (!Catalog.Types.Any(type => string.Equals(type.Id, providerType, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"{providerType}: WebLoginService supports unknown catalog provider");
        }

        Assert.AreEqual(
            "",
            string.Join(Environment.NewLine, failures),
            "Every WebView provider needs support, an editable URL, and a default URL row.");
    }

    [TestMethod]
    public void SimpleApiProviders_HaveRegistryAndRequiredConfigFields()
    {
        var wrapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = Catalog.Types.Select(type => type.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = ProviderRegistry.RegisteredTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var providerType in SimpleApiProvider.SupportedTypes)
        {
            if (!known.Contains(providerType))
                failures.Add($"{providerType}: missing catalog type");
            if (!registered.Contains(providerType))
                failures.Add($"{providerType}: missing registry entry");

            var configKey = SimpleApiProvider.ConfigKeyFor(providerType);
            if (!Catalog.DefaultConfig.ContainsKey(configKey))
                failures.Add($"{providerType}: missing default {configKey}");
            if (!Catalog.Fields.TryGetValue(providerType, out var fields) || !fields.Any(field => field.Key == configKey))
                failures.Add($"{providerType}: missing editable {configKey}");
            if (!wrapped.Contains(providerType)
                && (!Catalog.RequiredFields.TryGetValue(providerType, out var required) || !required.Contains(configKey)))
                failures.Add($"{providerType}: missing required field {configKey}");

            if (wrapped.Contains(providerType))
                continue;

            var provider = ProviderRegistry.Create(providerType);
            Assert.IsInstanceOfType(provider, typeof(SimpleApiProvider));
            if (provider.Name != Catalog.ProviderName(providerType))
                failures.Add($"{providerType}: simple API provider name '{provider.Name}' does not match catalog");
        }

        Assert.AreEqual(
            "",
            string.Join(Environment.NewLine, failures),
            "Every shared API provider needs registry, edit field, default config, and required config rows.");
    }

    [TestMethod]
    public void EveryAddableProvider_HasAConfigurationPage()
    {
        var missing = Catalog.AddableTypes
            .Where(type => !Catalog.Fields.TryGetValue(type.Id, out var fields) || fields.Length == 0)
            .Select(type => type.Id)
            .ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            "Every addable provider needs settings fields so Add/Edit open the same configuration page: "
            + string.Join(", ", missing));
    }

    [TestMethod]
    public void ProviderSetupKind_IsDerivedFromCatalogCapabilities()
    {
        Assert.AreEqual(ProviderSetupKind.ApiKey, Catalog.SetupKindFor("deepseek"));
        Assert.AreEqual(ProviderSetupKind.BrowserLogin, Catalog.SetupKindFor("kimi"));
        Assert.AreEqual(ProviderSetupKind.LocalAppOrCli, Catalog.SetupKindFor("qoder"));
        Assert.AreEqual(ProviderSetupKind.Ready, Catalog.SetupKindFor("test-provider"));
    }

    [TestMethod]
    public void LocalSetupProbes_OnlyReferenceKnownLocalProvidersAndEditableFields()
    {
        var known = Catalog.Types.Select(type => type.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (providerType, probe) in Catalog.LocalSetupProbes)
        {
            Assert.IsTrue(known.Contains(providerType), $"{providerType} local setup probe references an unknown provider.");
            Assert.AreEqual(ProviderSetupKind.LocalAppOrCli, Catalog.SetupKindFor(providerType), $"{providerType} probe should only be used for local setup providers.");
            Assert.IsTrue(Catalog.Fields.TryGetValue(providerType, out var fields), $"{providerType} should have editable fields.");
            var editableKeys = fields!.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var configKey in probe.ConfigKeys)
            {
                Assert.IsTrue(editableKeys.Contains(configKey), $"{providerType}.{configKey} should be an editable field.");
            }
        }
    }

    [TestMethod]
    public void BrowserLoginProviders_HaveBrowserLoginSetupKind()
    {
        foreach (var providerType in WebLoginService.SupportedTypes)
        {
            Assert.AreEqual(
                ProviderSetupKind.BrowserLogin,
                Catalog.SetupKindFor(providerType),
                $"{providerType} should be categorized as browser-login setup.");
        }
    }

    [TestMethod]
    public void SubscriptionProviders_HavePlanValueRules()
    {
        foreach (var providerType in Catalog.SubscriptionProviderTypes)
        {
            Assert.IsTrue(
                Catalog.DefaultPlanValueRules.ContainsKey(providerType),
                $"{providerType} is sortable as a subscription but has no default plan value rule row.");
        }
    }

    [TestMethod]
    public void OfficialDefaultPlanRules_HaveCompleteAuditableProvenance()
    {
        var failures = new List<string>();

        foreach (var (providerType, rules) in Catalog.DefaultPlanValueRules)
        {
            foreach (var rule in rules.Where(rule => rule.Evidence == ProviderPlanEvidence.Official))
            {
                var ruleName = $"{providerType}.{rule.Keyword}";
                if (string.IsNullOrWhiteSpace(rule.PlanId))
                    failures.Add($"{ruleName}: missing plan id");
                if (rule.PriceAmount is null || Math.Abs(rule.PriceAmount.Value - rule.Value) > 0.0001)
                    failures.Add($"{ruleName}: price amount must match the monthly sort value");
                if (string.IsNullOrWhiteSpace(rule.Currency))
                    failures.Add($"{ruleName}: missing currency");
                if (string.IsNullOrWhiteSpace(rule.Region))
                    failures.Add($"{ruleName}: missing region/storefront");
                if (string.IsNullOrWhiteSpace(rule.Cadence))
                    failures.Add($"{ruleName}: missing cadence");
                if (string.IsNullOrWhiteSpace(rule.SeatBasis))
                    failures.Add($"{ruleName}: missing seat basis");
                if (!Uri.TryCreate(rule.OfficialSource, UriKind.Absolute, out var source)
                    || source.Scheme != Uri.UriSchemeHttps)
                    failures.Add($"{ruleName}: missing HTTPS official source");
                if (!DateOnly.TryParse(rule.LastVerifiedAt, out _))
                    failures.Add($"{ruleName}: missing verification date");
            }
        }

        Assert.AreEqual(
            "",
            string.Join(Environment.NewLine, failures),
            "Official plan rules must remain independently auditable.");
    }

    [TestMethod]
    [DataRow("codex", "Codex · Go", 8)]
    [DataRow("codex", "Codex · Pro 20x", 200)]
    [DataRow("copilot", "Copilot · Max", 100)]
    [DataRow("claude", "Claude Code · Team Premium", 125)]
    [DataRow("cursor", "Cursor · Pro+", 60)]
    [DataRow("cursor", "Cursor · Teams Premium", 120)]
    [DataRow("augment", "Augment · Business", 100)]
    [DataRow("factory", "Factory · Plus", 100)]
    [DataRow("elevenlabs", "ElevenLabs · Business", 990)]
    [DataRow("warp", "Warp · Build", 20)]
    [DataRow("kilo", "Kilo · Kilo Pass Pro", 49)]
    [DataRow("ollama", "Ollama · Max", 100)]
    [DataRow("amp", "Amp · Gigawatt", 200)]
    [DataRow("minimax", "MiniMax · Max", 50)]
    [DataRow("mimo", "MiMo · Standard", 16)]
    [DataRow("abacus", "Abacus AI · Basic", 10)]
    [DataRow("opencodego", "OpenCode Go", 10)]
    public void CurrentOfficialPlanPrices_MatchSpecificAliasesBeforeGenericRules(
        string providerType,
        string planName,
        double expectedValue)
    {
        var actual = PlanValueRules.Match(providerType, planName);

        Assert.IsNotNull(actual);
        Assert.AreEqual(expectedValue, actual.Value, 0.001);
    }

    [TestMethod]
    [DataRow("augment", "Augment · Pro")]
    [DataRow("factory", "Factory · Starter")]
    [DataRow("warp", "Warp · Turbo")]
    [DataRow("opencode", "OpenCode · Pro")]
    [DataRow("abacus", "Abacus AI · Enterprise")]
    public void RetiredOrUnsupportedPublicPlanClaims_HaveNoDefaultValue(
        string providerType,
        string planName)
    {
        Assert.IsNull(PlanValueRules.Match(providerType, planName));
    }

    [TestMethod]
    public void CatalogTypes_HaveExplicitDefaultPlanValueRows()
    {
        foreach (var type in Catalog.Types)
        {
            Assert.IsTrue(
                Catalog.DefaultPlanValueRules.ContainsKey(type.Id),
                $"{type.Id} has no default plan value row. Use an empty array for subscription providers with unknown value, or a negative value for pay-as-you-go providers.");
        }
    }

    [TestMethod]
    public void CostModelProviderSets_AreDerivedFromDefaultPlanValueRules()
    {
        var expectedPayAsYouGo = Catalog.DefaultPlanValueRules
            .Where(pair => !Catalog.RetiredProviderTypes.Contains(pair.Key)
                && pair.Value.Any(rule => rule.Value < 0))
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actualPayAsYouGo = Catalog.PayAsYouGoProviderTypes
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expectedSubscriptions = Catalog.DefaultPlanValueRules
            .Where(pair => !Catalog.RetiredProviderTypes.Contains(pair.Key)
                && pair.Value.All(rule => rule.Value >= 0))
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actualSubscriptions = Catalog.SubscriptionProviderTypes
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        CollectionAssert.AreEqual(expectedPayAsYouGo, actualPayAsYouGo, "Pay-as-you-go providers should be derived from negative default plan-value rules.");
        CollectionAssert.AreEqual(expectedSubscriptions, actualSubscriptions, "Subscription providers should be derived from non-negative default plan-value rules.");
    }

    [TestMethod]
    public void PayAsYouGoProviders_HaveNegativeDefaultPlanValueRules()
    {
        foreach (var providerType in Catalog.PayAsYouGoProviderTypes)
        {
            Assert.IsFalse(
                Catalog.SubscriptionProviderTypes.Contains(providerType),
                $"{providerType} cannot be both pay-as-you-go and an editable subscription plan-value provider.");

            Assert.IsTrue(
                Catalog.DefaultPlanValueRules.TryGetValue(providerType, out var rules),
                $"{providerType} is pay-as-you-go but has no default plan value rule row.");

            Assert.IsTrue(
                rules!.Any(rule => rule.Value < 0),
                $"{providerType} is pay-as-you-go but has no negative default plan value rule.");
        }
    }

    [TestMethod]
    public void CatalogTypes_HaveExplicitBrandIdentity()
    {
        var fallback = Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x7B, 0x8A);

        foreach (var type in Catalog.Types)
        {
            var color = Brand.Color(type.Id);
            Assert.AreNotEqual(fallback, color, $"{type.Id} is using the fallback brand color.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(Brand.Monogram(type.Id)), $"{type.Id} has no monogram.");
        }
    }

    private static void AssertKnownKeys(
        IReadOnlySet<string> knownTypes,
        IEnumerable<string> keys,
        string tableName)
    {
        foreach (var key in keys)
            Assert.IsTrue(knownTypes.Contains(key), $"{tableName} references unknown provider type {key}.");
    }

    private static void AssertDisjoint(
        IReadOnlySet<string> left,
        IReadOnlySet<string> right,
        string leftName,
        string rightName)
    {
        var overlap = left.Intersect(right, StringComparer.OrdinalIgnoreCase).OrderBy(id => id).ToArray();
        Assert.AreEqual("", string.Join(", ", overlap), $"{leftName} and {rightName} provider factories must not overlap.");
    }

    private sealed class FakeConfig(IReadOnlyDictionary<string, string> values) : IConfig
    {
        public string Get(string key, string fallback = "") =>
            values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            values.TryGetValue($"{instanceId}.{key}", out var value) ? value : fallback;

        public bool HasScoped(string instanceId, string key) =>
            values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) =>
            values.TryGetValue(key, out var value)
                ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                : fallback;
    }
}
