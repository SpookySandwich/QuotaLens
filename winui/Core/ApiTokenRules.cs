using System.Globalization;

namespace QuotaLens.Core;

/// <summary>
/// Turns a metered API balance into tokens so a pay-as-you-go key can share the
/// effective-usage axis with subscription plans. Without this the chart would put
/// a dollar figure and a token figure on the same scale, and a funded key would
/// draw a bar hundreds of times narrower than a plan it can actually outspend.
///
/// The rate is one blended USD-per-million figure per provider — input, cached
/// input, and output mixed the way an agentic coding session actually consumes
/// them. It is an estimate, and the chart says so; users can override it per
/// provider with the config key <c>api_token_price.&lt;providerType&gt;</c>.
/// </summary>
public static class ApiTokenRules
{
    private const string KeyPrefix = "api_token_price.";

    /// <summary>
    /// Blended rate for a metered provider with no entry of its own. Sits between
    /// the cheap open-weight endpoints and frontier pricing, so an unknown key gets
    /// a plausible bar rather than a flattering one.
    /// </summary>
    public const double GlobalDefaultUsdPerMillionTokens = 2.0;

    public static string ConfigKey(string providerType) => $"{KeyPrefix}{providerType}";

    /// <summary>
    /// Blended USD per million tokens, or null when the provider is not metered in
    /// tokens at all (speech and audio APIs) and therefore has no place on a token
    /// axis.
    /// </summary>
    public static double? UsdPerMillionTokens(string providerType, IConfig? config = null)
    {
        var configured = config?.Get(ConfigKey(providerType));
        if (!string.IsNullOrWhiteSpace(configured)
            && double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        if (Catalog.DefaultApiTokenPrices.TryGetValue(providerType, out var price))
            return price > 0 ? price : null;

        return GlobalDefaultUsdPerMillionTokens;
    }

    /// <summary>
    /// Tokens (millions) the remaining balance can buy. Returns zero for an empty
    /// balance or a provider that is not token-metered — both mean "nothing to draw
    /// on this axis", never "assume the default rate".
    /// </summary>
    public static double TokensMillionsForBalance(string providerType, double balanceUsd, IConfig? config = null)
    {
        if (!(balanceUsd > 0))
            return 0;

        var rate = UsdPerMillionTokens(providerType, config);
        return rate is > 0 ? balanceUsd / rate.Value : 0;
    }
}
