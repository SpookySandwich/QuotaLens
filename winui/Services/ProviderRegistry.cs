using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Services;

/// <summary>Creates the single IProvider for a given provider type.</summary>
public static class ProviderRegistry
{
    private static readonly IReadOnlyDictionary<string, Func<IProvider>> CustomFactories =
        new Dictionary<string, Func<IProvider>>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex-lb"] = () => new CodexLbProvider(),
            ["codex"] = () => new CodexProvider(),
            ["gemini"] = () => new GeminiProvider(),
            ["bedrock"] = () => new BedrockProvider(),
            ["vertexai"] = () => new VertexAIProvider(),
            ["claude"] = () => new ClaudeProvider(),
            ["deepseek"] = () => new DeepSeekProvider(),
            ["kiro"] = () => new KiroProvider(),
            ["alibabacloud"] = () => new AlibabaProvider(),
            ["antigravity"] = () => new RetiredProvider(
                "antigravity",
                "Provider retired: Antigravity is now a data source under Gemini. Remove this card and use Gemini."),
            ["qoder"] = () => new QoderProvider(),
            ["azureopenai"] = () => new AzureOpenAIProvider(),
            ["doubao"] = () => new DoubaoProvider(),
            ["groq"] = () => new GroqProvider(),
            ["deepgram"] = () => new DeepgramProvider(),
            ["grok"] = () => new GrokProvider(),
            ["kilo"] = () => new KiloProvider(),
            ["jetbrains"] = () => new JetBrainsProvider(),
            ["kimi"] = () => new KimiProvider(),
            ["zcode"] = () => new ZcodeProvider(),
            ["kimik2"] = () => new RetiredProvider(
                "kimik2",
                "Provider retired: Kimi K2 used an unverified credential relay. Remove this card, use Kimi or Moonshot, and rotate any credential previously sent to that relay."),
        };

    private static readonly IReadOnlyDictionary<string, Func<IProvider>> Factories = BuildFactories();

    public static IReadOnlyCollection<string> RegisteredTypes => Factories.Keys.ToArray();

    internal static IReadOnlyCollection<string> CustomProviderTypes => CustomFactories.Keys.ToArray();

    public static IProvider Create(string type)
    {
        if (Factories.TryGetValue(type, out var factory))
            return factory();

        throw new ArgumentException($"Unknown provider type: {type}");
    }

    /// <summary>
    /// Older single-source CLI providers keep their fetch implementations unchanged,
    /// but receive the same source-owned connection contract as native multi-source
    /// providers. This is the compatibility boundary; the dialog never consults the
    /// provider login catalog or provider IDs.
    /// </summary>
    public static IReadOnlyList<IProviderSource> ConnectionSourcesFor(IProvider provider)
    {
        if (provider.Sources.Count > 0)
            return provider.Sources;
        if (!ProviderLoginLauncher.IsSupported(provider.Type))
            return Array.Empty<IProviderSource>();

        var fieldKeys = Catalog.Fields.TryGetValue(provider.Type, out var fields)
            ? fields.Select(field => field.Key).ToArray()
            : Array.Empty<string>();
        return new IProviderSource[]
        {
            new ProviderSource(
                ProviderSourceMode.Cli,
                (_, _) => true,
                provider.FetchAsync,
                configFieldKeys: fieldKeys,
                connectionAction: new CliProviderConnectionAction(provider.Type)),
        };
    }

    /// <summary>
    /// Resolves the source displayed by settings when no explicit value has been saved:
    /// the provider's first declared source is its default. Fetch orchestration may use
    /// a temporary fallback, but that must not silently change the user's everyday tool.
    /// </summary>
    public static IProviderSource? ConfiguredOrDefaultSourceFor(
        IReadOnlyList<IProviderSource> sources,
        string instanceId,
        IConfig config)
    {
        var explicitlySelected = config.GetScoped(instanceId, ProviderSourceRunner.SourceConfigKey);
        return sources.Find(explicitlySelected) ?? sources.FirstOrDefault();
    }

    /// <summary>
    /// Resolves the dashboard launch action from the configured/default native source.
    /// A native provider never borrows another source's action; legacy providers retain
    /// their existing catalog/default-editor behavior.
    /// </summary>
    public static IProviderLaunchAction? LaunchActionFor(
        IProvider provider,
        string instanceId,
        IConfig config)
    {
        if (provider.Sources.Count == 0)
            return new AppProviderLaunchAction(provider.Type, allowDefaultEditorFallback: true);

        return ConfiguredOrDefaultSourceFor(provider.Sources, instanceId, config)?.LaunchAction;
    }

    /// <summary>True when a provider exposes more than one data source (selected per instance).</summary>
    public static bool HasMultipleSources(string type) =>
        Factories.ContainsKey(type) && Create(type).Sources.Count > 1;

    private static IReadOnlyDictionary<string, Func<IProvider>> BuildFactories()
    {
        var factories = new Dictionary<string, Func<IProvider>>(CustomFactories, StringComparer.OrdinalIgnoreCase);

        foreach (var type in SimpleApiProvider.SupportedTypes)
        {
            if (CustomFactories.ContainsKey(type))
                continue;

            AddFactory(factories, type, () => new SimpleApiProvider(type));
        }

        foreach (var type in WebLoginService.SupportedTypes)
        {
            // A custom factory may deliberately wrap the WebView flow (e.g. KimiProvider
            // prefers the Kimi Code CLI credentials and falls back to the WebView login).
            if (CustomFactories.ContainsKey(type))
                continue;

            AddFactory(factories, type, () => new WebViewLoginProvider(type));
        }

        return factories;
    }

    private static void AddFactory(
        IDictionary<string, Func<IProvider>> factories,
        string type,
        Func<IProvider> factory)
    {
        if (factories.ContainsKey(type))
            throw new InvalidOperationException($"Provider type {type} is registered by more than one provider factory.");

        factories[type] = factory;
    }
}
