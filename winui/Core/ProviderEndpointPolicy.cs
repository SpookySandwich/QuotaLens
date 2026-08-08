using System.Net;

namespace QuotaLens.Core;

/// <summary>Validates a destination before a provider attaches credentials to it.</summary>
public static class ProviderEndpointPolicy
{
    /// <summary>Validates a credential-bearing base URL before provider paths are appended.</summary>
    public static Uri RequireCredentialBase(string providerType, string endpoint)
    {
        var uri = RequireCredentialTarget(providerType, endpoint);
        if (!string.IsNullOrEmpty(uri.Query))
        {
            throw new ProviderException(
                $"Not configured: {Catalog.ProviderName(providerType)} base URL cannot contain a query string.");
        }

        return uri;
    }

    public static Uri RequireCredentialTarget(string providerType, string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ProviderException($"Not configured: {Catalog.ProviderName(providerType)} endpoint is invalid.");
        }

        var contract = ProviderContracts.For(providerType);
        var isLoopback = IsLoopback(uri);
        var hasSafeScheme = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (contract.AllowsLoopbackHttp
                && isLoopback
                && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
        if (!hasSafeScheme)
        {
            throw new ProviderException(
                $"Not configured: {Catalog.ProviderName(providerType)} credentials require HTTPS or an approved loopback HTTP endpoint.");
        }

        if (!contract.AllowsCustomCredentialHost
            && !contract.ApprovedCredentialHosts.Any(pattern => HostMatches(uri.IdnHost, pattern)))
        {
            throw new ProviderException(
                $"Not configured: {Catalog.ProviderName(providerType)} credentials cannot be sent to host '{uri.IdnHost}'.");
        }

        return uri;
    }

    internal static bool HostMatches(string host, string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host.Length > suffix.Length;
        }

        if (pattern.Contains('*', StringComparison.Ordinal))
        {
            var parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                && host.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
                && host.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback)
            return true;

        return IPAddress.TryParse(uri.IdnHost, out var address) && IPAddress.IsLoopback(address);
    }
}
