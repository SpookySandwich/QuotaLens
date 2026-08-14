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
            ["antigravity"] = () => new AntigravityProvider(),
            ["qoder"] = () => new QoderProvider(),
            ["azureopenai"] = () => new AzureOpenAIProvider(),
            ["doubao"] = () => new DoubaoProvider(),
            ["groq"] = () => new GroqProvider(),
            ["deepgram"] = () => new DeepgramProvider(),
            ["grok"] = () => new GrokProvider(),
            ["kilo"] = () => new KiloProvider(),
            ["jetbrains"] = () => new JetBrainsProvider(),
            // CLI-first with WebView fallback: overrides the WebLoginService factory below.
            ["kimi"] = () => new KimiProvider(),
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

    /// <summary>True when a provider exposes more than one data source (selected per instance).</summary>
    public static bool HasMultipleSources(string type) =>
        Factories.ContainsKey(type) && Create(type).Sources.Count > 1;

    private static IReadOnlyDictionary<string, Func<IProvider>> BuildFactories()
    {
        var factories = new Dictionary<string, Func<IProvider>>(CustomFactories, StringComparer.OrdinalIgnoreCase);

        foreach (var type in SimpleApiProvider.SupportedTypes)
            AddFactory(factories, type, () => new SimpleApiProvider(type));

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
