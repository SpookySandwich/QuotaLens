using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ProviderContractsTests
{
    [TestMethod]
    public void All_WithCatalogProviders_CoversEveryProviderExactlyOnce()
    {
        var expected = Catalog.Types.Select(type => type.Id).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var actual = ProviderContracts.All.Select(contract => contract.ProviderType).Order(StringComparer.OrdinalIgnoreCase).ToArray();

        CollectionAssert.AreEqual(expected, actual, StringComparer.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void For_KimiK2_MarksUnverifiedRelayAsRetiredWithoutCredentialHosts()
    {
        var contract = ProviderContracts.For("kimik2");

        Assert.AreEqual(ProviderSourceKind.UnverifiedRelay, contract.SourceKind);
        Assert.AreEqual(ProviderContractStability.Retired, contract.Stability);
        Assert.HasCount(0, contract.ApprovedCredentialHosts);
    }

    [TestMethod]
    public void For_CodexLb_PinsTheAuditedLocalSchemaRevision()
    {
        var contract = ProviderContracts.For("codex-lb");

        Assert.AreEqual(ProviderAuthKind.LocalService, contract.Auth);
        Assert.IsTrue(contract.AllowsLoopbackHttp);
        Assert.AreEqual("c539a200c301e5cdf2cf524dea336e1c40094bbd", contract.UpstreamRevision);
        Assert.AreEqual("2026-08-02", contract.LastVerifiedAt);
    }

    [TestMethod]
    public void For_Codex_AllowsCurrentAndLegacyCredentialHosts()
    {
        var contract = ProviderContracts.For("codex");

        CollectionAssert.AreEquivalent(
            new[] { "chatgpt.com", "chat.openai.com" },
            contract.ApprovedCredentialHosts.ToArray());
    }

    [TestMethod]
    public void For_Gemini_ClassifiesInternalQuotaEndpointAsUpstreamCompatibility()
    {
        var contract = ProviderContracts.For("gemini");
        var source = contract.SourceFor("Gemini OAuth");

        Assert.AreEqual(ProviderSourceKind.UndocumentedApi, contract.SourceKind);
        Assert.AreEqual(ProviderContractStability.UpstreamCompatibility, contract.Stability);
        Assert.IsNull(contract.OfficialDocumentation);
        Assert.AreEqual(ProviderSourceKind.UndocumentedApi, source.SourceKind);
        Assert.AreEqual(ProviderContractStability.UpstreamCompatibility, source.Stability);
        StringAssert.Contains(source.EvidenceUrl, "GeminiStatusProbe.swift");
    }

    [TestMethod]
    public void For_Claude_ClassifiesUndocumentedOAuthUsageSchemaAsUpstreamCompatibility()
    {
        var contract = ProviderContracts.For("claude");
        var source = contract.SourceFor("Anthropic OAuth API");

        Assert.AreEqual(ProviderSourceKind.UndocumentedApi, contract.SourceKind);
        Assert.AreEqual(ProviderContractStability.UpstreamCompatibility, contract.Stability);
        Assert.IsNull(contract.OfficialDocumentation);
        Assert.AreEqual(ProviderSourceKind.UndocumentedApi, source.SourceKind);
        Assert.AreEqual(ProviderContractStability.UpstreamCompatibility, source.Stability);
        StringAssert.Contains(source.EvidenceUrl, "ClaudeOAuthUsageFetcher.swift");
    }

    [TestMethod]
    public void For_VertexAi_DocumentsImplementedMonitoringTimeSeriesEndpoint()
    {
        var contract = ProviderContracts.For("vertexai");

        Assert.AreEqual(
            "https://docs.cloud.google.com/monitoring/api/ref_v3/rest/v3/projects.timeSeries/list",
            contract.OfficialDocumentation);
        Assert.AreEqual(
            ProviderCapability.QuotaWindows | ProviderCapability.CostActivity,
            contract.Capabilities);
    }

    [TestMethod]
    public void For_AzureOpenAi_ClassifiesArmUsagesAsCapacityAllocationNotRuntimeQuota()
    {
        var contract = ProviderContracts.For("azureopenai");

        Assert.IsTrue(contract.Capabilities.HasFlag(ProviderCapability.CapacityAllocation));
        Assert.IsFalse(contract.Capabilities.HasFlag(ProviderCapability.QuotaWindows));
        Assert.AreEqual(ProviderSourceKind.OfficialApi, contract.SourceKind);
    }

    [TestMethod]
    public void For_Deepgram_DocumentsUsageBreakdownWithoutClaimingBalance()
    {
        var contract = ProviderContracts.For("deepgram");

        Assert.AreEqual(
            "https://developers.deepgram.com/reference/manage/usage/breakdown/get",
            contract.OfficialDocumentation);
        Assert.AreEqual(ProviderCapability.CostActivity, contract.Capabilities);
        Assert.IsFalse(contract.Capabilities.HasFlag(ProviderCapability.Balance));
    }

    [TestMethod]
    public void For_PrivateDashboardCaptureProviders_MatchesParserOutputCapabilities()
    {
        var expected = new (string ProviderType, ProviderCapability Capabilities)[]
        {
            ("alibaba", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("alibabatokenplan", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("bayesdl", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("mimo", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("amp", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("cursor", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("augment", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("factory", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("minimax", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("windsurf", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows),
            ("manus", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows | ProviderCapability.Balance),
            ("perplexity", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("t3chat", ProviderCapability.QuotaWindows),
            ("commandcode", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("ollama", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows),
            ("abacus", ProviderCapability.QuotaWindows | ProviderCapability.Balance),
            ("stepfun", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows),
            ("opencode", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows),
            ("mistral", ProviderCapability.QuotaWindows | ProviderCapability.DynamicWindows |
                ProviderCapability.Balance | ProviderCapability.CostActivity),
        };

        foreach (var (providerType, capabilities) in expected)
        {
            var contract = ProviderContracts.For(providerType);

            Assert.AreEqual(ProviderSourceKind.PrivateDashboard, contract.SourceKind, providerType);
            Assert.AreEqual(ProviderContractStability.PrivateContract, contract.Stability, providerType);
            Assert.AreEqual(capabilities, contract.Capabilities, providerType);
        }
    }

    [TestMethod]
    public void OfficialContracts_HavePublicDocumentationAndVerificationDates()
    {
        foreach (var contract in ProviderContracts.All.Where(contract =>
                     contract.Stability == ProviderContractStability.Official))
        {
            Assert.IsTrue(
                Uri.TryCreate(contract.OfficialDocumentation, UriKind.Absolute, out var source)
                && source.Scheme == Uri.UriSchemeHttps,
                $"{contract.ProviderType} is marked official without an HTTPS documentation source.");
            Assert.IsTrue(
                DateOnly.TryParse(contract.LastVerifiedAt, out _),
                $"{contract.ProviderType} is marked official without a verification date.");
        }
    }

    [TestMethod]
    public void For_Copilot_DoesNotMislabelInternalQuotaEndpointAsPublicApi()
    {
        var contract = ProviderContracts.For("copilot");

        Assert.AreEqual(ProviderSourceKind.UndocumentedApi, contract.SourceKind);
        Assert.AreEqual(ProviderContractStability.PrivateContract, contract.Stability);
        Assert.IsNull(contract.OfficialDocumentation);
    }

    [TestMethod]
    public void SourceFor_KimiWebView_DistinguishesPrivateChannelFromLocalCli()
    {
        var contract = ProviderContracts.For("kimi");

        var web = contract.SourceFor("Kimi WebView");
        var cli = contract.SourceFor("Kimi Code CLI");

        Assert.AreEqual(ProviderSourceKind.PrivateDashboard, web.SourceKind);
        Assert.AreEqual(ProviderContractStability.PrivateContract, web.Stability);
        Assert.AreEqual(ProviderSourceKind.CliOrLocal, cli.SourceKind);
    }

    [TestMethod]
    public void For_OpenCodeGo_DistinguishesLocalHistoryAndWebQuotaChannels()
    {
        var contract = ProviderContracts.For("opencodego");

        Assert.AreEqual(ProviderAuthKind.LocalCli | ProviderAuthKind.BrowserSession, contract.Auth);
        Assert.AreEqual(ProviderSourceKind.CliOrLocal, contract.SourceKind);
        Assert.AreEqual(ProviderSourceKind.CliOrLocal, contract.SourceFor("OpenCode local history").SourceKind);
        Assert.AreEqual(
            ProviderSourceKind.PrivateDashboard,
            contract.SourceFor("OpenCode Go Web quota + local history").SourceKind);
        Assert.AreEqual(
            ProviderSourceKind.PrivateDashboard,
            contract.SourceFor("OpenCode Go local history + Web balance").SourceKind);
        Assert.AreEqual(
            ProviderSourceKind.PrivateDashboard,
            contract.SourceFor("OpenCode Go WebView").SourceKind);
    }

    [TestMethod]
    public void RequireCredentialTarget_AzureArmHost_ReturnsNormalizedUri()
    {
        var uri = ProviderEndpointPolicy.RequireCredentialTarget(
            "azureopenai",
            "https://management.azure.com/subscriptions/example/providers/Microsoft.CognitiveServices/locations/eastus/usages");

        Assert.AreEqual("management.azure.com", uri.IdnHost);
    }

    [TestMethod]
    [DataRow("https://my-resource.openai.azure.com/")]
    [DataRow("https://openai.azure.com.evil.example/")]
    [DataRow("http://management.azure.com/")]
    [DataRow("https://user:password@management.azure.com/")]
    public void RequireCredentialTarget_UnsafeAzureOverride_Throws(string endpoint)
    {
        Assert.ThrowsExactly<ProviderException>(() =>
            ProviderEndpointPolicy.RequireCredentialTarget("azureopenai", endpoint));
    }

    [TestMethod]
    public void For_Doubao_UsesLocalCliUpstreamCompatibilityOnly()
    {
        var contract = ProviderContracts.For("doubao");

        Assert.AreEqual(ProviderAuthKind.LocalCli, contract.Auth);
        Assert.AreEqual(ProviderSourceKind.CliOrLocal, contract.SourceKind);
        Assert.AreEqual(ProviderContractStability.UpstreamCompatibility, contract.Stability);
        Assert.HasCount(0, contract.ApprovedCredentialHosts);
    }

    [TestMethod]
    [DataRow("https://proxy.example.com/api/status")]
    [DataRow("http://127.0.0.1:8787/status")]
    [DataRow("http://localhost:8787/status")]
    public void RequireCredentialTarget_CustomProxySafeTransport_ReturnsUri(string endpoint)
    {
        var uri = ProviderEndpointPolicy.RequireCredentialTarget("llmproxy", endpoint);

        Assert.AreEqual(new Uri(endpoint), uri);
    }

    [TestMethod]
    public void RequireCredentialTarget_CustomProxyInsecureRemoteHttp_Throws()
    {
        Assert.ThrowsExactly<ProviderException>(() =>
            ProviderEndpointPolicy.RequireCredentialTarget("llmproxy", "http://proxy.example.com/status"));
    }

    [TestMethod]
    public void RequireCredentialBase_CustomProxyIpv6Loopback_ReturnsUri()
    {
        var uri = ProviderEndpointPolicy.RequireCredentialBase("llmproxy", "http://[::1]:8787/api");

        Assert.IsTrue(uri.IsLoopback);
        Assert.AreEqual(8787, uri.Port);
    }

    [TestMethod]
    [DataRow("https://proxy.example.com/api?credential=leak")]
    [DataRow("https://user:password@proxy.example.com/api")]
    [DataRow("https://proxy.example.com/api#fragment")]
    public void RequireCredentialBase_AmbiguousBaseUrl_Throws(string endpoint)
    {
        Assert.ThrowsExactly<ProviderException>(() =>
            ProviderEndpointPolicy.RequireCredentialBase("llmproxy", endpoint));
    }

    [TestMethod]
    [DataRow("https://groq.example.com/v1")]
    [DataRow("https://api.groq.com.evil.example/v1")]
    [DataRow("http://api.groq.com/v1")]
    public void RequireCredentialBase_UnsafeGroqOverride_Throws(string endpoint)
    {
        Assert.ThrowsExactly<ProviderException>(() =>
            ProviderEndpointPolicy.RequireCredentialBase("groq", endpoint));
    }

    [TestMethod]
    public async Task GroqFetch_UnsafeOverrideFailsBeforeMetricsTransport()
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["groq.groq_key"] = "test-key-never-sent",
            ["groq.groq_base_url"] = "https://collector.example.com/v1",
        });

        var error = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            new GroqProvider().FetchAsync("groq", config, CancellationToken.None));

        StringAssert.Contains(error.Message, "cannot be sent");
    }

    [TestMethod]
    public async Task DeepgramFetch_UnsafeOverrideFailsBeforeApiTransport()
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["deepgram.deepgram_key"] = "test-key-never-sent",
            ["deepgram.deepgram_base_url"] = "https://collector.example.com/v1",
        });

        var error = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            new DeepgramProvider().FetchAsync("deepgram", config, CancellationToken.None));

        StringAssert.Contains(error.Message, "cannot be sent");
    }

    [TestMethod]
    public void CodexUsageBase_UnsafeOverrideFailsBeforeCredentialTransport()
    {
        var error = Assert.ThrowsExactly<ProviderException>(() =>
            CodexProvider.ValidatedChatGptBaseUrl("https://chatgpt.com.evil.example/backend-api"));

        StringAssert.Contains(error.Message, "cannot be sent");
    }

    [TestMethod]
    public async Task BedrockFetch_UnsafeOverrideFailsBeforeSignedTransport()
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["bedrock.bedrock_access_key_id"] = "test-access-key-never-sent",
            ["bedrock.bedrock_secret_access_key"] = "test-secret-never-sent",
            ["bedrock.bedrock_cost_explorer_url"] = "https://collector.example.com/",
        });

        var error = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            new BedrockProvider().FetchAsync("bedrock", config, CancellationToken.None));

        StringAssert.Contains(error.Message, "cannot be sent");
    }

    [TestMethod]
    public async Task KiloFetch_UnsafeOverrideFailsBeforeBearerTransport()
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["kilo.kilo_key"] = "test-key-never-sent",
            ["kilo.kilo_base_url"] = "https://collector.example.com/trpc",
        });

        var error = await Assert.ThrowsExactlyAsync<ProviderException>(() =>
            new KiloProvider().FetchAsync("kilo", config, CancellationToken.None));

        StringAssert.Contains(error.Message, "cannot be sent");
    }

    [TestMethod]
    public void IsInferenceRequest_ReadOnlyUsagePost_ReturnsFalse()
    {
        var result = ReadOnlyRefreshPolicy.IsInferenceRequest(
            HttpMethod.Post,
            new Uri("https://api.example.com/v1/usage"),
            "{\"start_time\":123,\"project_ids\":[\"proj_123\"]}");

        Assert.IsFalse(result);
    }

    [TestMethod]
    [DataRow("https://api.openai.com/v1/chat/completions", "{\"model\":\"gpt\",\"messages\":[]}")]
    [DataRow("https://api.anthropic.com/v1/messages", "{\"model\":\"claude\",\"prompt\":\"ping\"}")]
    [DataRow("https://example.com/status", "{\"model\":\"x\",\"messages\":[{\"content\":\"ping\"}]}")]
    [DataRow("https://example.com/status", "{\"model\":\"x\",\"input\":\"ping\"}")]
    public void IsInferenceRequest_InferenceShape_ReturnsTrue(string endpoint, string body)
    {
        Assert.IsTrue(ReadOnlyRefreshPolicy.IsInferenceRequest(HttpMethod.Post, new Uri(endpoint), body));
    }

    [TestMethod]
    public void IsInferenceRequest_BedrockInvokePath_ReturnsTrueEvenForUnexpectedGet()
    {
        Assert.IsTrue(ReadOnlyRefreshPolicy.IsInferenceRequest(
            HttpMethod.Get,
            new Uri("https://bedrock-runtime.us-east-1.amazonaws.com/model/example/invoke")));
    }

    [TestMethod]
    public async Task HttpHandler_InferenceRequest_BlocksBeforeTransport()
    {
        var transport = new RecordingHandler();
        using var client = new HttpClient(Http.CreateHandler(transport));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/chat/completions")
        {
            Content = new StringContent(
                "{\"model\":\"probe\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}",
                Encoding.UTF8,
                "application/json"),
        };

        await Assert.ThrowsExactlyAsync<ProviderException>(() => client.SendAsync(request));
        Assert.AreEqual(0, transport.CallCount);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FakeConfig(IReadOnlyDictionary<string, string> values) : IConfig
    {
        public string Get(string key, string fallback = "") =>
            values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            values.TryGetValue($"{instanceId}.{key}", out var value) ? value : Get(key, fallback);

        public bool HasScoped(string instanceId, string key) => values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) => fallback;
    }
}
