using System.Net;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class HttpTests
{
    [TestMethod]
    public async Task RetryingHandler_RetriesTransientTransportFailure()
    {
        var handler = new SequenceHandler(
            () => throw new HttpRequestException("The SSL connection could not be established, see inner exception."),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using var client = new HttpClient(Http.CreateHandler(handler));
        using var response = await client.GetAsync("https://example.test/");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, handler.Attempts);
    }

    [TestMethod]
    public async Task RetryingHandler_StopsAfterRetryBudget()
    {
        var handler = new SequenceHandler(
            () => throw new HttpRequestException("The SSL connection could not be established, see inner exception."),
            () => throw new HttpRequestException("The SSL connection could not be established, see inner exception."),
            () => throw new HttpRequestException("The SSL connection could not be established, see inner exception."));

        using var client = new HttpClient(Http.CreateHandler(handler));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => client.GetAsync("https://example.test/"));
        Assert.AreEqual(Http.MaxTransientTransportRetries + 1, handler.Attempts);
    }

    [TestMethod]
    public async Task RetryingHandler_PostTransportFailure_DoesNotRetry()
    {
        var handler = new SequenceHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("Connection reset.")),
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        using var client = new HttpClient(Http.CreateHandler(handler));

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.PostAsync(
            "https://example.test/status",
            new StringContent("""{"readOnly":true}""")));
        Assert.AreEqual(1, handler.Attempts);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public SequenceHandler(params Func<HttpResponseMessage>[] responses)
            : this(responses.Select<Func<HttpResponseMessage>, Func<HttpRequestMessage, HttpResponseMessage>>(response => _ => response()).ToArray())
        {
        }

        public SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (_responses.Count == 0)
                throw new InvalidOperationException("No response configured.");

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
