using System.Net;
using System.Net.Http;

namespace QuotaLens.Core;

/// <summary>Shared HttpClient for providers (20s timeout, auto-decompression).</summary>
public static class Http
{
    internal const int MaxTransientTransportRetries = 2;

    public static readonly HttpClient Client = new(CreateHandler())
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    internal static HttpMessageHandler CreateHandler(HttpMessageHandler? inner = null) =>
        new ReadOnlyRefreshHandler(
            new TransientHttpRetryHandler(inner ?? new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
            }));

    internal static bool IsTransientTransportFailure(Exception exception) =>
        exception is HttpRequestException;
}

internal sealed class TransientHttpRetryHandler : DelegatingHandler
{
    public TransientHttpRetryHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var contentBytes = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            using var retryRequest = CloneRequest(request, contentBytes);
            try
            {
                return await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (
                attempt < Http.MaxTransientTransportRetries
                && IsSafeToRetry(request.Method)
                && Http.IsTransientTransportFailure(e)
                && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt == 0 ? 250 : 750);

    private static bool IsSafeToRetry(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options;

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source, byte[]? contentBytes)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
        };

        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (contentBytes is not null)
        {
            clone.Content = new ByteArrayContent(contentBytes);
            if (source.Content is not null)
            {
                foreach (var header in source.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
