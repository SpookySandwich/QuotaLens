using System.Net.Http;

namespace QuotaLens.Core;

/// <summary>Blocks inference traffic from provider refresh paths.</summary>
public static class ReadOnlyRefreshPolicy
{
    private static readonly string[] InferencePaths =
    {
        "/chat/completions",
        "/completions",
        "/responses",
        "/messages",
        ":generatecontent",
        ":streamgeneratecontent",
        "/embeddings",
        "/images/generations",
        "/audio/speech",
        "/invoke",
        "/converse",
        ":predict",
        ":rawpredict",
        ":streamrawpredict",
        ":directpredict",
    };

    public static bool IsInferenceRequest(HttpMethod method, Uri? uri, string? body = null)
    {
        var path = uri?.AbsolutePath ?? string.Empty;
        if (InferencePaths.Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options)
            return false;

        if (string.IsNullOrWhiteSpace(body))
            return false;

        return ContainsJsonProperty(body, "model")
            && (ContainsJsonProperty(body, "messages")
                || ContainsJsonProperty(body, "prompt")
                || ContainsJsonProperty(body, "input")
                || ContainsJsonProperty(body, "contents")
                || ContainsJsonProperty(body, "instances")
                || ContainsJsonProperty(body, "max_tokens")
                || ContainsJsonProperty(body, "max_output_tokens"));
    }

    public static void EnsureReadOnly(HttpMethod method, Uri? uri, string? body = null)
    {
        if (IsInferenceRequest(method, uri, body))
        {
            throw new ProviderException(
                $"Refresh blocked: QuotaLens never sends inference requests ({method} {uri?.Host}{uri?.AbsolutePath}).");
        }
    }

    private static bool ContainsJsonProperty(string body, string propertyName) =>
        body.Contains($"\"{propertyName}\"", StringComparison.OrdinalIgnoreCase);
}

internal sealed class ReadOnlyRefreshHandler : DelegatingHandler
{
    public ReadOnlyRefreshHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ReadOnlyRefreshPolicy.EnsureReadOnly(request.Method, request.RequestUri, body);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
