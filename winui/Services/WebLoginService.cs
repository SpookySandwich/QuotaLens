using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using QuotaLens.Core;
using QuotaLens.Providers;
using QuotaLens.Views;

namespace QuotaLens.Services;

/// <summary>
/// Drives WebView-login providers. BayesDL/MiMo started as faithful ports of
/// the Tauri commands open_bayesdl_login / open_mimo_login and their injected JS
/// from src-tauri/src/main.rs; newer CodexBar-derived web providers use the same
/// browser-session capture pattern with provider-specific scripts and parsers.
///
/// The original used an embedded Tauri WebviewWindow that:
///   1. injects an initialization script which, once the page is logged-in, calls the
///      provider's own API (using the page cookies), encodes the JSON result with a
///      url-safe base64 ("qlEncode"), and stuffs it into <c>window.location.hash</c>
///      behind a <c>#__ql__</c> marker;
///   2. polls the window URL every 2s (up to 60 iterations) for that marker, decodes,
///      parses into a ProviderSnapshot, caches it, and closes the window.
///
/// This service reproduces that exactly on WinUI 3 using Microsoft.UI.Xaml.Controls.WebView2.
///
/// PROFILE REUSE: legacy default instances (id == provider type) use
/// %LOCALAPPDATA%\com.quotalens.app\EBWebView, the old Tauri WebView2 profile, so
/// existing cookies/sessions carry over. Generated instances use their own WebView2
/// user data folder so two cards of the same provider can log in separately.
///
/// THREADING: WebView2 MUST run on the UI thread. The service is constructed with the UI
/// DispatcherQueue; every WebView2 touch is marshalled onto it via <see cref="RunOnUiAsync{T}"/>.
/// Public methods are safe to call from any thread (e.g. the refresh scheduler).
///
/// CACHING: the latest successful snapshot per provider instance is cached in-memory and
/// persisted under %LOCALAPPDATA%\QuotaLens\WebLoginCache as last-known data. FetchAsync
/// only attempts hidden recapture after a provider has cached snapshot data; the persisted
/// cache lets WebView providers survive app restarts without needing a visible login window.
/// </summary>
public sealed class WebLoginService
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed record WebLoginCachedSnapshot(
        int Version,
        string ProviderType,
        ProviderSnapshot Snapshot);

    /// <summary>
    /// Set during App startup with the UI-thread DispatcherQueue, e.g.:
    /// <code>WebLoginService.Instance = new WebLoginService(DispatcherQueue.GetForCurrentThread());</code>
    /// Web-login providers read this static to delegate their fetches. Integration
    /// must construct it on the UI thread (so DispatcherQueue.GetForCurrentThread() is non-null)
    /// before any provider fetch runs.
    /// </summary>
    public static WebLoginService? Instance { get; set; }

    private readonly DispatcherQueue _ui;
    private readonly Func<WebLoginCaptureRequest, bool, Task<bool>> _captureAsync;
    private readonly string? _cacheDirectory;
    private readonly string? _localAppDataDirectory;

    // In-memory snapshot cache, keyed by provider instance.
    private readonly Dictionary<string, ProviderSnapshot> _cache = new();
    private readonly object _cacheLock = new();

    // One live login window per provider instance.
    private readonly Dictionary<string, ProviderLoginWindow> _windows = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _captureSignals = new();

    // Last native cookie-capture attempt per instance (token + timestamp), so the same
    // token is not hammered against the provider API on every 2s poll iteration.
    private readonly Dictionary<string, (string Token, DateTimeOffset At)> _nativeCookieAttempts = new();

    internal sealed record WebLoginCaptureRequest(
        string InstanceId,
        string ProviderType,
        string LoginUrl,
        string CaptureUrl,
        string UserDataFolder);

    private sealed record WebLoginProviderDefinition(
        string Type,
        string InitScript,
        string? BackupFetchScript,
        Func<string, ProviderSnapshot> Parse);

    private static readonly IReadOnlyDictionary<string, WebLoginProviderDefinition> Definitions =
        new Dictionary<string, WebLoginProviderDefinition>
        {
            ["bayesdl"] = new(
                "bayesdl",
                BayesdlInitScript,
                BayesdlFetchScript,
                ParseBayesdl),
            ["mimo"] = new(
                "mimo",
                MimoInitScript,
                null,
                ParseMimo),
            ["kimi"] = new(
                "kimi",
                KimiInitScript,
                null,
                ParseKimi),
            ["alibaba"] = new(
                "alibaba",
                AlibabaCodingPlanInitScript,
                null,
                ParseAlibabaCodingPlan),
            ["alibabatokenplan"] = new(
                "alibabatokenplan",
                AlibabaTokenPlanInitScript,
                null,
                ParseAlibabaTokenPlan),
            ["amp"] = new(
                "amp",
                AmpInitScript,
                null,
                ParseAmp),
            ["cursor"] = new(
                "cursor",
                CursorInitScript,
                null,
                ParseCursor),
            ["augment"] = new(
                "augment",
                AugmentInitScript,
                null,
                ParseAugment),
            ["factory"] = new(
                "factory",
                FactoryInitScript,
                null,
                ParseFactory),
            ["minimax"] = new(
                "minimax",
                MiniMaxInitScript,
                null,
                ParseMiniMax),
            ["windsurf"] = new(
                "windsurf",
                WindsurfInitScript,
                null,
                ParseWindsurf),
            ["manus"] = new(
                "manus",
                ManusInitScript,
                null,
                ParseManus),
            ["perplexity"] = new(
                "perplexity",
                PerplexityInitScript,
                null,
                ParsePerplexity),
            ["t3chat"] = new(
                "t3chat",
                T3ChatInitScript,
                null,
                ParseT3Chat),
            ["commandcode"] = new(
                "commandcode",
                CommandCodeInitScript,
                null,
                ParseCommandCode),
            ["ollama"] = new(
                "ollama",
                OllamaInitScript,
                null,
                ParseOllama),
            ["abacus"] = new(
                "abacus",
                AbacusInitScript,
                null,
                ParseAbacus),
            ["stepfun"] = new(
                "stepfun",
                StepFunInitScript,
                null,
                ParseStepFun),
            ["opencode"] = new(
                "opencode",
                OpenCodeInitScript,
                null,
                ParseOpenCode),
            ["opencodego"] = new(
                "opencodego",
                OpenCodeGoInitScript,
                null,
                ParseOpenCodeGo),
            ["mistral"] = new(
                "mistral",
                MistralInitScript,
                null,
                ParseMistral),
        };

    public static bool IsSupported(string providerType) => Definitions.ContainsKey(providerType);

    public static IReadOnlyCollection<string> SupportedTypes => Definitions.Keys.ToArray();

    internal static string InitScriptForTesting(string providerType) =>
        Definition(providerType).InitScript;

    internal static string BridgeScriptForTesting() => WebMessageBridgeScript;

    internal static string ModeScriptForTesting(bool hidden) => WebLoginModeScript(hidden);

    internal static bool NativeCapturedResponseForTesting(string providerType, string? uri) =>
        IsNativeCapturedResponse(providerType, uri);

    public WebLoginService(DispatcherQueue uiDispatcher)
    {
        _ui = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _captureAsync = RunLoginAsync;
        _cacheDirectory = DefaultCacheDirectory();
        _localAppDataDirectory = null;
    }

    internal WebLoginService(Func<WebLoginCaptureRequest, bool, Task<bool>> captureAsync)
        : this(captureAsync, cacheDirectory: null)
    {
    }

    internal WebLoginService(
        Func<WebLoginCaptureRequest, bool, Task<bool>> captureAsync,
        string? cacheDirectory,
        string? localAppDataDirectory = null)
    {
        _ui = null!;
        _captureAsync = captureAsync ?? throw new ArgumentNullException(nameof(captureAsync));
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory) ? null : cacheDirectory;
        _localAppDataDirectory = string.IsNullOrWhiteSpace(localAppDataDirectory) ? null : localAppDataDirectory;
    }

    // ---- public API -------------------------------------------------------

    /// <summary>
    /// Returns cached WebView data and, by default, refreshes it in the background
    /// capture path only after the user has signed in once. With no cache, throws the
    /// EXACT placeholder "Login required …" error so the card can offer a sign-in action.
    /// </summary>
    public async Task<ProviderSnapshot> FetchAsync(
        string instanceId,
        string providerType,
        IConfig config,
        bool allowHiddenCapture = true)
    {
        var cached = GetCached(instanceId, providerType);
        var refreshMs = RefreshIntervalMs(config);
        if (string.Equals(providerType, "opencodego", StringComparison.OrdinalIgnoreCase))
        {
            var local = await OpenCodeGoLocalUsageProvider.TryFetchAsync(instanceId, config, CancellationToken.None)
                .ConfigureAwait(false);
            if (local is not null)
            {
                if (ShouldOverlayOpenCodeGoCache(cached, refreshMs))
                    local = MergeOpenCodeGoSources(local, cached!);
                return NormalizeOpenCodeGoLocalSnapshot(local);
            }
        }

        if (cached is null)
            throw new ProviderException(PlaceholderError(providerType));

        if (allowHiddenCapture)
        {
            var request = CaptureRequest(instanceId, providerType, config, visibleLogin: false);
            await _captureAsync(request, true).ConfigureAwait(false);

            cached = GetCached(instanceId, providerType);
        }

        if (cached is not null && !Quota.IsStale(cached.UpdatedAt, refreshMs))
        {
            var normalized = NormalizeSnapshot(providerType, cached);
            await TryAttachAlibabaCloudBalanceAsync(instanceId, providerType, config, normalized).ConfigureAwait(false);
            return normalized;
        }

        throw new ProviderException(PlaceholderError(providerType));
    }

    internal static bool ShouldOverlayOpenCodeGoCache(ProviderSnapshot? cached, double refreshMs) =>
        cached is { Error: null }
        && !Quota.IsStale(cached.UpdatedAt, refreshMs);

    internal static ProviderSnapshot MergeOpenCodeGoSources(
        ProviderSnapshot local,
        ProviderSnapshot web)
    {
        var webQuotas = ProviderSnapshotWindows.AllWindows(web)
            .Where(window => window.Kind == RateWindowKind.Quota)
            .Select(window => (Key: OpenCodeGoWindowKey(window), Window: window))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Window, StringComparer.OrdinalIgnoreCase);

        var overlaidQuota = false;
        if (webQuotas.TryGetValue("rolling", out var rolling))
        {
            local.Primary = rolling;
            overlaidQuota = true;
        }
        if (webQuotas.TryGetValue("weekly", out var weekly))
        {
            local.Secondary = weekly;
            overlaidQuota = true;
        }
        if (webQuotas.TryGetValue("monthly", out var monthly))
        {
            monthly.CountsForAvailability = true;
            local.Tertiary = monthly;
            overlaidQuota = true;
        }

        foreach (var window in ProviderSnapshotWindows.AllWindows(web).Where(window =>
                     window.Kind == RateWindowKind.Quota
                     && OpenCodeGoWindowKey(window) is null
                     && !local.AdditionalWindows.Any(existing =>
                         existing.Label.Equals(window.Label, StringComparison.OrdinalIgnoreCase))))
        {
            local.AdditionalWindows.Add(window);
            overlaidQuota = true;
        }

        if (web.Balance is not null)
            local.Balance = web.Balance;

        if (overlaidQuota)
        {
            local.SourceLabel = "OpenCode Go Web quota + local history";
            // Conservatively expose the age of the authoritative web lanes instead
            // of making cached data look newly refreshed by the local calculation.
            local.UpdatedAt = web.UpdatedAt;
        }
        else if (web.Balance is not null)
        {
            local.SourceLabel = "OpenCode Go local history + Web balance";
            local.UpdatedAt = web.UpdatedAt;
        }

        return local;
    }

    internal static ProviderSnapshot NormalizeOpenCodeGoLocalSnapshot(ProviderSnapshot snapshot) =>
        ProviderSnapshotMetadata.Apply("opencodego", snapshot.SourceLabel, snapshot.Confidence, snapshot);

    private static string? OpenCodeGoWindowKey(RateWindow window)
    {
        var label = window.Label.Trim();
        if (label.Contains("5h", StringComparison.OrdinalIgnoreCase)
            || label.Contains("5 hour", StringComparison.OrdinalIgnoreCase)
            || label.Contains("5-hour", StringComparison.OrdinalIgnoreCase)
            || label.Contains("rolling", StringComparison.OrdinalIgnoreCase))
        {
            return "rolling";
        }
        if (label.Contains("week", StringComparison.OrdinalIgnoreCase))
            return "weekly";
        if (label.Contains("month", StringComparison.OrdinalIgnoreCase))
            return "monthly";
        return null;
    }

    /// <summary>
    /// Opens a VISIBLE login window for manual login (mirrors open_*_login(hidden:false)):
    /// if a window already exists it is closed first, then a fresh visible one is created.
    /// </summary>
    public async Task<bool> OpenLoginAsync(string instanceId, string providerType, IConfig config) =>
        await _captureAsync(CaptureRequest(instanceId, providerType, config, visibleLogin: true), false).ConfigureAwait(false);

    /// <summary>Latest cached snapshot, or null. Used by providers and integration.</summary>
    public ProviderSnapshot? GetCached(string instanceId, string? providerType = null)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(instanceId, out var snapshot))
            {
                if (providerType is not null && !IsSnapshotForProvider(snapshot, providerType))
                    return null;

                return CloneSnapshot(snapshot);
            }
        }

        return LoadCachedSnapshot(instanceId, providerType);
    }

    // ---- window lifecycle + scrape loop -----------------------------------

    private async Task<bool> RunLoginAsync(WebLoginCaptureRequest request, bool hidden)
    {
        // Existing-window rules (open_bayesdl_login / open_mimo_login):
        //   hidden  => if a window already exists, skip (an auto-fetch is already in flight).
        //   visible => close the existing window, wait 200ms, then open a fresh visible one.
        bool exists;
        lock (_windows)
            exists = _windows.ContainsKey(request.InstanceId);
        if (exists)
        {
            if (hidden)
                return false;
            await RunOnUiAsync(() => { CloseWindow(request.InstanceId); return true; }).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
        }

        var captureSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_captureSignals)
            _captureSignals[request.InstanceId] = captureSignal;

        // Build the window + CoreWebView2 with the instance profile, inject the init script,
        // navigate, and (when not hidden) show it. Returns once navigation has started.
        try
        {
            var ok = await RunOnUiAsync(async () =>
            {
                try
                {
                    await CreateWindowAsync(request, hidden).ConfigureAwait(true);
                    return true;
                }
                catch
                {
                    CloseWindow(request.InstanceId);
                    return false;
                }
            }).ConfigureAwait(false);

            if (!ok)
                return false;

            // Drive the eval-fetch backup loop and the hash-poll loop. The poll loop completes
            // when data is captured (cache updated + window closed), the visible login window is
            // closed by the user, or the hidden auto-fetch budget runs out. Visible manual login is
            // deliberately open-ended so a slow human login still refreshes the card when capture
            // eventually succeeds.
            var captured = await PollLoopAsync(request, PollBudgetFor(request, hidden)).ConfigureAwait(false);

            // A hidden window must never linger off-screen — close it if the scrape didn't.
            if (hidden)
                await RunOnUiAsync(() => { CloseWindow(request.InstanceId); return true; }).ConfigureAwait(false);

            return captured || (captureSignal.Task.IsCompletedSuccessfully && captureSignal.Task.Result);
        }
        finally
        {
            lock (_captureSignals)
            {
                if (_captureSignals.TryGetValue(request.InstanceId, out var current)
                    && ReferenceEquals(current, captureSignal))
                {
                    _captureSignals.Remove(request.InstanceId);
                }
            }
        }
    }

    private async Task CreateWindowAsync(WebLoginCaptureRequest request, bool hidden)
    {
        var definition = Definition(request.ProviderType);
        var title = $"{Catalog.ProviderName(request.ProviderType)} Login";
        var url = request.LoginUrl;
        var initScript = definition.InitScript;
        var injectCaptureScript = ShouldInjectCaptureScript(request, hidden);

        var window = new ProviderLoginWindow(title);
        lock (_windows)
            _windows[request.InstanceId] = window;

        // If the user closes the window, drop our reference so the poll loop sees it gone.
        window.Closed += (_, _) =>
        {
            lock (_windows)
            {
                if (_windows.TryGetValue(request.InstanceId, out var w) && ReferenceEquals(w, window))
                    _windows.Remove(request.InstanceId);
            }
        };

        Directory.CreateDirectory(request.UserDataFolder);
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, request.UserDataFolder, null);
        await window.WebView.EnsureCoreWebView2Async(env);

        var core = window.WebView.CoreWebView2;

        core.WebMessageReceived += (_, args) => HandleWebMessage(request, args.WebMessageAsJson);
        core.WebResourceResponseReceived += (_, args) => HandleWebResourceResponseReceived(request, args);
        AttachVisibleCookieLoginClose(core, request, hidden);
        AttachNativeCookieCapture(core, request);

        // Inject the page-side bridge + mode flag + init script BEFORE document scripts run.
        // AddScriptToExecuteOnDocumentCreatedAsync applies to future navigations, so the mode must
        // be available to provider scripts on every login redirect/page reload.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebMessageBridgeScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebLoginModeScript(hidden));
        if (injectCaptureScript)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(initScript);

        core.Navigate(url);

        if (hidden)
        {
            // A WinUI 3 WebView2 only runs once its host window is activated. For a hidden
            // auto-fetch we park the window off-screen and activate it so the page (and the
            // scrape) run without the user seeing it.
            window.MoveOffScreen();
            window.Activate();
        }
        else
        {
            window.Activate();
        }
    }

    private static bool ShouldInjectCaptureScript(WebLoginCaptureRequest request, bool hidden) =>
        ShouldInjectCaptureScriptForTesting(request.ProviderType, hidden);

    internal static bool ShouldInjectCaptureScriptForTesting(string providerType, bool hidden) =>
        hidden || !string.Equals(providerType, "alibaba", StringComparison.OrdinalIgnoreCase);

    private static int? PollBudgetFor(WebLoginCaptureRequest request, bool hidden) =>
        PollBudgetForTesting(request.ProviderType, hidden);

    internal static int? PollBudgetForTesting(string providerType, bool hidden)
    {
        if (!hidden)
            return null;

        // Alibaba/Aliyun frequently spends tens of seconds in SSO redirects and console
        // bootstrapping before the Bailian Coding Plan API is callable. The generic 18s
        // hidden budget was too short and made valid saved logins look like failures.
        return string.Equals(providerType, "alibaba", StringComparison.OrdinalIgnoreCase)
            ? 45
            : 9;
    }

    private void AttachVisibleCookieLoginClose(
        CoreWebView2 core,
        WebLoginCaptureRequest request,
        bool hidden)
    {
        if (hidden
            || ShouldInjectCaptureScript(request, hidden)
            || !string.Equals(request.ProviderType, "alibaba", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        core.NavigationCompleted += (_, _) =>
        {
            if (!IsAlibabaPostLoginLanding(core.Source))
                return;

            RunOnUiAsync(() =>
            {
                CloseWindow(request.InstanceId);
                return true;
            });
        };
    }

    internal static bool IsAlibabaPostLoginLandingForTesting(string sourceUrl) =>
        IsAlibabaPostLoginLanding(sourceUrl);

    private static bool IsAlibabaPostLoginLanding(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (IsAlibabaAuthenticationHost(uri.Host))
            return false;

        return IsAlibabaOwnedHost(uri.Host);
    }

    private static bool IsAlibabaAuthenticationHost(string host) =>
        HostEqualsOrEndsWith(host, "account.aliyun.com")
        || HostEqualsOrEndsWith(host, "account.alibabacloud.com")
        || HostEqualsOrEndsWith(host, "signin.alibabacloud.com")
        || HostEqualsOrEndsWith(host, "passport.aliyun.com")
        || HostEqualsOrEndsWith(host, "passport.alibabacloud.com");

    private static bool IsAlibabaOwnedHost(string host) =>
        HostEqualsOrEndsWith(host, "aliyun.com")
        || HostEqualsOrEndsWith(host, "alibabacloud.com");

    private static bool HostEqualsOrEndsWith(string host, string suffix) =>
        string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    private async void HandleWebResourceResponseReceived(
        WebLoginCaptureRequest request,
        CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (!IsNativeCapturedResponse(request.ProviderType, args.Request.Uri))
            return;

        try
        {
            var statusCode = args.Response.StatusCode;
            if (statusCode < 200 || statusCode >= 300)
                return;

            using var content = await args.Response.GetContentAsync();
            using var stream = content.AsStreamForRead();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var json = await reader.ReadToEndAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(json))
                return;

            TryCompleteCaptureFromJson(request, json, closeWindow: true);
        }
        catch
        {
            // Native response capture is a hardening path; the injected page script and URL
            // poll remain available if this response cannot be read.
        }
    }

    /// <summary>
    /// Response sniffing: when the logged-in dashboard page itself calls the same usage
    /// API the provider's parser understands, capture that response natively instead of
    /// waiting for the injected script. This closes the login window even when the page
    /// script cannot authenticate (e.g. the auth cookie is HttpOnly and invisible to
    /// document.cookie — the Kimi/Manus failure mode). A sniffed body that does not
    /// parse is simply ignored, so extra matches are harmless.
    /// </summary>
    private static bool IsNativeCapturedResponse(string providerType, string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        if (string.Equals(providerType, "alibaba", StringComparison.OrdinalIgnoreCase))
        {
            return uri.Contains("queryCodingPlanInstanceInfoV2", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("data/api.json", StringComparison.OrdinalIgnoreCase);
        }

        // kimi.com/code/console fetches GetUsages on load; ParseKimi accepts the raw body
        // (it only completes when a FEATURE_CODING scope entry is present).
        if (string.Equals(providerType, "kimi", StringComparison.OrdinalIgnoreCase))
            return uri.Contains("kimi.gateway.billing.v1.BillingService/GetUsages", StringComparison.OrdinalIgnoreCase);

        // manus.im dashboard calls the credits RPC; ParseManus accepts the raw body and
        // throws when every credit field is zero, so pre-login bodies never capture.
        if (string.Equals(providerType, "manus", StringComparison.OrdinalIgnoreCase))
            return uri.Contains("user.v1.UserService/GetAvailableCredits", StringComparison.OrdinalIgnoreCase);

        // windsurf.com plan page calls GetPlanStatus (ConnectRPC). HYPOTHESIS (untested,
        // no account): the web app uses the JSON codec; if it uses binary protobuf the
        // sniffed body fails JSON parsing and this path is a no-op.
        if (string.Equals(providerType, "windsurf", StringComparison.OrdinalIgnoreCase))
            return uri.Contains("SeatManagementService/GetPlanStatus", StringComparison.OrdinalIgnoreCase);

        return false;
    }

    // ---- native cookie capture ---------------------------------------------

    /// <summary>
    /// Native cookie capture: some providers keep their session token in an HttpOnly
    /// cookie that page scripts cannot read via document.cookie, so the injected capture
    /// script can never build the Authorization header the API requires and the login
    /// window never closes. CoreWebView2's CookieManager CAN read HttpOnly cookies, so
    /// the host reads the cookie and calls the usage API natively instead.
    /// </summary>
    private sealed record NativeCookieCaptureDefinition(
        string CookieUrl,
        string[] CookieNames,
        Func<string, HttpRequestMessage> BuildRequest);

    private static readonly IReadOnlyDictionary<string, NativeCookieCaptureDefinition> NativeCookieCaptures =
        new Dictionary<string, NativeCookieCaptureDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            // kimi-auth is HttpOnly; the billing gateway requires it as a Bearer header
            // (verified: cookieless/anonymous calls return 401 {"code":"unauthenticated"}).
            ["kimi"] = new(
                "https://www.kimi.com",
                new[] { "kimi-auth" },
                KimiNativeUsageRequest),
            // HYPOTHESIS (untested, no account): Manus keeps session_id/__Secure-session_id
            // HttpOnly and api.manus.im wants it as a Bearer header — same request the
            // injected script builds (from CodexBar ManusUsageFetcher). If wrong, the call
            // 401s and the other capture paths are unaffected.
            ["manus"] = new(
                "https://manus.im",
                new[] { "session_id", "sessionid", "__Secure-session_id" },
                ManusNativeCreditsRequest),
        };

    private static HttpRequestMessage KimiNativeUsageRequest(string token)
    {
        var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages")
        {
            Content = new StringContent("{\"scope\":[\"FEATURE_CODING\"]}", Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("connect-protocol-version", "1");
        req.Headers.TryAddWithoutValidation("x-msh-platform", "web");
        req.Headers.TryAddWithoutValidation("x-language", "en-US");
        return req;
    }

    private static HttpRequestMessage ManusNativeCreditsRequest(string token)
    {
        var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.manus.im/user.v1.UserService/GetAvailableCredits")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("connect-protocol-version", "1");
        return req;
    }

    internal static bool HasNativeCookieCaptureForTesting(string providerType) =>
        NativeCookieCaptures.ContainsKey(providerType);

    internal static HttpRequestMessage NativeCookieCaptureRequestForTesting(string providerType, string token) =>
        NativeCookieCaptures[providerType].BuildRequest(token);

    private void AttachNativeCookieCapture(CoreWebView2 core, WebLoginCaptureRequest request)
    {
        if (!NativeCookieCaptures.ContainsKey(request.ProviderType))
            return;

        // Fires on every login redirect; SPA logins without navigation are covered by
        // the poll loop calling TryNativeCookieCaptureAsync each iteration.
        core.NavigationCompleted += (_, _) => _ = TryNativeCookieCaptureAsync(request);
    }

    private async Task<bool> TryNativeCookieCaptureAsync(WebLoginCaptureRequest request)
    {
        if (!NativeCookieCaptures.TryGetValue(request.ProviderType, out var definition))
            return false;

        string? token;
        try
        {
            token = await RunOnUiAsync(async () =>
            {
                var core = GetCore(request.InstanceId);
                if (core is null)
                    return (string?)null;

                var cookies = await core.CookieManager.GetCookiesAsync(definition.CookieUrl);
                foreach (var name in definition.CookieNames)
                {
                    var match = cookies.FirstOrDefault(c =>
                        string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(match?.Value))
                        return match!.Value;
                }

                return (string?)null;
            }).ConfigureAwait(false);
        }
        catch
        {
            return false; // window torn down mid-read
        }

        if (string.IsNullOrWhiteSpace(token) || !ShouldAttemptNativeCookieCapture(request.InstanceId, token))
            return false;

        try
        {
            // Bounded below the 2s poll cadence's tolerance: a slow provider API must not
            // stall the URL-poll loop for the HttpClient's full 20s timeout.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var req = definition.BuildRequest(token);
            using var resp = await Http.Client.SendAsync(req, timeout.Token).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (status < 200 || status >= 300)
                return false;

            var json = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) || !TryCompleteCaptureFromJson(request, json, closeWindow: false))
                return false;

            await RunOnUiAsync(() => { CloseWindow(request.InstanceId); return true; }).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Network hiccups leave the script/poll capture paths untouched.
            return false;
        }
    }

    /// <summary>At most one attempt per token per 15s, immediately on a token change.</summary>
    private bool ShouldAttemptNativeCookieCapture(string instanceId, string token)
    {
        lock (_nativeCookieAttempts)
        {
            if (_nativeCookieAttempts.TryGetValue(instanceId, out var last)
                && string.Equals(last.Token, token, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow - last.At < TimeSpan.FromSeconds(15))
            {
                return false;
            }

            _nativeCookieAttempts[instanceId] = (token, DateTimeOffset.UtcNow);
            return true;
        }
    }

    private void HandleWebMessage(WebLoginCaptureRequest request, string messageJson)
    {
        if (WebMessageCaptureJson(messageJson) is { } json
            && TryCompleteCaptureFromJson(request, json, closeWindow: true))
        {
            return;
        }

        var href = WebMessageHref(messageJson);
        if (href is null)
            return;

        TryCompleteCaptureFromUrl(request, href, closeWindow: true);
    }

    private static string? WebMessageHref(string messageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return root.GetString();
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("href", out var href)
                && href.ValueKind == JsonValueKind.String)
            {
                return href.GetString();
            }
        }
        catch
        {
            // Ignore malformed page messages.
        }

        return null;
    }

    private static string? WebMessageCaptureJson(string messageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "quotalens-capture-json"
                && root.TryGetProperty("json", out var json)
                && json.ValueKind == JsonValueKind.String)
            {
                return json.GetString();
            }
        }
        catch
        {
            // Ignore malformed page messages.
        }

        return null;
    }

    private bool TryCompleteCaptureFromUrl(WebLoginCaptureRequest request, string urlStr, bool closeWindow)
    {
        if (!TryDecodeCaptureJsonFromUrl(urlStr, out var json))
            return false;

        return TryCompleteCaptureFromJson(request, json, closeWindow);
    }

    private bool TryCompleteCaptureFromJson(WebLoginCaptureRequest request, string json, bool closeWindow)
    {
        if (TryReadCaptureError(json) is { } error)
        {
            AppLog.Warn($"webcapture: {request.InstanceId} ({request.ProviderType}) error: {error}");
            StoreResult(
                request.InstanceId,
                ProviderSnapshot.ForError(
                    request.ProviderType,
                    Catalog.ProviderName(request.ProviderType),
                    SourceLabelFor(request.ProviderType),
                    error));
            SignalCapture(request.InstanceId);
            if (closeWindow)
                CloseWindow(request.InstanceId);
            return true;
        }

        var definition = Definition(request.ProviderType);

        ProviderSnapshot snapshot;
        try
        {
            snapshot = NormalizeSnapshot(request.ProviderType, definition.Parse(json));
        }
        catch (ProviderException)
        {
            return false; // parse_response failed: keep polling
        }

        StoreResult(request.InstanceId, snapshot);
        AppLog.Info(
            $"webcapture: {request.InstanceId} ({request.ProviderType}) captured " +
            $"used={snapshot.Primary.UsedPercent:F1}% balance={snapshot.Balance?.Total ?? 0:0.##}");
        SignalCapture(request.InstanceId);
        if (closeWindow)
            CloseWindow(request.InstanceId);
        return true;
    }

    private static string? TryReadCaptureError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var message = FirstString(doc.RootElement, "__quotalensError", "quotalensError");
            return string.IsNullOrWhiteSpace(message) ? null : message;
        }
        catch
        {
            return null;
        }
    }

    private void SignalCapture(string instanceId)
    {
        lock (_captureSignals)
        {
            if (_captureSignals.TryGetValue(instanceId, out var signal))
                signal.TrySetResult(true);
        }
    }

    private bool IsCaptureSignaled(string instanceId)
    {
        lock (_captureSignals)
            return _captureSignals.TryGetValue(instanceId, out var signal)
                && signal.Task.IsCompletedSuccessfully
                && signal.Task.Result;
    }

    /// <summary>
    /// Backup eval-fetch loop + hash-poll loop, faithful to main.rs.
    ///
    /// BayesDL: a backup fetch script is eval'd every 3s (up to 30 iterations) in case the
    /// init script failed; the URL hash is polled every 2s (up to 60 iterations).
    /// MiMo: hash poll only (the init script is the sole fetch path), 2s × 60.
    ///
    /// Both run concurrently. The poll loop owns completion.
    /// </summary>
    private async Task<bool> PollLoopAsync(WebLoginCaptureRequest request, int? maxIters)
    {
        // ---- backup eval-fetch (BayesDL only) ----
        var definition = Definition(request.ProviderType);
        if (!string.IsNullOrWhiteSpace(definition.BackupFetchScript))
        {
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    var alive = await RunOnUiAsync(async () =>
                    {
                        var core = GetCore(request.InstanceId);
                        if (core is null) return false;
                        try { await core.ExecuteScriptAsync(definition.BackupFetchScript); return true; }
                        catch { return false; }
                    }).ConfigureAwait(false);
                    if (!alive) break;
                }
            });
        }

        // ---- hash poll: every 2s, up to maxIters iterations ----
        for (var i = 0; !maxIters.HasValue || i < maxIters.Value; i++)
        {
            if (IsCaptureSignaled(request.InstanceId))
                return true;

            await Task.Delay(2000).ConfigureAwait(false);

            if (IsCaptureSignaled(request.InstanceId))
                return true;

            // Covers SPA logins that set the auth cookie without a navigation event.
            if (await TryNativeCookieCaptureAsync(request).ConfigureAwait(false))
                return true;

            // Read the live document URL (incl. fragment). location.href always reflects the
            // current hash, whereas CoreWebView2.Source can lag on fragment-only changes.
            var urlStr = await RunOnUiAsync(async () =>
            {
                var core = GetCore(request.InstanceId);
                if (core is null) return (string?)null;
                try
                {
                    var raw = await core.ExecuteScriptAsync("location.href");
                    // ExecuteScriptAsync returns a JSON-encoded string ("\"https://...#...\"").
                    return JsonSerializer.Deserialize<string>(raw);
                }
                catch
                {
                    return (string?)null;
                }
            }).ConfigureAwait(false);

            if (urlStr is null)
                return IsCaptureSignaled(request.InstanceId); // window gone

            if (TryCompleteCaptureFromUrl(request, urlStr, closeWindow: false))
            {
                await RunOnUiAsync(() => { CloseWindow(request.InstanceId); return true; }).ConfigureAwait(false);
                return true;
            }
        }

        // Iterations exhausted (or window gone): leave window for the user if visible.
        return IsCaptureSignaled(request.InstanceId);
    }

    internal void StoreResult(string instanceId, ProviderSnapshot snapshot)
    {
        var cached = CloneSnapshot(snapshot);
        lock (_cacheLock)
        {
            _cache[instanceId] = cached;
        }

        SaveCachedSnapshot(instanceId, cached);
    }

    private static async Task TryAttachAlibabaCloudBalanceAsync(
        string instanceId,
        string providerType,
        IConfig config,
        ProviderSnapshot snapshot)
    {
        if (!string.Equals(providerType, "alibaba", StringComparison.OrdinalIgnoreCase)
            || snapshot.Balance is not null
            || ProviderConfig.Scoped(instanceId, config, "alibabacloud_key_id") is null
            || ProviderConfig.Scoped(instanceId, config, "alibabacloud_key_secret") is null)
        {
            return;
        }

        try
        {
            var balanceSnapshot = await new AlibabaProvider()
                .FetchAsync(instanceId, config, CancellationToken.None)
                .ConfigureAwait(false);
            if (balanceSnapshot.Balance is { } balance)
                snapshot.Balance = balance;
        }
        catch (ProviderException)
        {
            // Balance enrichment is optional; coding-plan data should remain usable.
        }
    }

    public void RemoveInstanceData(string instanceId, string providerType)
    {
        lock (_cacheLock)
            _cache.Remove(instanceId);

        DeleteCachedSnapshot(instanceId);
        CloseLoginWindowBestEffort(instanceId);

        if (!string.Equals(instanceId, providerType, StringComparison.OrdinalIgnoreCase))
        {
            var profileRoot = ProfileRoot(_localAppDataDirectory);
            DeleteDirectoryBestEffort(
                profileRoot,
                ProfileFolderFor(instanceId, providerType, _localAppDataDirectory));
        }
    }

    // ---- UI-thread helpers ------------------------------------------------

    private CoreWebView2? GetCore(string instanceId)
    {
        lock (_windows)
            return _windows.TryGetValue(instanceId, out var w) ? w.WebView.CoreWebView2 : null;
    }

    private void CloseWindow(string instanceId)
    {
        ProviderLoginWindow? window;
        lock (_windows)
        {
            _windows.TryGetValue(instanceId, out window);
            _windows.Remove(instanceId);
        }
        lock (_nativeCookieAttempts)
            _nativeCookieAttempts.Remove(instanceId);
        try { window?.Close(); } catch { /* already closed */ }
    }

    private void CloseLoginWindowBestEffort(string instanceId)
    {
        if (_ui is null)
            return;

        if (_ui.HasThreadAccess)
        {
            CloseWindow(instanceId);
            return;
        }

        _ui.TryEnqueue(() => CloseWindow(instanceId));
    }

    /// <summary>Marshal a synchronous func onto the UI DispatcherQueue and await its result.</summary>
    private Task<T> RunOnUiAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_ui.HasThreadAccess)
        {
            try { tcs.SetResult(func()); }
            catch (Exception e) { tcs.SetException(e); }
            return tcs.Task;
        }
        var queued = _ui.TryEnqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        if (!queued)
            tcs.SetException(new InvalidOperationException("UI DispatcherQueue rejected the work item."));
        return tcs.Task;
    }

    /// <summary>Marshal an async func onto the UI DispatcherQueue and await its result.</summary>
    private Task<T> RunOnUiAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        async void Run()
        {
            try { tcs.SetResult(await func().ConfigureAwait(true)); }
            catch (Exception e) { tcs.SetException(e); }
        }

        if (_ui.HasThreadAccess)
        {
            Run();
            return tcs.Task;
        }
        var queued = _ui.TryEnqueue(Run);
        if (!queued)
            tcs.SetException(new InvalidOperationException("UI DispatcherQueue rejected the work item."));
        return tcs.Task;
    }

    // ---- decode helpers (port of base64_decode_urlsafe + urlencoding::decode) ----

    private static WebLoginProviderDefinition Definition(string providerType) =>
        Definitions.TryGetValue(providerType, out var definition)
            ? definition
            : throw new ArgumentException($"Unknown web-login provider: {providerType}");

    internal static bool TryDecodeCaptureJsonFromUrlForTesting(string url, out string json) =>
        TryDecodeCaptureJsonFromUrl(url, out json);

    private static bool TryDecodeCaptureJsonFromUrl(string urlStr, out string json)
    {
        json = "";
        var idx = urlStr.IndexOf("#__ql__", StringComparison.Ordinal);
        if (idx < 0)
            return false;

        var encoded = urlStr.Substring(idx + 7); // skip "#__ql__"

        // Diagnostic prefixes are skipped (logged in Rust); just continue polling.
        if (encoded.StartsWith("ERR_", StringComparison.Ordinal)
            || encoded.StartsWith("NODATA_", StringComparison.Ordinal)
            || encoded.StartsWith("HTTP_", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            json = Base64DecodeUrlSafe(UrlDecode(encoded));
            return true;
        }
        catch
        {
            json = "";
            return false;
        }
    }

    private static string PlaceholderError(string providerType) =>
        Definitions.ContainsKey(providerType)
            ? $"Login required - click to open {Catalog.ProviderName(providerType)} in browser"
            : "Login required";

    internal static string SourceLabelFor(string providerType) =>
        $"{Catalog.ProviderName(providerType)} WebView";

    internal static ProviderSnapshot NormalizeSnapshot(string providerType, ProviderSnapshot snapshot)
    {
        snapshot.ProviderId = providerType;
        return ProviderSnapshotMetadata.Apply(
            providerType,
            SourceLabelFor(providerType),
            Confidence.Official,
            snapshot,
            replaceSourceLabel: true);
    }

    private static WebLoginCaptureRequest CaptureRequest(
        string instanceId,
        string providerType,
        IConfig config,
        bool visibleLogin)
    {
        _ = Definition(providerType);
        var configuredUrl = ProviderConfig.Clean(config.GetScoped(instanceId, $"{providerType}_url"))
            ?? Catalog.DefaultLoginUrlFor(providerType)
            ?? throw new ArgumentException($"Web-login provider {providerType} has no catalog login URL.");
        var captureUrl = NormalizeProviderLoginUrl(providerType, configuredUrl);
        var loginUrl = LoginUrlForCapture(providerType, captureUrl, visibleLogin);
        return new WebLoginCaptureRequest(
            instanceId,
            providerType,
            loginUrl,
            captureUrl,
            ProfileFolderFor(instanceId, providerType));
    }

    private static string NormalizeProviderLoginUrl(string providerType, string configuredUrl)
    {
        if (!string.Equals(providerType, "bayesdl", StringComparison.OrdinalIgnoreCase))
            return configuredUrl;

        return IsLegacyBayesdlLoginUrl(configuredUrl)
            ? Catalog.DefaultLoginUrlFor("bayesdl") ?? configuredUrl
            : configuredUrl;
    }

    private static bool IsLegacyBayesdlLoginUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return string.Equals(uri.Host, "token.bayesdl.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string LoginUrlForCapture(string providerType, string configuredUrl, bool visibleLogin)
    {
        if (!visibleLogin || !string.Equals(providerType, "alibaba", StringComparison.OrdinalIgnoreCase))
            return configuredUrl;

        return AlibabaVisibleLoginUrl(configuredUrl);
    }

    internal static string AlibabaCloudSignInUrlForTesting(string callbackUrl) =>
        AlibabaVisibleLoginUrl(callbackUrl);

    private static string AlibabaVisibleLoginUrl(string callbackUrl)
    {
        if (callbackUrl.Contains("account.aliyun.com/login", StringComparison.OrdinalIgnoreCase)
            || callbackUrl.Contains("account.alibabacloud.com", StringComparison.OrdinalIgnoreCase)
            || callbackUrl.Contains("signin.alibabacloud.com", StringComparison.OrdinalIgnoreCase))
        {
            return callbackUrl;
        }

        return "https://account.aliyun.com/login/login.htm?oauth_callback=https%3A%2F%2Fwww.aliyun.com%2F";
    }

    internal static string ProfileFolderFor(string instanceId, string providerType)
        => ProfileFolderFor(instanceId, providerType, localAppDataDirectory: null);

    private static string ProfileFolderFor(
        string instanceId,
        string providerType,
        string? localAppDataDirectory)
    {
        ProviderInstanceIdentity.RequireValid(instanceId);
        var localAppData = localAppDataDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Path.GetTempPath();

        if (string.Equals(instanceId, providerType, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(localAppData, "com.quotalens.app", "EBWebView");

        return Path.Combine(
            ProfileRoot(localAppData),
            instanceId);
    }

    private static string ProfileRoot(string? localAppDataDirectory)
    {
        var localAppData = localAppDataDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, "QuotaLens", "WebView2Profiles");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "provider" : sanitized;
    }

    private static string DefaultCacheDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Path.GetTempPath();

        return Path.Combine(localAppData, "QuotaLens", "WebLoginCache");
    }

    private ProviderSnapshot? LoadCachedSnapshot(string instanceId, string? providerType)
    {
        if (_cacheDirectory is null)
            return null;

        var path = CachePathFor(instanceId);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var snapshot = DeserializeCachedSnapshot(json, providerType);
            if (snapshot?.Primary is null)
                return null;

            lock (_cacheLock)
                _cache[instanceId] = CloneSnapshot(snapshot);

            return CloneSnapshot(snapshot);
        }
        catch
        {
            return null;
        }
    }

    private void SaveCachedSnapshot(string instanceId, ProviderSnapshot snapshot)
    {
        if (_cacheDirectory is null)
            return;

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var providerType = ProviderTypeForSnapshot(instanceId, snapshot);
            var cached = new WebLoginCachedSnapshot(1, providerType, snapshot);
            File.WriteAllText(CachePathFor(instanceId), JsonSerializer.Serialize(cached, CacheJsonOptions));
        }
        catch
        {
            // Last-known WebView data is useful but non-critical; the next login can recreate it.
        }
    }

    private void DeleteCachedSnapshot(string instanceId)
    {
        if (_cacheDirectory is null)
            return;

        try
        {
            var path = CachePathFor(instanceId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The stale cache can be overwritten by the next successful login.
        }
    }

    private static void DeleteDirectoryBestEffort(string root, string path)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return;

            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            // WebView2 may still be releasing files; leaving the profile is safer than blocking deletion.
        }
    }

    private string CachePathFor(string instanceId) =>
        Path.Combine(_cacheDirectory!, SanitizePathSegment(instanceId) + ".json");

    private static ProviderSnapshot? DeserializeCachedSnapshot(string json, string? providerType)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var looksLikeEnvelope =
            HasProperty(root, nameof(WebLoginCachedSnapshot.Version)) ||
            HasProperty(root, nameof(WebLoginCachedSnapshot.ProviderType)) ||
            HasProperty(root, nameof(WebLoginCachedSnapshot.Snapshot));

        if (looksLikeEnvelope)
        {
            var envelope = JsonSerializer.Deserialize<WebLoginCachedSnapshot>(json, CacheJsonOptions);
            if (envelope?.Snapshot is null)
                return null;

            if (providerType is not null
                && !string.Equals(envelope.ProviderType, providerType, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return envelope.Snapshot;
        }

        var legacy = JsonSerializer.Deserialize<ProviderSnapshot>(json, CacheJsonOptions);
        return legacy is not null
            && (providerType is null || IsSnapshotForProvider(legacy, providerType))
            ? legacy
            : null;
    }

    private static bool HasProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsSnapshotForProvider(ProviderSnapshot snapshot, string providerType) =>
        string.Equals(ProviderTypeForSnapshot(providerType, snapshot), providerType, StringComparison.OrdinalIgnoreCase);

    private static string ProviderTypeForSnapshot(string instanceId, ProviderSnapshot snapshot)
    {
        var id = string.IsNullOrWhiteSpace(snapshot.ProviderId) ? instanceId : snapshot.ProviderId;
        return Catalog.ProviderTypeFromId(id);
    }

    private static ProviderSnapshot CloneSnapshot(ProviderSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, CacheJsonOptions);
        return JsonSerializer.Deserialize<ProviderSnapshot>(json, CacheJsonOptions) ?? new ProviderSnapshot();
    }

    private static double RefreshIntervalMs(IConfig config)
    {
        if (config is IConfigService configService)
            return configService.RefreshMs;

        var raw = config.Get("min_refresh_interval_secs", "1800");
        return int.TryParse(raw, out var seconds)
            ? Math.Max(30_000.0, seconds * 1000.0)
            : 1_800_000.0;
    }

    /// <summary>
    /// Port of urlencoding::decode: percent-decode (UTF-8), turning "%XX" into bytes and
    /// decoding the byte stream as UTF-8. On any malformed sequence, returns the input as-is
    /// (the Rust uses unwrap_or_else(|_| input)).
    /// </summary>
    private static string UrlDecode(string input)
    {
        try
        {
            var bytes = new List<byte>(input.Length);
            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (c == '%' && i + 2 < input.Length
                    && Uri.IsHexDigit(input[i + 1]) && Uri.IsHexDigit(input[i + 2]))
                {
                    bytes.Add((byte)((HexVal(input[i + 1]) << 4) | HexVal(input[i + 2])));
                    i += 2;
                }
                else
                {
                    // Non-escaped chars are ASCII for our payloads; encode defensively as UTF-8.
                    foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                        bytes.Add(b);
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        catch
        {
            return input;
        }
    }

    private static int HexVal(char c) =>
        c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a' + 10);

    /// <summary>
    /// Port of base64_decode_urlsafe (main.rs): map url-safe alphabet back to standard
    /// ('-'→'+', '_'→'/'), re-pad based on len % 4 (2→"==", 3→"="), standard-base64 decode,
    /// then UTF-8 decode. Throws on base64/utf8 failure (the Rust returns Err with those
    /// prefixes; callers treat any error the same — keep polling).
    /// </summary>
    private static string Base64DecodeUrlSafe(string encoded)
    {
        var cleaned = encoded.Replace('-', '+').Replace('_', '/');
        var padded = (cleaned.Length % 4) switch
        {
            2 => cleaned + "==",
            3 => cleaned + "=",
            _ => cleaned,
        };
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (Exception e)
        {
            throw new FormatException($"base64 error: {e.Message}", e);
        }
        return Encoding.UTF8.GetString(bytes); // UTF8 (no BOM) is what String::from_utf8 expects
    }

    // ---- BayesDL parse (port of bayesdl.rs parse_response) ----------------

    private sealed class BayesdlApiResponse
    {
        [JsonPropertyName("code")] public string Code { get; set; } = "";
        [JsonPropertyName("data")] public BayesdlData? Data { get; set; }
    }

    private sealed class BayesdlData
    {
        [JsonPropertyName("rows")] public List<BayesdlCombo>? Rows { get; set; }
        [JsonPropertyName("cost")] public BayesdlCostData? Cost { get; set; }
    }

    private sealed class BayesdlCombo
    {
        [JsonPropertyName("tokensTotal")] public double? TokensTotal { get; set; }
        [JsonPropertyName("tokensUse")] public double? TokensUse { get; set; }
        [JsonPropertyName("comboStartTime")] public string? ComboStartTime { get; set; }
        [JsonPropertyName("comboEndTime")] public string? ComboEndTime { get; set; }
        [JsonPropertyName("comboName")] public string? ComboName { get; set; }
        [JsonPropertyName("statusDict")] public BayesdlStatusDict? StatusDict { get; set; }
        [JsonPropertyName("comboAttributeDict")] public BayesdlStatusDict? ComboAttributeDict { get; set; }
        [JsonPropertyName("isCodingPlan")] public int? IsCodingPlan { get; set; }
    }

    private sealed class BayesdlStatusDict
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class BayesdlCostData
    {
        [JsonPropertyName("balance")] public double? Balance { get; set; }
        [JsonPropertyName("amountOwed")] public double? AmountOwed { get; set; }
        [JsonPropertyName("orderCost")] public double? OrderCost { get; set; }
        [JsonPropertyName("userRecharge")] public double? UserRecharge { get; set; }
        [JsonPropertyName("issuedCouponCount")] public double? IssuedCouponCount { get; set; }
        [JsonPropertyName("invoiceableTotalAmount")] public double? InvoiceableTotalAmount { get; set; }
    }

    internal static ProviderSnapshot ParseBayesdl(string json) => ParseBayesdl(json, DateTimeOffset.UtcNow);

    internal static ProviderSnapshot ParseBayesdl(string json, DateTimeOffset now)
    {
        BayesdlApiResponse? resp;
        try
        {
            resp = JsonSerializer.Deserialize<BayesdlApiResponse>(json);
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Invalid JSON: {e.Message}", e);
        }
        if (resp is null)
            throw new ProviderException("Parse error: Invalid JSON: null");

        var rows = resp.Data?.Rows ?? new List<BayesdlCombo>();
        var combo = rows.FirstOrDefault(row => IsActiveBayesdlCombo(row, now));

        double tokensTotal, tokensUsed, usedPercent;
        string comboName;
        string? endTime;
        string? resetDesc;

        if (combo is not null)
        {
            var tt = combo.TokensTotal ?? 0.0;
            var tu = combo.TokensUse ?? 0.0;
            tokensTotal = tt;
            tokensUsed = tu;
            usedPercent = tt > 0.0 ? Quota.UtilizationToUsedPercent(tu / tt) : 0.0;
            comboName = !string.IsNullOrWhiteSpace(combo.ComboName)
                ? combo.ComboName.Trim()
                : combo.ComboAttributeDict?.Name ?? "Unknown";
            endTime = combo.ComboEndTime;
            var status = combo.StatusDict?.Name ?? "Unknown";
            resetDesc = endTime is not null ? $"{status} resets {endTime}" : null;
        }
        else
        {
            tokensTotal = 0.0;
            tokensUsed = 0.0;
            usedPercent = 0.0;
            comboName = "No Plan";
            endTime = null;
            resetDesc = null;
        }

        // Financial balance = cost.balance - cost.amountOwed (may be negative).
        var cost = resp.Data?.Cost;
        var rawBalance = cost?.Balance ?? 0.0;
        var amountOwed = cost?.AmountOwed ?? 0.0;
        var financialBalance = rawBalance - amountOwed;

        var primaryLabel = comboName == "No Plan" ? "Token Quota" : comboName;
        var quotaUnit = combo?.IsCodingPlan == 1 ? "uses" : "tokens";

        return new ProviderSnapshot
        {
            ProviderId = "bayesdl",
            Name = combo is null ? "BayesDL" : $"BayesDL · {comboName}",
            PlanName = comboName == "No Plan" ? null : comboName,
            Primary = new RateWindow
            {
                Label = primaryLabel,
                Kind = combo is null ? RateWindowKind.Informational : RateWindowKind.Quota,
                Sensitivity = combo is null ? RateWindowSensitivity.Financial : RateWindowSensitivity.None,
                UsedPercent = usedPercent,
                ValueText = combo is null ? $"¥{Fmt2(financialBalance)}" : null,
                ResetsAt = endTime,
                ResetDescription =
                    $"{primaryLabel} ({Fmt0(tokensUsed)}/{Fmt0(tokensTotal)} {quotaUnit}) | ¥{Fmt2(financialBalance)} bal / ¥{Fmt2(amountOwed)} owed",
                WindowMinutes = null,
            },
            Secondary = combo is null
                ? null
                : new RateWindow
                {
                    Label = "Tokens",
                    UsedPercent = usedPercent,
                    ResetsAt = endTime,
                    ResetDescription = resetDesc,
                    WindowMinutes = null,
                },
            Tertiary = null,
            Balance = new BalanceInfo
            {
                Currency = "CNY",
                Total = financialBalance,
                Paid = 0.0,
                Granted = 0.0,
            },
            SourceLabel = "BayesDL WebView",
            Confidence = Confidence.Official,
            EntitlementStatus = combo is not null
                ? EntitlementStatus.Active
                : BayesdlInactiveEntitlement(rows, now),
            UpdatedAt = now,
            Error = null,
        };
    }

    // ---- MiMo parse (port of mimo.rs parse_response) ----------------------

    private sealed class MimoApiResponse
    {
        [JsonPropertyName("usage")] public MimoUsageResponse? Usage { get; set; }
        [JsonPropertyName("detail")] public MimoDetailResponse? Detail { get; set; }
        [JsonPropertyName("balance")] public MimoBalanceResponse? Balance { get; set; }
        [JsonPropertyName("code")] public long? Code { get; set; }
        [JsonPropertyName("data")] public MimoData? Data { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class MimoUsageResponse
    {
        [JsonPropertyName("code")] public long? Code { get; set; }
        [JsonPropertyName("data")] public MimoData? Data { get; set; }
    }

    private sealed class MimoDetailResponse
    {
        [JsonPropertyName("code")] public long? Code { get; set; }
        [JsonPropertyName("data")] public MimoDetailData? Data { get; set; }
    }

    private sealed class MimoBalanceResponse
    {
        [JsonPropertyName("code")] public long? Code { get; set; }
        [JsonPropertyName("data")] public JsonElement? Data { get; set; }
    }

    private sealed class MimoDetailData
    {
        [JsonPropertyName("planCode")] public string? PlanCode { get; set; }
        [JsonPropertyName("planName")] public string? PlanName { get; set; }
        [JsonPropertyName("currentPeriodEnd")] public string? CurrentPeriodEnd { get; set; }
        [JsonPropertyName("expired")] public bool? Expired { get; set; }
        [JsonPropertyName("enableAutoRenew")] public bool? EnableAutoRenew { get; set; }
    }

    private sealed class MimoData
    {
        [JsonPropertyName("monthUsage")] public MimoUsageGroup? MonthUsage { get; set; }
        [JsonPropertyName("usage")] public MimoUsageGroup? Usage { get; set; }
    }

    private sealed class MimoUsageGroup
    {
        [JsonPropertyName("percent")] public double? Percent { get; set; }
        [JsonPropertyName("items")] public List<MimoUsageItem>? Items { get; set; }
    }

    private sealed class MimoUsageItem
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("used")] public double? Used { get; set; }
        [JsonPropertyName("limit")] public double? Limit { get; set; }
        [JsonPropertyName("percent")] public double? Percent { get; set; }
    }

    private static MimoUsageItem? FindItem(MimoUsageGroup? group, string name) =>
        group?.Items?.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    private static (double Used, double Limit, double Pct) ItemUsedLimit(MimoUsageItem? item)
    {
        var used = item?.Used ?? 0.0;
        var limit = item?.Limit ?? 0.0;
        var pct = item?.Percent ?? (limit > 0.0 ? used / limit : 0.0);
        return (used, limit, pct);
    }

    internal static ProviderSnapshot ParseMimo(string json) =>
        ParseMimo(json, DateTimeOffset.UtcNow);

    internal static ProviderSnapshot ParseMimo(string json, DateTimeOffset now)
    {
        MimoApiResponse? resp;
        try
        {
            resp = JsonSerializer.Deserialize<MimoApiResponse>(json);
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Invalid JSON: {e.Message}", e);
        }
        if (resp is null)
            throw new ProviderException("Parse error: Invalid JSON: null");

        // usage_data = resp.usage.data OR the legacy top-level resp.data. Balance is
        // independently useful for accounts without an active token plan. Reject explicit
        // error envelopes even if they happen to retain a stale data object.
        var usageData = resp.Usage is { } usageResponse
            ? MimoSucceeded(usageResponse.Code) ? usageResponse.Data : null
            : MimoSucceeded(resp.Code) ? resp.Data : null;
        var detailData = resp.Detail is { } detailResponse && MimoSucceeded(detailResponse.Code)
            ? detailResponse.Data
            : null;
        var balance = MimoBalance(resp.Balance);

        if (usageData is null && balance is null && detailData is null)
            throw new ProviderException("Parse error: No MiMo usage, entitlement, or balance data");

        var planName = DisplayName(detailData?.PlanName ?? detailData?.PlanCode);
        var planCode = detailData?.PlanCode ?? "";
        var periodEnd = detailData?.CurrentPeriodEnd;
        var isExpired = IsMimoPlanExpired(detailData, now);

        var monthWindow = MimoWindow(
            FindItem(usageData?.MonthUsage, "month_total_token"),
            planName ?? "Token Plan",
            periodEnd);
        var planWindow = MimoWindow(
            FindItem(usageData?.Usage, "plan_total_token"),
            "Token Plan",
            periodEnd);
        var compensationWindow = MimoWindow(
            FindItem(usageData?.Usage, "compensation_total_token"),
            "Compensation",
            periodEnd);

        var primaryLabel = string.IsNullOrWhiteSpace(planName)
            ? "Token Plan"
            : planCode.Length == 0 || planCode.Equals(planName, StringComparison.OrdinalIgnoreCase)
                ? planName
                : $"{planName} ({planCode})";

        var windows = new[] { monthWindow, planWindow, compensationWindow }
            .Where(window => window is not null)
            .Cast<RateWindow>()
            .ToList();
        var noQuotaWindow = balance is not null
            ? new RateWindow
            {
                Label = "Balance",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Financial,
                ValueText = $"{balance.Currency} {Fmt2(balance.Total)} remaining",
            }
            : new RateWindow
            {
                Label = "Plan status",
                Kind = RateWindowKind.Informational,
                ValueText = "Usage not reported",
                ResetsAt = periodEnd,
            };
        var primary = isExpired
            ? new RateWindow
            {
                Label = "Plan expired",
                UsedPercent = 100,
                ResetDescription = periodEnd is null ? "Expired" : $"Expired {periodEnd}",
            }
            : windows.FirstOrDefault() ?? noQuotaWindow;
        var remainingWindows = isExpired ? new List<RateWindow>() : windows.Skip(1).ToList();

        return new ProviderSnapshot
        {
            ProviderId = "mimo",
            Name = isExpired || string.IsNullOrWhiteSpace(planName) ? "MiMo" : $"MiMo · {planName}",
            PlanId = isExpired ? null : ProviderConfig.Clean(planCode),
            PlanName = isExpired ? null : planName,
            Primary = primary,
            Secondary = remainingWindows.ElementAtOrDefault(0),
            Tertiary = remainingWindows.ElementAtOrDefault(1),
            Balance = balance,
            SourceLabel = "MiMo WebView",
            Confidence = Confidence.Official,
            EntitlementStatus = isExpired
                ? EntitlementStatus.Expired
                : detailData is null || string.IsNullOrWhiteSpace(planName)
                    ? EntitlementStatus.Unknown
                    : EntitlementStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null,
        };
    }

    private static bool IsActiveBayesdlCombo(BayesdlCombo combo, DateTimeOffset now)
    {
        var status = combo.StatusDict?.Name?.Trim() ?? "";
        if (Regex.IsMatch(
                status,
                "expired|inactive|cancel(?:led|ed)?|ended|disabled|已?过期|失效|取消|停用|已结束",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (TryParseBayesdlDate(combo.ComboStartTime, out var startsAt) && startsAt > now)
            return false;
        if (TryParseBayesdlDate(combo.ComboEndTime, out var endsAt) && endsAt <= now)
            return false;
        return true;
    }

    private static EntitlementStatus BayesdlInactiveEntitlement(
        IReadOnlyList<BayesdlCombo> rows,
        DateTimeOffset now)
    {
        if (rows.Count == 0)
            return EntitlementStatus.Unknown;

        var hasScheduled = rows.Any(combo =>
            TryParseBayesdlDate(combo.ComboStartTime, out var startsAt)
            && startsAt > now);
        return hasScheduled ? EntitlementStatus.Unknown : EntitlementStatus.Expired;
    }

    private static bool TryParseBayesdlDate(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out parsed);

    private static BalanceInfo? MimoBalance(MimoBalanceResponse? response)
    {
        if (response is null
            || response.Code != 0
            || response.Data is not { ValueKind: JsonValueKind.Object } data
            || JDouble(data, "balance") is not { } total
            || !double.IsFinite(total))
        {
            return null;
        }

        var currency = JString(data, "currency");
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        var cash = JDouble(data, "cashBalance");
        var gift = JDouble(data, "giftBalance");
        return new BalanceInfo
        {
            Currency = currency,
            Total = total,
            Paid = cash is { } cashValue && double.IsFinite(cashValue) ? cashValue : 0,
            Granted = gift is { } giftValue && double.IsFinite(giftValue) ? giftValue : 0,
            PaidLabelKey = "card.cashBalance",
            GrantedLabelKey = "card.giftBalance",
        };
    }

    private static bool MimoSucceeded(long? code) => code is null or 0;

    private static bool IsMimoPlanExpired(MimoDetailData? detail, DateTimeOffset now)
    {
        if (detail?.Expired == true)
            return true;

        return DateTimeOffset.TryParse(
                detail?.CurrentPeriodEnd,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var periodEnd)
            && periodEnd <= now;
    }

    private static RateWindow? MimoWindow(MimoUsageItem? item, string label, string? periodEnd)
    {
        if (item is null || item.Limit is not > 0)
            return null;

        var (used, limit, percent) = ItemUsedLimit(item);
        var usedPercent = Quota.UtilizationToUsedPercent(percent);
        return new RateWindow
        {
            Label = label,
            UsedPercent = usedPercent,
            ResetsAt = periodEnd,
            ResetDescription = $"{FmtTokens(used)}/{FmtTokens(limit)} ({Fmt1(usedPercent)}%)",
        };
    }

    // mimo.rs fmt_tokens: >=1e9 "{:.1}B", >=1e6 "{:.1}M", >=1e3 "{:.1}K", else "{:.0}".
    private static string FmtTokens(double v)
    {
        if (v >= 1_000_000_000.0) return $"{Fmt1(v / 1_000_000_000.0)}B";
        if (v >= 1_000_000.0) return $"{Fmt1(v / 1_000_000.0)}M";
        if (v >= 1_000.0) return $"{Fmt1(v / 1_000.0)}K";
        return Fmt0(v);
    }

    // ---- Kimi parse (from CodexBar KimiUsageSnapshot) --------------------

    private sealed class KimiUsageResponse
    {
        [JsonPropertyName("usages")] public List<KimiUsage>? Usages { get; set; }
    }

    private sealed class KimiUsage
    {
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("detail")] public KimiUsageDetail? Detail { get; set; }
        [JsonPropertyName("limits")] public List<KimiUsageLimit>? Limits { get; set; }
    }

    private sealed class KimiUsageLimit
    {
        [JsonPropertyName("window")] public KimiUsageWindow? Window { get; set; }
        [JsonPropertyName("detail")] public KimiUsageDetail? Detail { get; set; }
    }

    private sealed class KimiUsageWindow
    {
        [JsonPropertyName("duration")] public long? Duration { get; set; }
        [JsonPropertyName("timeUnit")] public string? TimeUnit { get; set; }
    }

    private sealed class KimiUsageDetail
    {
        [JsonPropertyName("limit")] public string? Limit { get; set; }
        [JsonPropertyName("used")] public string? Used { get; set; }
        [JsonPropertyName("remaining")] public string? Remaining { get; set; }
        [JsonPropertyName("resetTime")] public string? ResetTime { get; set; }
    }

    internal static ProviderSnapshot ParseKimi(string json)
    {
        KimiUsageResponse? resp;
        try
        {
            resp = JsonSerializer.Deserialize<KimiUsageResponse>(json);
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Invalid JSON: {e.Message}", e);
        }

        var codingUsage = resp?.Usages?.FirstOrDefault(u => u.Scope == "FEATURE_CODING")
            ?? throw new ProviderException("Parse error: FEATURE_CODING scope not found");
        var weekly = codingUsage.Detail
            ?? throw new ProviderException("Parse error: Weekly Kimi usage detail missing");

        var weeklyWindow = BuildKimiWindow(
            label: "Weekly Requests",
            detail: weekly,
            descriptionSuffix: "requests",
            windowMinutes: null,
            descriptionPrefix: null);

        var rateLimit = codingUsage.Limits?
            .OrderBy(l => l.Window?.Duration ?? long.MaxValue)
            .FirstOrDefault(l => l.Detail is not null);
        RateWindow? rateWindow = null;
        if (rateLimit?.Detail is not null)
        {
            var minutes = rateLimit.Window?.Duration;
            rateWindow = BuildKimiWindow(
                label: minutes == 300 ? "5h Rate Limit" : "Rate Limit",
                detail: rateLimit.Detail,
                descriptionSuffix: minutes == 300 ? "per 5 hours" : "requests",
                windowMinutes: minutes,
                descriptionPrefix: "Rate: ");
        }

        var tier = KimiTierName(ParseLong(weekly.Limit));
        return new ProviderSnapshot
        {
            ProviderId = "kimi",
            Name = string.IsNullOrWhiteSpace(tier) ? "Kimi" : $"Kimi · {tier}",
            PlanName = tier,
            Primary = weeklyWindow,
            Secondary = rateWindow,
            SourceLabel = "Kimi WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
            Error = null,
        };
    }

    private static RateWindow BuildKimiWindow(
        string label,
        KimiUsageDetail detail,
        string descriptionSuffix,
        long? windowMinutes,
        string? descriptionPrefix)
    {
        var limit = ParseLong(detail.Limit);
        var remaining = ParseLong(detail.Remaining);
        var used = ParseLong(detail.Used);
        if (used is null && limit is not null && remaining is not null)
            used = Math.Max(0, limit.Value - remaining.Value);

        var resolvedLimit = Math.Max(0, limit ?? 0);
        var resolvedUsed = Math.Max(0, used ?? 0);
        var usedPercent = resolvedLimit > 0
            ? Quota.UtilizationToUsedPercent((double)resolvedUsed / resolvedLimit)
            : 0.0;

        var prefix = descriptionPrefix ?? "";
        return new RateWindow
        {
            Label = label,
            UsedPercent = usedPercent,
            ResetsAt = detail.ResetTime,
            ResetDescription = $"{prefix}{resolvedUsed}/{resolvedLimit} {descriptionSuffix}",
            WindowMinutes = windowMinutes,
        };
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? KimiTierName(long? weeklyLimit) => weeklyLimit switch
    {
        1024 => "Andante",
        2048 => "Moderato",
        7168 => "Allegretto",
        _ => null,
    };

    // ---- Amp parse (from CodexBar AmpUsageSnapshot) ----------------------

    private sealed class AmpUsageCapture
    {
        [JsonPropertyName("freeQuota")] public double? FreeQuota { get; set; }
        [JsonPropertyName("freeUsed")] public double? FreeUsed { get; set; }
        [JsonPropertyName("hourlyReplenishment")] public double? HourlyReplenishment { get; set; }
        [JsonPropertyName("windowHours")] public double? WindowHours { get; set; }
        [JsonPropertyName("individualCredits")] public double? IndividualCredits { get; set; }
        [JsonPropertyName("workspaceCreditTotal")] public double? WorkspaceCreditTotal { get; set; }
        [JsonPropertyName("workspaceCount")] public int WorkspaceCount { get; set; }
        [JsonPropertyName("workspaceCredits")] public List<double>? WorkspaceCredits { get; set; }
        [JsonPropertyName("workspaceBalances")] public List<AmpWorkspaceBalanceCapture>? WorkspaceBalances { get; set; }
        [JsonPropertyName("subscription")] public AmpSubscriptionCapture? Subscription { get; set; }
    }

    private sealed class AmpWorkspaceBalanceCapture
    {
        [JsonPropertyName("remaining")] public double Remaining { get; set; }
    }

    private sealed class AmpSubscriptionCapture
    {
        [JsonPropertyName("plan")] public string? Plan { get; set; }
        [JsonPropertyName("otherRemainingPercent")] public double? OtherRemainingPercent { get; set; }
        [JsonPropertyName("orbRemainingPercent")] public double? OrbRemainingPercent { get; set; }
        [JsonPropertyName("renewalDays")] public int? RenewalDays { get; set; }
        [JsonPropertyName("resetsAt")] public string? ResetsAt { get; set; }
    }

    internal static ProviderSnapshot ParseAmp(string json) => ParseAmp(json, DateTimeOffset.UtcNow);

    internal static ProviderSnapshot ParseAmp(string json, DateTimeOffset now)
    {
        AmpUsageCapture? usage;
        try
        {
            usage = JsonSerializer.Deserialize<AmpUsageCapture>(json);
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Invalid JSON: {e.Message}", e);
        }

        if (usage is null)
            throw new ProviderException("Parse error: Missing Amp usage data");

        var workspaceCredits = usage.WorkspaceCredits?.Where(double.IsFinite).Select(value => Math.Max(0, value)).ToList()
            ?? usage.WorkspaceBalances?.Select(item => Math.Max(0, item.Remaining)).ToList()
            ?? new List<double>();
        var workspaceTotal = Math.Max(0, usage.WorkspaceCreditTotal ?? workspaceCredits.Sum());
        var workspaceCount = Math.Max(usage.WorkspaceCount, workspaceCredits.Count);
        var individualCredits = usage.IndividualCredits is { } individual && double.IsFinite(individual)
            ? Math.Max(0, individual)
            : (double?)null;
        var totalCredits = (individualCredits ?? 0) + workspaceTotal;
        var hasCredits = individualCredits is not null || usage.WorkspaceCreditTotal is not null || workspaceCredits.Count > 0;
        var creditsWindow = hasCredits
            ? AmpCreditsWindow(totalCredits, individualCredits is not null, workspaceCount)
            : null;

        RateWindow? freeWindow = null;
        if (usage.FreeQuota is > 0 && usage.FreeUsed is { } freeUsed)
        {
            var quota = Math.Max(0, usage.FreeQuota.Value);
            var used = Math.Clamp(freeUsed, 0, quota);
            string? resetsAt = null;
            if (usage.HourlyReplenishment is > 0)
            {
                var hoursToFull = used / usage.HourlyReplenishment.Value;
                resetsAt = now.AddSeconds(Math.Max(0, hoursToFull * 3600.0)).ToString("O");
            }

            freeWindow = new RateWindow
            {
                Label = "Amp Free",
                UsedPercent = Quota.UtilizationToUsedPercent(used / quota),
                ResetsAt = resetsAt,
                ResetDescription = $"{Fmt1(used)}/{Fmt1(quota)} credits",
                WindowMinutes = usage.WindowHours is > 0 ? (long?)Math.Round(usage.WindowHours.Value * 60.0) : null,
            };
        }

        var subscriptionPlan = DisplayName(usage.Subscription?.Plan);
        var windows = AmpSubscriptionWindows(usage.Subscription, now);
        if (freeWindow is not null)
            windows.Add(freeWindow);
        if (creditsWindow is not null)
            windows.Add(creditsWindow);
        if (windows.Count == 0)
            throw new ProviderException("Parse error: Missing Amp usage data");

        return new ProviderSnapshot
        {
            ProviderId = "amp",
            Name = subscriptionPlan is not null
                ? $"Amp · {subscriptionPlan}"
                : freeWindow is not null ? "Amp · Free" : "Amp",
            PlanName = subscriptionPlan ?? (freeWindow is null ? null : "Free"),
            Primary = windows[0],
            Secondary = windows.ElementAtOrDefault(1),
            Tertiary = windows.ElementAtOrDefault(2),
            AdditionalWindows = windows.Skip(3).ToList(),
            Balance = hasCredits
                ? new BalanceInfo
                {
                    Currency = "USD",
                    Total = totalCredits,
                    Paid = totalCredits,
                    Granted = 0,
                }
                : null,
            SourceLabel = "Amp WebView",
            Confidence = Confidence.Official,
            UpdatedAt = now,
            Error = null,
        };
    }

    private static List<RateWindow> AmpSubscriptionWindows(AmpSubscriptionCapture? subscription, DateTimeOffset now)
    {
        var windows = new List<RateWindow>();
        if (subscription is null)
            return windows;

        var resetsAt = subscription.ResetsAt;
        if (string.IsNullOrWhiteSpace(resetsAt) && subscription.RenewalDays is >= 0)
            resetsAt = now.AddDays(subscription.RenewalDays.Value).ToString("O");
        var resetDescription = subscription.RenewalDays is { } renewalDays
            ? renewalDays == 1 ? "renews in 1 day" : $"renews in {renewalDays} days"
            : "resets upon renewal";

        AddWindow("Other usage", subscription.OtherRemainingPercent);
        AddWindow("Orb usage", subscription.OrbRemainingPercent);
        return windows;

        void AddWindow(string label, double? remainingPercent)
        {
            if (remainingPercent is not { } remaining || !double.IsFinite(remaining))
                return;

            windows.Add(new RateWindow
            {
                Label = label,
                UsedPercent = Quota.ClampPercent(100 - remaining),
                ResetsAt = resetsAt,
                ResetDescription = resetDescription,
                WindowMinutes = 30L * 24L * 60L,
            });
        }
    }

    private static RateWindow AmpCreditsWindow(double totalCredits, bool hasIndividualCredits, int workspaceCount)
    {
        var parts = new List<string>();
        if (hasIndividualCredits)
            parts.Add("individual");
        if (workspaceCount > 0)
            parts.Add(workspaceCount == 1 ? "1 workspace" : $"{workspaceCount} workspaces");
        var suffix = parts.Count == 0 ? "" : $" · {string.Join(" + ", parts)}";
        return new RateWindow
        {
            Label = "Credits",
            Kind = RateWindowKind.Informational,
            Sensitivity = RateWindowSensitivity.Financial,
            UsedPercent = 0,
            ValueText = $"${totalCredits.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)} remaining{suffix}",
        };
    }

    // ---- Cursor parse (from CodexBar CursorStatusProbe) ------------------

    internal static ProviderSnapshot ParseCursor(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var summary = FirstObject(root, "usageSummary", "summary") ?? root;
        var userInfo = FirstObject(root, "userInfo", "user") ?? root;
        var requestUsage = FirstObject(root, "requestUsage", "usage");

        var individual = FirstObject(summary, "individualUsage");
        var team = FirstObject(summary, "teamUsage");
        var plan = FirstObject(individual, "plan");
        var overall = FirstObject(individual, "overall");
        var pooled = FirstObject(team, "pooled");

        var planUsedRaw = JDouble(plan, "used") ?? 0;
        var planLimitRaw = JDouble(plan, "limit") ?? 0;
        var (planUsedUsd, planLimitUsd, planPercent) = CursorPlanUsage(plan, overall, pooled);

        var autoPercent = PercentScale(JDouble(plan, "autoPercentUsed"));
        var apiPercent = PercentScale(JDouble(plan, "apiPercentUsed"));
        var billingCycleEnd = JDateIso(summary, "billingCycleEnd");
        var membership = DisplayName(JString(summary, "membershipType") ?? JString(summary, "limitType"));
        var email = JString(userInfo, "email");

        var legacy = FirstObject(requestUsage, "gpt-4", "gpt4");
        var requestsUsed = JDouble(legacy, "numRequestsTotal") ?? JDouble(legacy, "numRequests");
        var requestsLimit = JDouble(legacy, "maxRequestUsage");

        RateWindow primary;
        if (requestsUsed is not null && requestsLimit is > 0)
        {
            primary = new RateWindow
            {
                Label = "Requests",
                UsedPercent = Quota.ClampPercent(requestsUsed.Value / requestsLimit.Value * 100),
                ResetsAt = billingCycleEnd,
                ResetDescription = $"{Fmt0(requestsUsed.Value)}/{Fmt0(requestsLimit.Value)} requests",
            };
        }
        else
        {
            primary = new RateWindow
            {
                Label = "Included plan",
                UsedPercent = planPercent,
                ResetsAt = billingCycleEnd,
                ResetDescription = planLimitUsd > 0
                    ? $"${Fmt2(planUsedUsd)} / ${Fmt2(planLimitUsd)} included"
                    : string.IsNullOrWhiteSpace(email) ? null : email,
            };
        }

        var onDemand = FirstObject(individual, "onDemand");
        var onDemandUsed = (JDouble(onDemand, "used") ?? 0) / 100.0;
        var onDemandLimit = JDouble(onDemand, "limit") is { } limit ? limit / 100.0 : (double?)null;

        return new ProviderSnapshot
        {
            ProviderId = "cursor",
            Name = string.IsNullOrWhiteSpace(membership) ? "Cursor" : $"Cursor · {membership}",
            PlanName = membership,
            Primary = primary,
            Secondary = autoPercent is null
                ? null
                : new RateWindow
                {
                    Label = "Auto + Composer",
                    UsedPercent = autoPercent.Value,
                    ResetsAt = billingCycleEnd,
                    ResetDescription = "Included plan lane",
                },
            Tertiary = apiPercent is null
                ? null
                : new RateWindow
                {
                    Label = "API",
                    UsedPercent = apiPercent.Value,
                    ResetsAt = billingCycleEnd,
                    ResetDescription = "Named model lane",
                },
            Balance = onDemandUsed > 0 || onDemandLimit is > 0
                ? new BalanceInfo
                {
                    Currency = "USD",
                    Total = Math.Max(0, (onDemandLimit ?? 0) - onDemandUsed),
                    Paid = onDemandUsed,
                    Granted = onDemandLimit ?? 0,
                }
                : null,
            SourceLabel = "Cursor WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static (double UsedUsd, double LimitUsd, double Percent) CursorPlanUsage(
        JsonElement? plan,
        JsonElement? overall,
        JsonElement? pooled)
    {
        var source = plan;
        if (!HasPositiveLimit(source))
            source = HasPositiveLimit(overall) ? overall : HasPositiveLimit(pooled) ? pooled : plan;

        var usedRaw = JDouble(source, "used") ?? 0;
        var limitRaw = JDouble(source, "limit") ?? 0;
        var totalPercent = PercentScale(JDouble(plan, "totalPercentUsed"));
        var autoPercent = PercentScale(JDouble(plan, "autoPercentUsed"));
        var apiPercent = PercentScale(JDouble(plan, "apiPercentUsed"));
        double? averagedLanePercent = autoPercent is not null && apiPercent is not null
            ? Quota.ClampPercent((autoPercent.Value + apiPercent.Value) / 2.0)
            : null;

        var percent = totalPercent
            ?? averagedLanePercent
            ?? apiPercent
            ?? autoPercent
            ?? (limitRaw > 0 ? Quota.ClampPercent(usedRaw / limitRaw * 100) : 0);
        return (usedRaw / 100.0, limitRaw / 100.0, percent);
    }

    private static bool HasPositiveLimit(JsonElement? obj) =>
        JDouble(obj, "limit") is > 0;

    private static double? PercentScale(double? value) =>
        value is null ? null : Quota.ClampPercent(value.Value);

    // ---- Augment parse (from CodexBar AugmentStatusProbe) ----------------

    internal static ProviderSnapshot ParseAugment(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var credits = FirstObject(root, "creditsResponse", "credits") ?? root;
        var subscription = FirstObject(root, "subscriptionResponse", "subscription") ?? root;

        var remaining = JDouble(credits, "usageUnitsRemaining")
            ?? JDouble(credits, "credits")
            ?? JDouble(credits, "creditsRemaining");
        var used = JDouble(credits, "usageUnitsConsumedThisBillingCycle")
            ?? JDouble(credits, "creditsUsed");
        var limit = JDouble(credits, "usageUnitsAvailable")
            ?? JDouble(credits, "creditsLimit");
        if (limit is null && remaining is not null && used is not null)
            limit = remaining.Value + used.Value;
        if (remaining is null && limit is not null && used is not null)
            remaining = Math.Max(0, limit.Value - used.Value);

        if (remaining is null && used is null && limit is null)
            throw new ProviderException("Parse error: Missing Augment credit fields");

        var resolvedUsed = Math.Max(0, used ?? Math.Max(0, (limit ?? 0) - (remaining ?? 0)));
        var resolvedLimit = Math.Max(0, limit ?? (resolvedUsed + Math.Max(0, remaining ?? 0)));
        var plan = DisplayName(JString(subscription, "planName") ?? JString(subscription, "accountPlan"));
        var email = JString(subscription, "email");
        var resetsAt = JDateIso(subscription, "billingPeriodEnd") ?? JDateIso(subscription, "currentPeriodEnd");

        return new ProviderSnapshot
        {
            ProviderId = "augment",
            Name = string.IsNullOrWhiteSpace(plan) ? "Augment" : $"Augment · {plan}",
            PlanName = plan,
            Primary = new RateWindow
            {
                Label = "Credits",
                UsedPercent = resolvedLimit > 0 ? Quota.ClampPercent(resolvedUsed / resolvedLimit * 100) : 0,
                ResetsAt = resetsAt,
                ResetDescription = resolvedLimit > 0
                    ? $"{Fmt1(resolvedUsed)} / {Fmt1(resolvedLimit)} credits"
                    : email,
            },
            Balance = remaining is null
                ? null
                : new BalanceInfo
                {
                    Currency = "credits",
                    Total = Math.Max(0, remaining.Value),
                    Paid = resolvedUsed,
                    Granted = resolvedLimit,
                },
            SourceLabel = "Augment WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- Factory parse (from CodexBar FactoryStatusProbe) ----------------

    internal static ProviderSnapshot ParseFactory(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var auth = FirstObject(root, "authInfo", "auth") ?? root;
        var usageResponse = FirstObject(root, "usageResponse", "usageData", "usage") ?? root;
        var billingLimits = FirstObject(root, "billingLimits", "limitsResponse");
        var limits = FirstObject(billingLimits, "limits") ?? FirstObject(root, "limits");

        var organization = FirstObject(auth, "organization");
        var subscription = FirstObject(organization, "subscription");
        var orb = FirstObject(subscription, "orbSubscription");
        var planObject = FirstObject(orb, "plan");
        var plan = DisplayName(JString(planObject, "name") ?? JString(subscription, "planName") ?? JString(root, "planName"));
        var tier = DisplayName(JString(subscription, "factoryTier") ?? JString(root, "tier"));
        var displayPlan = plan ?? tier;

        if (FirstObject(limits, "standard") is { } standardPool)
        {
            var extraBalanceCents = JDouble(billingLimits, "extraUsageBalanceCents");
            return new ProviderSnapshot
            {
                ProviderId = "factory",
                Name = string.IsNullOrWhiteSpace(displayPlan) ? "Factory" : $"Factory · {displayPlan}",
                PlanName = displayPlan,
                Primary = FactoryLimitRate("5h Window", FirstObject(standardPool, "fiveHour"), 5 * 60),
                Secondary = FactoryLimitRate("Weekly", FirstObject(standardPool, "weekly"), 7 * 24 * 60),
                Tertiary = FactoryLimitRate("Monthly", FirstObject(standardPool, "monthly"), 30 * 24 * 60),
                Balance = extraBalanceCents is null
                    ? null
                    : new BalanceInfo
                    {
                        Currency = "USD",
                        Total = Math.Max(0, extraBalanceCents.Value / 100.0),
                        Paid = 0,
                        Granted = Math.Max(0, extraBalanceCents.Value / 100.0),
                    },
                SourceLabel = "Factory WebView",
                Confidence = Confidence.Official,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        var usage = FirstObject(usageResponse, "usage") ?? usageResponse;
        var standard = FirstObject(usage, "standard")
            ?? throw new ProviderException("Parse error: Missing Factory usage fields");
        var premium = FirstObject(usage, "premium");
        var periodEnd = JDateIso(usage, "endDate") ?? JDateIso(usage, "periodEnd");

        return new ProviderSnapshot
        {
            ProviderId = "factory",
            Name = string.IsNullOrWhiteSpace(displayPlan) ? "Factory" : $"Factory · {displayPlan}",
            PlanName = displayPlan,
            Primary = FactoryTokenRate("Standard", standard, periodEnd),
            Secondary = premium is { } premiumObj ? FactoryTokenRate("Premium", premiumObj, periodEnd) : null,
            SourceLabel = "Factory WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static RateWindow FactoryLimitRate(string label, JsonElement? window, long windowMinutes)
    {
        var usedPercent = PercentScale(JDouble(window, "usedPercent")) ?? 0;
        var reset = FactoryWindowReset(window);
        return new RateWindow
        {
            Label = label,
            UsedPercent = usedPercent,
            ResetsAt = reset,
            ResetDescription = $"{Fmt0(usedPercent)}% used",
            WindowMinutes = windowMinutes,
        };
    }

    private static string? FactoryWindowReset(JsonElement? window)
    {
        var seconds = JDouble(window, "secondsRemaining") ?? JDouble(window, "resetInSec");
        if (seconds is > 0)
            return DateTimeOffset.UtcNow.AddSeconds(seconds.Value).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return JDateIso(window, "windowEnd") ?? JDateIso(window, "resetAt");
    }

    private static RateWindow FactoryTokenRate(string label, JsonElement usage, string? resetsAt)
    {
        var used = JDouble(usage, "userTokens") ?? JDouble(usage, "orgTotalTokensUsed") ?? 0;
        var allowance = JDouble(usage, "totalAllowance") ?? 0;
        var ratio = JDouble(usage, "usedRatio");
        var percent = FactoryTokenPercent(used, allowance, ratio);
        return new RateWindow
        {
            Label = label,
            UsedPercent = percent,
            ResetsAt = resetsAt,
            ResetDescription = allowance > 0
                ? $"{FmtTokens(used)}/{FmtTokens(allowance)} tokens"
                : $"{FmtTokens(used)} tokens",
        };
    }

    private static double FactoryTokenPercent(double used, double allowance, double? apiRatio)
    {
        const double unlimitedThreshold = 1_000_000_000_000;
        if (apiRatio is { } ratio && double.IsFinite(ratio))
        {
            if (ratio >= -0.001 && ratio <= 1.001)
                return Quota.ClampPercent(ratio * 100);
            if ((allowance <= 0 || allowance > unlimitedThreshold) && ratio >= -0.1 && ratio <= 100.1)
                return Quota.ClampPercent(ratio);
        }

        if (allowance > unlimitedThreshold)
            return Quota.ClampPercent(used / 100_000_000.0 * 100);
        return allowance > 0 ? Quota.ClampPercent(used / allowance * 100) : 0;
    }

    // ---- MiniMax parse (from CodexBar MiniMaxUsageParser) ----------------

    internal static ProviderSnapshot ParseMiniMax(string json) => ParseMiniMax(json, DateTimeOffset.UtcNow);

    internal static ProviderSnapshot ParseMiniMax(string json, DateTimeOffset now)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var data = FirstObject(root, "data") ?? root;

        if (MiniMaxParseStatusError(root, data) is { } statusError)
            throw statusError;

        var plan = MiniMaxPlanName(
            JString(data, "current_subscribe_title")
            ?? JString(data, "plan_name")
            ?? JString(data, "combo_title")
            ?? JString(data, "current_plan_title")
            ?? JString(FirstObject(data, "current_combo_card"), "title"));

        var services = MiniMaxServiceRates(data).ToList();
        if (services.Count == 0)
            services = MiniMaxModelRemainRates(data, now).ToList();

        if (services.Count == 0)
            throw new ProviderException("Parse error: Missing MiniMax coding plan data");

        plan ??= MiniMaxInferredPlan(data);

        var primary = services
            .OrderBy(rate => MiniMaxServicePriority(rate.Label))
            .ThenBy(rate => rate.WindowMinutes == 7 * 24 * 60 ? 1 : 0)
            .ThenBy(rate => rate.WindowMinutes ?? long.MaxValue)
            .First();
        var remaining = services
            .Where(rate => !ReferenceEquals(rate, primary))
            .OrderBy(rate => MiniMaxServicePriority(rate.Label))
            .ThenBy(rate => rate.WindowMinutes == 7 * 24 * 60 ? 0 : 1)
            .ThenBy(rate => rate.WindowMinutes ?? long.MaxValue)
            .ToList();
        var points = FirstDeepDouble(data, "points_balance", "point_balance", "credits_balance", "credit_balance");

        return new ProviderSnapshot
        {
            ProviderId = "minimax",
            Name = string.IsNullOrWhiteSpace(plan) ? "MiniMax" : $"MiniMax · {plan}",
            PlanName = plan,
            Primary = primary,
            Secondary = remaining.Count > 0 ? remaining[0] : null,
            Tertiary = remaining.Count > 1 ? remaining[1] : null,
            AdditionalWindows = remaining.Skip(2).ToList(),
            Balance = points is null
                ? null
                : new BalanceInfo
                {
                    Currency = "points",
                    Total = Math.Max(0, points.Value),
                    Paid = 0,
                    Granted = Math.Max(0, points.Value),
                },
            SourceLabel = "MiniMax WebView",
            Confidence = Confidence.Official,
            EntitlementStatus = string.IsNullOrWhiteSpace(plan)
                ? EntitlementStatus.Unknown
                : EntitlementStatus.Active,
            UpdatedAt = now,
        };
    }

    private static ProviderException? MiniMaxParseStatusError(JsonElement root, JsonElement data)
    {
        var baseResp = FirstObject(data, "base_resp") ?? FirstObject(root, "base_resp");
        var status = JDouble(baseResp, "status_code");
        if (status is null || status == 0)
            return null;

        var message = JString(baseResp, "status_msg") ?? $"status_code {Fmt0(status.Value)}";
        var lower = message.ToLowerInvariant();
        return status == 1004 || lower.Contains("cookie", StringComparison.Ordinal) || lower.Contains("login", StringComparison.Ordinal)
            ? new ProviderException("Login required: MiniMax session is invalid or expired")
            : new ProviderException($"Not available: MiniMax API error {message}");
    }

    private static IEnumerable<RateWindow> MiniMaxServiceRates(JsonElement data)
    {
        if (!data.TryGetProperty("services", out var services)
            || services.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var service in services.EnumerateArray())
        {
            var serviceType = JString(service, "service_type");
            var windowType = JString(service, "window_type") ?? "Window";
            var timeRange = JString(service, "time_range");
            var used = JDouble(service, "usage");
            var limit = JDouble(service, "limit");
            if (limit is not > 0 || used is null)
                continue;

            var percent = JDouble(service, "percent") ?? used.Value / limit.Value * 100.0;
            var reset = MiniMaxResetFromTimeRange(timeRange, windowType);
            var label = MiniMaxDisplayName(serviceType);
            yield return new RateWindow
            {
                Label = label,
                UsedPercent = Quota.ClampPercent(percent),
                ResetsAt = reset,
                ResetDescription = $"{Fmt0(used.Value)}/{Fmt0(limit.Value)} prompts · {windowType}",
                WindowMinutes = MiniMaxWindowMinutes(windowType),
            };
        }
    }

    private static IEnumerable<RateWindow> MiniMaxModelRemainRates(JsonElement data, DateTimeOffset now)
    {
        if (!data.TryGetProperty("model_remains", out var remains)
            || remains.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in remains.EnumerateArray())
        {
            var service = MiniMaxModelServiceName(JString(item, "model_name"));
            if (MiniMaxRemainRate(
                service,
                JDouble(item, "current_interval_total_count"),
                JDouble(item, "current_interval_usage_count"),
                JDouble(item, "current_interval_remaining_percent"),
                JDouble(item, "current_interval_status"),
                JDouble(item, "start_time"),
                JDouble(item, "end_time"),
                JDouble(item, "remains_time"),
                JDouble(item, "interval_boost_permill") ?? JDouble(item, "interval_boost_permille"),
                null,
                now) is { } intervalRate)
            {
                yield return intervalRate;
            }

            if (MiniMaxHasWeeklyWindow(JString(item, "model_name"))
                && MiniMaxRemainRate(
                    service,
                    JDouble(item, "current_weekly_total_count"),
                    JDouble(item, "current_weekly_usage_count"),
                    JDouble(item, "current_weekly_remaining_percent"),
                    JDouble(item, "current_weekly_status"),
                    JDouble(item, "weekly_start_time"),
                    JDouble(item, "weekly_end_time"),
                    JDouble(item, "weekly_remains_time"),
                    JDouble(item, "weekly_boost_permill") ?? JDouble(item, "weekly_boost_permille"),
                    "Weekly",
                    now) is { } weeklyRate)
            {
                yield return weeklyRate;
            }
        }
    }

    private static RateWindow? MiniMaxRemainRate(
        string service,
        double? total,
        double? remaining,
        double? remainingPercent,
        double? status,
        double? start,
        double? end,
        double? remainsSeconds,
        double? boostPermille,
        string? windowOverride,
        DateTimeOffset now)
    {
        var isWeekly = string.Equals(windowOverride, "Weekly", StringComparison.OrdinalIgnoreCase);
        var isGeneral = service.Equals("General", StringComparison.OrdinalIgnoreCase)
            || service.Equals("Text Generation", StringComparison.OrdinalIgnoreCase);
        var isUnlimited = status == 3 && isWeekly && isGeneral && remainingPercent is >= 100;
        var isUnavailablePlaceholder = !isUnlimited
            && status == 3
            && (total ?? 0) == 0
            && (remaining ?? 0) == 0
            && remainingPercent is >= 100;
        if (isUnavailablePlaceholder)
            return null;

        var startDate = MiniMaxEpochToDate(start);
        var endDate = MiniMaxEpochToDate(end);
        var windowMinutes = startDate is not null && endDate is not null && endDate > startDate
            ? (long?)Math.Round((endDate.Value - startDate.Value).TotalMinutes)
            : null;
        var reset = endDate is not null && endDate > now
            ? endDate
            : remainsSeconds is > 0
                ? now.AddSeconds(remainsSeconds.Value > 1_000_000 ? remainsSeconds.Value / 1000.0 : remainsSeconds.Value)
                : null;

        var windowType = windowOverride ?? MiniMaxWindowType(windowMinutes);
        if (isUnlimited)
        {
            return new RateWindow
            {
                Label = service,
                UsedPercent = 0,
                ResetsAt = null,
                ResetDescription = "Unlimited",
                WindowMinutes = windowMinutes ?? 7 * 24 * 60,
            };
        }

        double used;
        double limit;
        double usedPercent;
        if (remainingPercent is { } percent)
        {
            usedPercent = Quota.ClampPercent(100 - percent);
            limit = boostPermille is > 0 ? Math.Max(1, Math.Round(boostPermille.Value / 10.0)) : 100;
            used = Math.Round(usedPercent * limit / 100.0);
        }
        else if (total is > 0 && remaining is { } remainingCount)
        {
            limit = total.Value;
            used = Math.Max(0, limit - remainingCount);
            usedPercent = Quota.ClampPercent(used / limit * 100.0);
        }
        else
        {
            return null;
        }

        return new RateWindow
        {
            Label = service,
            UsedPercent = usedPercent,
            ResetsAt = reset?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ResetDescription = $"{Fmt0(used)}/{Fmt0(limit)} prompts · {windowType}",
            WindowMinutes = windowMinutes ?? (isWeekly ? 7 * 24 * 60 : null),
        };
    }

    private static string MiniMaxModelServiceName(string? modelName)
    {
        var lower = (modelName ?? "").ToLowerInvariant();
        if (lower == "general") return "General";
        if (MiniMaxIsTextGeneration(modelName)) return "Text Generation";
        if (lower.Contains("speech", StringComparison.Ordinal)) return "Text to Speech";
        if (lower.Contains("hailuo", StringComparison.Ordinal) && lower.Contains("fast", StringComparison.Ordinal)) return "Image to Video";
        if (lower.Contains("hailuo", StringComparison.Ordinal)) return "Text to Video";
        if (lower.StartsWith("image-", StringComparison.Ordinal)) return "Image Generation";
        if (lower.Contains("music", StringComparison.Ordinal)) return "Music Generation";
        return DisplayName(modelName) ?? "MiniMax";
    }

    private static bool MiniMaxIsTextGeneration(string? modelName)
    {
        var lower = (modelName ?? "").ToLowerInvariant();
        return lower.Contains("minimax-m", StringComparison.Ordinal) || lower.StartsWith("m2.", StringComparison.Ordinal);
    }

    private static bool MiniMaxHasWeeklyWindow(string? modelName) =>
        string.Equals(modelName?.Trim(), "general", StringComparison.OrdinalIgnoreCase)
        || MiniMaxIsTextGeneration(modelName);

    private static string MiniMaxDisplayName(string? serviceType) =>
        (serviceType ?? "").Trim().ToLowerInvariant() switch
        {
            "text-generation" or "text generation" => "Text Generation",
            "text-to-speech" or "text to speech" => "Text to Speech",
            "image" => "Image",
            "image-generation" or "image generation" => "Image Generation",
            "text-to-video" or "text to video" => "Text to Video",
            "image-to-video" or "image to video" => "Image to Video",
            "music-generation" or "music generation" => "Music Generation",
            "" => "MiniMax",
            var value => DisplayName(value) ?? serviceType ?? "MiniMax",
        };

    private static int MiniMaxServicePriority(string label) =>
        label.Equals("Text Generation", StringComparison.OrdinalIgnoreCase)
        || label.Equals("General", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static string? MiniMaxPlanName(string? raw)
    {
        var display = DisplayName(raw);
        if (display is null)
            return null;

        var match = Regex.Match(
            display,
            @"(?i)(?:token\s*plan\s*(?:·\s*)?|tokenplan)(plus|max|ultra)\b");
        return match.Success
            ? char.ToUpperInvariant(match.Groups[1].Value[0]) + match.Groups[1].Value[1..].ToLowerInvariant()
            : display;
    }

    private static string? MiniMaxInferredPlan(JsonElement data)
    {
        if (!data.TryGetProperty("model_remains", out var remains) || remains.ValueKind != JsonValueKind.Array)
            return null;

        var hasGeneral = false;
        var hasUnavailableVideo = false;
        foreach (var item in remains.EnumerateArray())
        {
            var model = JString(item, "model_name")?.Trim();
            hasGeneral |= MiniMaxHasWeeklyWindow(model);
            if (string.Equals(model, "video", StringComparison.OrdinalIgnoreCase)
                && JDouble(item, "current_interval_status") == 3
                && (JDouble(item, "current_interval_total_count") ?? 0) == 0
                && (JDouble(item, "current_interval_usage_count") ?? 0) == 0
                && JDouble(item, "current_interval_remaining_percent") is >= 100)
            {
                hasUnavailableVideo = true;
            }
        }
        return hasGeneral && hasUnavailableVideo ? "Plus" : null;
    }

    private static DateTimeOffset? MiniMaxEpochToDate(double? value)
    {
        if (value is not > 0)
            return null;
        var seconds = value.Value > 1_000_000_000_000 ? value.Value / 1000.0 : value.Value;
        return seconds > 1_000_000_000
            ? DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(seconds))
            : null;
    }

    private static string MiniMaxWindowType(long? windowMinutes)
    {
        if (windowMinutes is null or <= 0)
            return "Window";
        if (windowMinutes.Value >= 23 * 60 && windowMinutes.Value <= 25 * 60)
            return "Today";
        if (windowMinutes.Value % 60 == 0)
            return $"{windowMinutes.Value / 60} hours";
        return $"{windowMinutes.Value} minutes";
    }

    private static long? MiniMaxWindowMinutes(string? windowType)
    {
        var lower = (windowType ?? "").Trim().ToLowerInvariant();
        if (lower is "today" or "今日")
            return 24 * 60;
        var match = Regex.Match(lower, @"([0-9]+)\s*(hours?|hrs?|h|minutes?|mins?|m|days?|d)");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
            return null;
        var unit = match.Groups[2].Value;
        if (unit.StartsWith("d", StringComparison.Ordinal)) return value * 24 * 60;
        if (unit.StartsWith("h", StringComparison.Ordinal)) return value * 60;
        return value;
    }

    private static string? MiniMaxResetFromTimeRange(string? timeRange, string? windowType)
    {
        if (string.IsNullOrWhiteSpace(timeRange))
            return null;

        var lowerWindow = (windowType ?? "").ToLowerInvariant();
        if (lowerWindow == "today")
        {
            var parts = timeRange.Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && DateTimeOffset.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var rangeParts = timeRange.Split('-', StringSplitOptions.TrimEntries);
        if (rangeParts.Length >= 2)
        {
            var end = Regex.Replace(rangeParts[^1], @"\(.*\)", "").Trim();
            if (TimeSpan.TryParse(end, System.Globalization.CultureInfo.InvariantCulture, out var time))
            {
                var now = DateTimeOffset.UtcNow;
                var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hours, time.Minutes, 0, TimeSpan.Zero);
                if (candidate < now)
                    candidate = candidate.AddDays(1);
                return candidate.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    // ---- Windsurf parse (from CodexBar WindsurfStatusProbe/WebFetcher) ----

    internal static ProviderSnapshot ParseWindsurf(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var data = FirstObject(root, "data") ?? root;
        var planStatus = FirstObject(data, "planStatus", "plan_status") ?? data;
        var planInfo = FirstObject(planStatus, "planInfo", "plan_info");
        var quotaUsage = FirstObject(data, "quotaUsage", "quota_usage");
        var usage = FirstObject(data, "usage");

        var plan = DisplayName(
            JString(planInfo, "planName")
            ?? JString(planInfo, "plan_name")
            ?? JString(data, "planName")
            ?? JString(data, "plan_name"));

        var primary = WindsurfQuotaWindow(
            "Daily",
            JDouble(planStatus, "dailyQuotaRemainingPercent") ?? JDouble(quotaUsage, "dailyRemainingPercent"),
            JDouble(planStatus, "dailyQuotaResetAtUnix") ?? JDouble(quotaUsage, "dailyResetAtUnix"),
            JDateIso(planStatus, "dailyQuotaResetAt") ?? JDateIso(quotaUsage, "dailyResetAt"));

        var secondary = WindsurfQuotaWindow(
            "Weekly",
            JDouble(planStatus, "weeklyQuotaRemainingPercent") ?? JDouble(quotaUsage, "weeklyRemainingPercent"),
            JDouble(planStatus, "weeklyQuotaResetAtUnix") ?? JDouble(quotaUsage, "weeklyResetAtUnix"),
            JDateIso(planStatus, "weeklyQuotaResetAt") ?? JDateIso(quotaUsage, "weeklyResetAt"));

        primary ??= WindsurfUsageWindow(
            "Messages",
            JDouble(usage, "usedMessages"),
            JDouble(usage, "remainingMessages"),
            JDouble(usage, "messages"),
            "messages");

        secondary ??= WindsurfUsageWindow(
            "Flow actions",
            JDouble(usage, "usedFlowActions"),
            JDouble(usage, "remainingFlowActions"),
            JDouble(usage, "flowActions"),
            "flow actions");

        if (primary is null && secondary is null)
            throw new ProviderException("Parse error: Missing Windsurf quota data");

        return new ProviderSnapshot
        {
            ProviderId = "windsurf",
            Name = string.IsNullOrWhiteSpace(plan) ? "Windsurf" : $"Windsurf · {plan}",
            PlanName = plan,
            Primary = primary ?? new RateWindow { Label = "Daily", UsedPercent = 0 },
            Secondary = secondary,
            SourceLabel = "Windsurf WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static RateWindow? WindsurfQuotaWindow(string label, double? remainingPercent, double? resetUnix, string? resetIso)
    {
        if (remainingPercent is null)
            return null;

        var remaining = Quota.ClampPercent(remainingPercent.Value);
        return new RateWindow
        {
            Label = label,
            UsedPercent = Quota.ClampPercent(100 - remaining),
            ResetsAt = EpochSecondsToIso(resetUnix) ?? resetIso,
            ResetDescription = $"{Fmt0(remaining)}% remaining",
        };
    }

    private static RateWindow? WindsurfUsageWindow(string label, double? rawUsed, double? rawRemaining, double? total, string unit)
    {
        if (total is not > 0)
            return null;

        var used = rawUsed ?? (rawRemaining is { } remaining ? Math.Max(0, total.Value - remaining) : null);
        if (used is null)
            return null;

        var clampedUsed = Math.Clamp(used.Value, 0, total.Value);
        return new RateWindow
        {
            Label = label,
            UsedPercent = Quota.ClampPercent(clampedUsed / total.Value * 100.0),
            ResetDescription = $"{Fmt0(clampedUsed)} / {Fmt0(total.Value)} {unit}",
        };
    }

    // ---- Manus parse (from CodexBar ManusUsageFetcher) -------------------

    internal static ProviderSnapshot ParseManus(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var data = FirstObject(root, "data", "result", "response", "availableCredits") ?? root;
        var totalCredits = JDouble(data, "totalCredits") ?? 0;
        var freeCredits = JDouble(data, "freeCredits") ?? 0;
        var periodicCredits = JDouble(data, "periodicCredits") ?? 0;
        var addonCredits = JDouble(data, "addonCredits") ?? 0;
        var refreshCredits = JDouble(data, "refreshCredits") ?? 0;
        var maxRefreshCredits = JDouble(data, "maxRefreshCredits") ?? 0;
        var proMonthlyCredits = JDouble(data, "proMonthlyCredits") ?? 0;
        var eventCredits = JDouble(data, "eventCredits") ?? 0;
        if (totalCredits == 0 && freeCredits == 0 && periodicCredits == 0 && addonCredits == 0
            && refreshCredits == 0 && maxRefreshCredits == 0 && proMonthlyCredits == 0 && eventCredits == 0)
        {
            throw new ProviderException("Parse error: Missing Manus credits fields");
        }

        var primary = proMonthlyCredits > 0
            ? new RateWindow
            {
                Label = "Monthly credits",
                UsedPercent = Quota.ClampPercent((proMonthlyCredits - periodicCredits) / proMonthlyCredits * 100),
                ResetDescription = $"Total {Fmt0(totalCredits)} · Free {Fmt0(freeCredits)}",
            }
            : null;
        var secondary = maxRefreshCredits > 0
            ? new RateWindow
            {
                Label = "Refresh credits",
                UsedPercent = Quota.ClampPercent((maxRefreshCredits - refreshCredits) / maxRefreshCredits * 100),
                ResetsAt = JDateIso(data, "nextRefreshTime"),
                ResetDescription = $"{DisplayName(JString(data, "refreshInterval")) ?? "Refresh"}: {Fmt0(refreshCredits)} / {Fmt0(maxRefreshCredits)}",
            }
            : null;

        return new ProviderSnapshot
        {
            ProviderId = "manus",
            Name = "Manus",
            Primary = primary ?? secondary ?? new RateWindow
            {
                Label = "Credits",
                UsedPercent = totalCredits > 0 ? 0 : 100,
                ResetDescription = $"{Fmt0(totalCredits)} credits",
            },
            Secondary = primary is not null ? secondary : null,
            Balance = new BalanceInfo
            {
                Currency = "credits",
                Total = totalCredits,
                Paid = addonCredits,
                Granted = totalCredits + eventCredits,
            },
            SourceLabel = "Manus WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- Perplexity parse (from CodexBar PerplexityUsageSnapshot) ---------

    internal static ProviderSnapshot ParsePerplexity(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var grants = root.TryGetProperty("credit_grants", out var grantArray) && grantArray.ValueKind == JsonValueKind.Array
            ? grantArray.EnumerateArray().ToList()
            : new List<JsonElement>();
        var now = DateTimeOffset.UtcNow;
        var totalUsageCents = JDouble(root, "total_usage_cents") ?? 0;
        var balanceCents = JDouble(root, "balance_cents") ?? 0;
        var renewalDate = EpochSecondsToIso(JDouble(root, "renewal_date_ts"));
        var purchasedFromField = Math.Max(0, JDouble(root, "current_period_purchased_cents") ?? 0);
        var recurringTotal = grants.Where(g => JString(g, "type") == "recurring").Sum(g => Math.Max(0, JDouble(g, "amount_cents") ?? 0));
        var promotional = grants
            .Where(g => JString(g, "type") == "promotional")
            .Where(g =>
            {
                var expires = JDouble(g, "expires_at_ts");
                return expires is null || DateTimeOffset.FromUnixTimeSeconds((long)expires.Value) > now;
            })
            .ToList();
        var promoTotal = promotional.Sum(g => Math.Max(0, JDouble(g, "amount_cents") ?? 0));
        var purchasedFromGrants = grants.Where(g => JString(g, "type") == "purchased").Sum(g => Math.Max(0, JDouble(g, "amount_cents") ?? 0));
        var purchasedTotal = Math.Max(purchasedFromField, purchasedFromGrants);

        var remainingUsage = totalUsageCents;
        var recurringUsed = Math.Min(remainingUsage, recurringTotal);
        remainingUsage -= recurringUsed;
        var purchasedUsed = Math.Min(remainingUsage, purchasedTotal);
        remainingUsage -= purchasedUsed;
        var promoUsed = Math.Min(remainingUsage, promoTotal);

        var hasFallbackCredits = promoTotal > 0 || purchasedTotal > 0;
        RateWindow? primary = null;
        if (recurringTotal > 0)
        {
            primary = new RateWindow
            {
                Label = "Recurring credits",
                UsedPercent = Quota.ClampPercent(recurringUsed / recurringTotal * 100),
                ResetsAt = renewalDate,
                ResetDescription = $"{Fmt0(recurringUsed)}/{Fmt0(recurringTotal)} credits",
            };
        }
        else if (!hasFallbackCredits)
        {
            primary = new RateWindow
            {
                Label = "Recurring credits",
                UsedPercent = 100,
                ResetsAt = renewalDate,
                ResetDescription = "0/0 credits",
            };
        }

        var promoExpiration = promotional
            .Select(g => JDouble(g, "expires_at_ts"))
            .Where(v => v is > 0)
            .Select(v => EpochSecondsToIso(v))
            .OrderBy(v => v, StringComparer.Ordinal)
            .FirstOrDefault();
        return new ProviderSnapshot
        {
            ProviderId = "perplexity",
            // Recurring credit amounts are usage allowances, not authoritative
            // subscription identifiers. Keep the provider-only title until the
            // private response explicitly supplies a plan field backed by a fixture.
            Name = "Perplexity",
            Primary = primary ?? new RateWindow
            {
                Label = "Purchased credits",
                UsedPercent = purchasedTotal > 0 ? Quota.ClampPercent(purchasedUsed / purchasedTotal * 100) : 100,
                ResetDescription = $"{Fmt0(purchasedUsed)}/{Fmt0(purchasedTotal)} credits",
            },
            Secondary = new RateWindow
            {
                Label = "Bonus credits",
                UsedPercent = promoTotal > 0 ? Quota.ClampPercent(promoUsed / promoTotal * 100) : 100,
                ResetsAt = promoExpiration,
                ResetDescription = $"{Fmt0(promoUsed)}/{Fmt0(promoTotal)} bonus",
            },
            Tertiary = new RateWindow
            {
                Label = "Purchased credits",
                UsedPercent = purchasedTotal > 0 ? Quota.ClampPercent(purchasedUsed / purchasedTotal * 100) : 100,
                ResetDescription = $"{Fmt0(purchasedUsed)}/{Fmt0(purchasedTotal)} credits",
            },
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = Math.Max(0, balanceCents / 100.0),
                Paid = Math.Max(0, totalUsageCents / 100.0),
                Granted = Math.Max(0, (recurringTotal + promoTotal + purchasedTotal) / 100.0),
            },
            SourceLabel = "Perplexity WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- T3 Chat parse (from CodexBar T3ChatUsageParser) -----------------

    internal static ProviderSnapshot ParseT3Chat(string json)
    {
        var customer = FindT3CustomerData(json)
            ?? throw new ProviderException("Parse error: Missing T3 Chat customer data");
        var plan = DisplayName(JString(FirstObject(customer, "subscription"), "productName") ?? JString(customer, "subTier"));
        var baseReset = EpochMillisecondsToIso(JDouble(customer, "usageFourHourNextResetAt") ?? JDouble(customer, "usageWindowNextResetAt"));
        var subscription = FirstObject(customer, "subscription");
        var overageReset = subscription is null ? null : EpochMillisecondsToIso(JDouble(subscription.Value, "currentPeriodEnd"));
        var secondaryPct = JDouble(customer, "usageMonthPercentage") ?? JDouble(customer, "usagePeriodPercentage") ?? 0;

        return new ProviderSnapshot
        {
            ProviderId = "t3chat",
            Name = string.IsNullOrWhiteSpace(plan) ? "T3 Chat" : $"T3 Chat · {plan}",
            PlanName = plan,
            Primary = new RateWindow
            {
                Label = "Base",
                UsedPercent = Quota.ClampPercent(JDouble(customer, "usageFourHourPercentage") ?? 0),
                ResetsAt = baseReset,
                ResetDescription = T3Description("Base", JString(customer, "usageBand")),
                WindowMinutes = 4 * 60,
            },
            Secondary = new RateWindow
            {
                Label = "Overage",
                UsedPercent = Quota.ClampPercent(secondaryPct),
                ResetsAt = overageReset,
                ResetDescription = "Overage",
            },
            SourceLabel = "T3 Chat WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- Command Code parse (from CodexBar CommandCodeUsageFetcher) -------

    internal static ProviderSnapshot ParseCommandCode(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var creditsRoot = FirstObject(root, "creditsResponse") ?? root;
        var credits = FirstObject(creditsRoot, "credits")
            ?? throw new ProviderException("Parse error: Missing Command Code credits object");
        var monthlyRemaining = JDouble(credits, "monthlyCredits")
            ?? throw new ProviderException("Parse error: Missing Command Code monthlyCredits");
        var purchasedCredits = JDouble(credits, "purchasedCredits") ?? 0;
        var subscriptionRoot = FirstObject(root, "subscriptionResponse");
        var subscriptionData = subscriptionRoot is null ? FirstObject(root, "data") : FirstObject(subscriptionRoot.Value, "data");
        var planId = JString(subscriptionData, "planId");
        var plan = CommandCodePlan(planId);
        var periodEnd = subscriptionData is null ? null : JDateIso(subscriptionData.Value, "currentPeriodEnd");
        var total = plan?.MonthlyCredits;
        var used = total is > 0 ? Math.Max(0, Math.Min(total.Value, total.Value - monthlyRemaining)) : 0;
        var description = plan is not null
            ? $"{CommandCodeUsd(used)} of {CommandCodeUsd(total!.Value)}"
            : monthlyRemaining > 0 ? $"{CommandCodeUsd(monthlyRemaining)} remaining" : null;
        if (purchasedCredits > 0)
            description = string.IsNullOrWhiteSpace(description)
                ? $"+ {CommandCodeUsd(purchasedCredits)} credits"
                : $"{description} · + {CommandCodeUsd(purchasedCredits)} credits";

        return new ProviderSnapshot
        {
            ProviderId = "commandcode",
            Name = plan is null ? "Command Code" : $"Command Code · {plan.Value.Name}",
            PlanName = plan?.Name,
            Primary = new RateWindow
            {
                Label = "Monthly credits",
                UsedPercent = total is > 0 ? Quota.ClampPercent(used / total.Value * 100) : monthlyRemaining > 0 || purchasedCredits > 0 ? 0 : 100,
                ResetsAt = periodEnd,
                ResetDescription = description,
            },
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = Math.Max(0, monthlyRemaining + purchasedCredits),
                Paid = Math.Max(0, used),
                Granted = Math.Max(0, total ?? monthlyRemaining + purchasedCredits),
            },
            SourceLabel = "Command Code WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- Ollama parse (from CodexBar OllamaUsageParser) ------------------

    internal static ProviderSnapshot ParseOllama(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var plan = DisplayName(JString(root, "planName"));
        var email = JString(root, "accountEmail");
        var session = JDouble(root, "sessionUsedPercent");
        var weekly = JDouble(root, "weeklyUsedPercent");

        if (session is null && weekly is null)
            throw new ProviderException("Parse error: Missing Ollama usage data");

        return new ProviderSnapshot
        {
            ProviderId = "ollama",
            Name = string.IsNullOrWhiteSpace(plan) ? "Ollama" : $"Ollama · {plan}",
            PlanName = plan,
            Primary = new RateWindow
            {
                Label = "Session",
                UsedPercent = Quota.ClampPercent(session ?? 0),
                ResetsAt = JDateIso(root, "sessionResetsAt"),
                ResetDescription = string.IsNullOrWhiteSpace(email) ? null : email,
                WindowMinutes = (long?)JDouble(root, "sessionWindowMinutes") ?? 5 * 60,
            },
            Secondary = weekly is null
                ? null
                : new RateWindow
                {
                    Label = "Weekly",
                    UsedPercent = Quota.ClampPercent(weekly.Value),
                    ResetsAt = JDateIso(root, "weeklyResetsAt"),
                    ResetDescription = "Weekly usage",
                    WindowMinutes = 7 * 24 * 60,
                },
            SourceLabel = "Ollama WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- Abacus AI parse (from CodexBar AbacusUsageFetcher) --------------

    internal static ProviderSnapshot ParseAbacus(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var computePoints = FirstObject(root, "computePoints", "computePointsResult", "result") ?? root;
        var billingInfo = FirstObject(root, "billingInfo", "billingInfoResult", "billing") ?? root;
        var total = JDouble(computePoints, "totalComputePoints")
            ?? JDouble(computePoints, "totalCredits")
            ?? JDouble(computePoints, "creditsTotal");
        var left = JDouble(computePoints, "computePointsLeft")
            ?? JDouble(computePoints, "creditsLeft")
            ?? JDouble(computePoints, "creditsRemaining");

        if (total is null || left is null)
            throw new ProviderException("Parse error: Missing Abacus AI credit fields");

        var used = Math.Max(0, total.Value - left.Value);
        var plan = DisplayName(JString(billingInfo, "currentTier") ?? JString(billingInfo, "planName"));
        var resetsAt = JDateIso(billingInfo, "nextBillingDate")
            ?? JDateIso(billingInfo, "currentPeriodEnd")
            ?? JDateIso(computePoints, "nextBillingDate");

        return new ProviderSnapshot
        {
            ProviderId = "abacus",
            Name = string.IsNullOrWhiteSpace(plan) ? "Abacus AI" : $"Abacus AI · {plan}",
            PlanName = plan,
            Primary = new RateWindow
            {
                Label = "Credits",
                UsedPercent = total.Value > 0 ? Quota.ClampPercent(used / total.Value * 100) : 0,
                ResetsAt = resetsAt,
                ResetDescription = $"{Fmt1(used)} / {Fmt1(total.Value)} credits",
                WindowMinutes = 30 * 24 * 60,
            },
            Balance = new BalanceInfo
            {
                Currency = "credits",
                Total = Math.Max(0, left.Value),
                Paid = used,
                Granted = total.Value,
            },
            SourceLabel = "Abacus AI WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- StepFun parse (from CodexBar StepFunUsageFetcher) ---------------

    internal static ProviderSnapshot ParseStepFun(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var payload = FirstObject(root, "data") ?? root;
        if ((JDouble(payload, "status") ?? JDouble(root, "status") ?? 1) != 1)
        {
            var message = JString(payload, "message") ?? JString(payload, "desc")
                ?? JString(root, "message") ?? JString(root, "desc") ?? "unknown";
            throw new ProviderException($"Not available: StepFun API error {message}");
        }

        var fiveHourLeft = JDouble(payload, "five_hour_usage_left_rate");
        var weeklyLeft = JDouble(payload, "weekly_usage_left_rate");
        var fiveHourReset = JDouble(payload, "five_hour_usage_reset_time");
        var weeklyReset = JDouble(payload, "weekly_usage_reset_time");
        var credit = FirstObject(payload, "plan_credit_rate_limit");
        var hasLiveWindow = fiveHourReset is > 0 || weeklyReset is > 0;
        var hasCreditPool = credit is { } creditObject && StepFunHasCreditPool(creditObject);
        var isCreditPlan = !hasLiveWindow && (hasCreditPool || JDouble(payload, "plan_family") == 2);
        var plan = DisplayName(
            JString(root, "planName") ?? JString(root, "plan_name")
            ?? JString(payload, "planName") ?? JString(payload, "plan_name"));

        RateWindow primary;
        RateWindow? secondary;
        if (isCreditPlan)
        {
            var creditLeft = credit is { } creditLimit ? StepFunCreditLeftRate(creditLimit) : null;
            if (creditLeft is null)
                throw new ProviderException("Parse error: Missing StepFun credit usage");

            primary = new RateWindow
            {
                Label = "Credits",
                UsedPercent = Quota.ClampPercent((1 - creditLeft.Value) * 100),
                ResetsAt = credit is { } creditSource ? StepFunCreditReset(creditSource) : null,
                ResetDescription = $"{Fmt1(Quota.ClampPercent(creditLeft.Value * 100))}% available",
            };
            secondary = null;
        }
        else
        {
            if (fiveHourLeft is null || weeklyLeft is null || fiveHourReset is null || weeklyReset is null)
                throw new ProviderException("Parse error: Missing StepFun usage rate or reset time fields");

            primary = new RateWindow
            {
                Label = "5h Window",
                UsedPercent = Quota.ClampPercent((1 - fiveHourLeft.Value) * 100),
                ResetsAt = EpochSecondsToIso(fiveHourReset),
                ResetDescription = $"{Fmt0(Quota.ClampPercent(fiveHourLeft.Value * 100))}% available",
                WindowMinutes = 5 * 60,
            };
            secondary = new RateWindow
            {
                Label = "Weekly Window",
                UsedPercent = Quota.ClampPercent((1 - weeklyLeft.Value) * 100),
                ResetsAt = EpochSecondsToIso(weeklyReset),
                ResetDescription = $"{Fmt0(Quota.ClampPercent(weeklyLeft.Value * 100))}% available",
                WindowMinutes = 7 * 24 * 60,
            };
        }

        return new ProviderSnapshot
        {
            ProviderId = "stepfun",
            Name = string.IsNullOrWhiteSpace(plan) ? "StepFun" : $"StepFun · {plan}",
            PlanName = plan,
            Primary = primary,
            Secondary = secondary,
            SourceLabel = "StepFun WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static bool StepFunHasCreditPool(JsonElement credit) =>
        JDouble(credit, "subscription_credit_left_rate") is not null
        || JDouble(credit, "topup_credit_left_rate") is not null
        || (TryGetProperty(credit, "credit_buckets", out var buckets)
            && buckets.ValueKind == JsonValueKind.Array
            && buckets.GetArrayLength() > 0);

    private static double? StepFunCreditLeftRate(JsonElement credit)
    {
        if (TryGetProperty(credit, "credit_buckets", out var buckets)
            && buckets.ValueKind == JsonValueKind.Array
            && buckets.GetArrayLength() > 0)
        {
            var total = 0.0;
            var residual = 0.0;
            var valid = true;
            foreach (var bucket in buckets.EnumerateArray())
            {
                var bucketTotal = JDouble(bucket, "credit_total");
                var bucketResidual = JDouble(bucket, "credit_residual");
                if (bucketTotal is not > 0
                    || bucketResidual is null
                    || !double.IsFinite(bucketTotal.Value)
                    || !double.IsFinite(bucketResidual.Value)
                    || bucketResidual < 0
                    || bucketResidual > bucketTotal)
                {
                    valid = false;
                    break;
                }
                total += bucketTotal.Value;
                residual += bucketResidual.Value;
            }
            if (valid && total > 0)
                return residual / total;
        }

        return JDouble(credit, "subscription_credit_left_rate")
            ?? JDouble(credit, "topup_credit_left_rate");
    }

    private static string? StepFunCreditReset(JsonElement credit)
    {
        var reset = JDouble(credit, "subscription_credit_reset_time");
        if (reset is > 0)
            return EpochSecondsToIso(reset);

        if (!TryGetProperty(credit, "credit_buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
            return null;
        var candidate = buckets.EnumerateArray()
            .Select(bucket => JDouble(bucket, "next_reset_at") ?? JDouble(bucket, "expire_at"))
            .Where(value => value is > 0)
            .Min();
        return EpochSecondsToIso(candidate);
    }

    // ---- OpenCode parse (from CodexBar OpenCodeUsageFetcher) -------------

    internal static ProviderSnapshot ParseOpenCode(string json)
    {
        using var doc = ParseDocument(json);
        var windows = ParseUsageWindows(doc.RootElement, includeMonthly: false);
        var rolling = windows.Rolling
            ?? throw new ProviderException("Parse error: Missing OpenCode rolling usage");
        var weekly = windows.Weekly
            ?? throw new ProviderException("Parse error: Missing OpenCode weekly usage");

        return new ProviderSnapshot
        {
            ProviderId = "opencode",
            Name = "OpenCode",
            Primary = UsageWindowRate("5h Window", rolling, 5 * 60),
            Secondary = UsageWindowRate("Weekly", weekly, 7 * 24 * 60),
            Tertiary = windows.RenewsAt is null ? null : RenewalWindow(windows.RenewsAt),
            SourceLabel = "OpenCode WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    // ---- OpenCode Go parse (from CodexBar OpenCodeGoUsageFetcher) --------

    internal static ProviderSnapshot ParseOpenCodeGo(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var windows = ParseUsageWindows(root, includeMonthly: true, preserveMissingNamedWindows: true);
        var rolling = windows.Rolling;
        var balance = OpenCodeGoBalance(root);
        if (rolling is null && balance is null)
            throw new ProviderException("Parse error: Missing OpenCode Go rolling usage or Zen balance");

        var balanceInfo = balance is null
            ? null
            : new BalanceInfo
            {
                Currency = "USD",
                Total = balance.Value,
                Paid = 0,
                Granted = balance.Value,
            };
        if (rolling is null)
        {
            return new ProviderSnapshot
            {
                ProviderId = "opencodego",
                Name = "OpenCode Go",
                Primary = new RateWindow
                {
                    Label = "Zen balance",
                    Kind = RateWindowKind.Informational,
                    Sensitivity = RateWindowSensitivity.Financial,
                    UsedPercent = 0,
                    ValueText = $"${Fmt2(balance!.Value)} remaining",
                },
                Balance = balanceInfo,
                SourceLabel = "OpenCode Go WebView",
                Confidence = Confidence.Official,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        var monthlyWindow = windows.Monthly is { } monthly
            ? UsageWindowRate("Monthly", monthly, 30 * 24 * 60)
            : null;
        if (monthlyWindow is not null)
            monthlyWindow.CountsForAvailability = true;

        return new ProviderSnapshot
        {
            ProviderId = "opencodego",
            Name = "OpenCode Go",
            PlanId = "opencode-go-recurring",
            PlanName = "Go",
            Primary = UsageWindowRate("5h Window", rolling, 5 * 60),
            Secondary = windows.Weekly is { } weekly
                ? UsageWindowRate("Weekly", weekly, 7 * 24 * 60)
                : null,
            Tertiary = monthlyWindow is not null
                ? monthlyWindow
                : windows.RenewsAt is null ? null : RenewalWindow(windows.RenewsAt),
            Balance = balanceInfo,
            SourceLabel = "OpenCode Go WebView",
            Confidence = Confidence.Official,
            EntitlementStatus = EntitlementStatus.Active,
            AvailabilityKind = ProviderAvailabilityKind.Finite,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static double? OpenCodeGoBalance(JsonElement root)
    {
        var normalized = FirstDeepDouble(root, "zenBalanceUSD", "zenBalance", "balanceUSD", "currentBalanceUSD");
        if (normalized is { } value && double.IsFinite(value))
            return Math.Max(0, value);

        var raw = OpenCodeGoRawBillingBalance(root);
        return raw is { } rawValue && double.IsFinite(rawValue)
            ? Math.Max(0, rawValue / 100_000_000.0)
            : null;
    }

    private static double? OpenCodeGoRawBillingBalance(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(JString(element, "customerID"))
                && JDouble(element, "balance") is { } rawBalance)
            {
                return rawBalance;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (OpenCodeGoRawBillingBalance(property.Value) is { } nested)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (OpenCodeGoRawBillingBalance(item) is { } nested)
                    return nested;
            }
        }
        return null;
    }

    // ---- Mistral parse (from CodexBar MistralUsageFetcher) ---------------

    internal static ProviderSnapshot ParseMistral(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var usage = FirstObject(root, "usage", "usageResponse") ?? root;
        var prices = MistralPrices(usage);
        var totals = new MistralTotals();
        MistralAggregateCategory(usage, prices, "completion", countsTokens: true, countCompletionModels: true, totals);
        MistralAggregateCategory(usage, prices, "ocr", countsTokens: false, countCompletionModels: false, totals);
        MistralAggregateCategory(usage, prices, "connectors", countsTokens: false, countCompletionModels: false, totals);
        MistralAggregateCategory(usage, prices, "audio", countsTokens: false, countCompletionModels: false, totals);

        if (FirstObject(usage, "libraries_api") is { } libraries)
        {
            MistralAggregateNestedModels(libraries, prices, "pages", countsTokens: false, countCompletionModels: false, totals);
            MistralAggregateNestedModels(libraries, prices, "tokens", countsTokens: true, countCompletionModels: false, totals);
        }

        if (FirstObject(usage, "fine_tuning") is { } fineTuning)
        {
            MistralAggregateModelMap(FirstObject(fineTuning, "training"), prices, countsTokens: false, countCompletionModels: false, totals);
            MistralAggregateModelMap(FirstObject(fineTuning, "storage"), prices, countsTokens: false, countCompletionModels: false, totals);
        }

        var currency = JString(usage, "currency") ?? "EUR";
        var symbol = JString(usage, "currency_symbol") ?? (currency.Equals("EUR", StringComparison.OrdinalIgnoreCase) ? "€" : "$");
        var totalTokens = totals.InputTokens + totals.CachedTokens + totals.OutputTokens;
        var reset = JDateIso(usage, "end_date");
        var vibeWindow = MistralVibeWindow(root);

        return new ProviderSnapshot
        {
            ProviderId = "mistral",
            Name = "Mistral",
            Primary = new RateWindow
            {
                Label = "Monthly spend",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Financial,
                UsedPercent = 0,
                ResetsAt = reset,
                ValueText = $"{symbol}{Math.Max(0, totals.Cost).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)} this month",
            },
            Secondary = new RateWindow
            {
                Label = "Tokens",
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Usage,
                UsedPercent = 0,
                ResetsAt = reset,
                ValueText = $"{Fmt0(totalTokens)} tokens · {totals.ModelCount} models",
            },
            AdditionalWindows = vibeWindow is null ? new List<RateWindow>() : new List<RateWindow> { vibeWindow },
            Balance = MistralBalance(root, currency),
            SourceLabel = "Mistral WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static BalanceInfo? MistralBalance(JsonElement root, string fallbackCurrency)
    {
        if (FirstObject(root, "credits", "creditsResponse") is not { } credits)
            return null;

        var payload = FirstObject(credits, "data") ?? credits;
        var walletValue = JDouble(payload, "wallet_amount") ?? JDouble(payload, "walletAmount");
        if (walletValue is not { } wallet)
            return null;

        var granted = JDouble(payload, "credit_notes_amount") ?? JDouble(payload, "creditNotesAmount") ?? 0;
        var ongoing = JDouble(payload, "ongoing_usage_balance") ?? JDouble(payload, "ongoingUsageBalance") ?? 0;
        if (!double.IsFinite(wallet) || !double.IsFinite(granted) || !double.IsFinite(ongoing))
            return null;

        var total = wallet + granted - ongoing;
        if (!double.IsFinite(total))
            return null;

        return new BalanceInfo
        {
            Currency = JString(payload, "currency") ?? fallbackCurrency,
            Total = Math.Max(0, total),
            Paid = wallet,
            Granted = granted,
        };
    }

    private static RateWindow? MistralVibeWindow(JsonElement root)
    {
        if (FirstObject(root, "vibe", "vibeUsage") is not { } vibe)
            return null;

        var usedPercent = JDouble(vibe, "usage_percentage") ?? JDouble(vibe, "usagePercentage");
        if (usedPercent is not { } percent || !double.IsFinite(percent) || percent is < 0 or > 100)
            return null;

        return new RateWindow
        {
            Label = "Monthly Plan",
            UsedPercent = percent,
            ResetsAt = JDateIso(vibe, "reset_at") ?? JDateIso(vibe, "resetAt"),
        };
    }

    internal static ProviderSnapshot ParseAlibabaTokenPlan(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;

        ThrowIfAlibabaTokenPlanError(root);

        if (AlibabaTokenPlanPersonalSnapshot(root) is { } personal)
            return personal;

        var total = FirstDeepDouble(root, AlibabaTokenPlanTotalKeys);
        var remaining = FirstDeepDouble(root, AlibabaTokenPlanRemainingKeys);
        var used = FirstDeepDouble(root, AlibabaTokenPlanUsedKeys);
        var totalCount = FirstDeepDouble(root, AlibabaTokenPlanCountKeys);
        var planName = FirstDeepString(root, AlibabaTokenPlanPlanKeys);
        var resetsAt = FirstDeepIso(root, AlibabaTokenPlanResetKeys);

        if (string.IsNullOrWhiteSpace(planName) && (totalCount is > 0 || total is not null))
            planName = "Token Plan";

        if (string.IsNullOrWhiteSpace(planName) && total is null && used is null && remaining is null && totalCount is null)
            throw new ProviderException("Parse error: Missing Alibaba Token Plan data.");

        var usedValue = used ?? (total is not null && remaining is not null ? Math.Max(0, total.Value - remaining.Value) : null);
        var usedPercent = total is > 0 && usedValue is not null
            ? Quota.ClampPercent(usedValue.Value / total.Value * 100)
            : 0;
        var detail = AlibabaTokenPlanQuotaDetail(used, total, remaining)
            ?? (totalCount is not null ? $"{Fmt0(totalCount.Value)} active subscriptions" : "Token Plan active");
        var displayPlan = DisplayName(planName);
        var balance = AlibabaTokenPlanBalance(usedValue, total, remaining);

        return new ProviderSnapshot
        {
            ProviderId = "alibabatokenplan",
            Name = displayPlan is null ? "Alibaba Token Plan" : $"Alibaba Token Plan · {displayPlan}",
            PlanName = displayPlan,
            Primary = new RateWindow
            {
                Label = "Credits",
                UsedPercent = usedPercent,
                ResetsAt = resetsAt,
                ResetDescription = detail,
                WindowMinutes = 30L * 24L * 60L,
            },
            Balance = balance,
            SourceLabel = "Alibaba Token Plan WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static ProviderSnapshot? AlibabaTokenPlanPersonalSnapshot(JsonElement root)
    {
        var usage = FirstObjectMatchingAnyKey(root, "per5HourPercentage", "per1WeekPercentage");
        if (usage is null)
            return null;

        var fiveHourRatio = JDouble(usage, "per5HourPercentage");
        var weeklyRatio = JDouble(usage, "per1WeekPercentage");
        if (fiveHourRatio is null && weeklyRatio is null)
            return null;

        var subscription = FirstObject(root, "subscription");
        var planCode = subscription is { } subscriptionPayload
            ? FirstDeepString(subscriptionPayload, "specCode", "spec_code", "planName", "plan_name")
            : FirstDeepString(root, "specCode", "spec_code");
        var plan = AlibabaTokenPlanPersonalPlanName(planCode);
        var quotaConfig = FirstObject(root, "quotaConfig", "quota_config");
        JsonElement? tierQuota = null;
        if (quotaConfig is { } config && !string.IsNullOrWhiteSpace(planCode))
            tierQuota = FirstObjectDeep(config, planCode.Trim().ToLowerInvariant());

        var fiveHourTotal = JDouble(tierQuota, "five_hour") ?? JDouble(tierQuota, "fiveHour");
        var weeklyTotal = JDouble(tierQuota, "weekly");
        var windows = new List<RateWindow>();
        if (fiveHourRatio is { } fiveHour)
        {
            var usedPercent = Quota.ClampPercent(fiveHour * 100);
            windows.Add(new RateWindow
            {
                Label = "5h Window",
                UsedPercent = usedPercent,
                ResetsAt = EpochMillisecondsToIso(JDouble(usage, "per5HourResetTime")),
                ResetDescription = AlibabaTokenPlanPersonalDetail(usedPercent, fiveHourTotal),
                WindowMinutes = 5 * 60,
            });
        }
        if (weeklyRatio is { } weekly)
        {
            var usedPercent = Quota.ClampPercent(weekly * 100);
            windows.Add(new RateWindow
            {
                Label = "Weekly",
                UsedPercent = usedPercent,
                ResetsAt = EpochMillisecondsToIso(JDouble(usage, "per1WeekResetTime")),
                ResetDescription = AlibabaTokenPlanPersonalDetail(usedPercent, weeklyTotal),
                WindowMinutes = 7 * 24 * 60,
            });
        }

        if (windows.Count == 0)
            return null;
        return new ProviderSnapshot
        {
            ProviderId = "alibabatokenplan",
            Name = $"Alibaba Token Plan · {plan}",
            PlanName = ProviderSnapshotIdentity.NormalizePlanName("Alibaba Token Plan", plan),
            Primary = windows[0],
            Secondary = windows.Count > 1 ? windows[1] : null,
            SourceLabel = "Alibaba Token Plan WebView",
            Confidence = Confidence.Official,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string AlibabaTokenPlanPersonalPlanName(string? planCode) =>
        (planCode ?? "").Trim().ToLowerInvariant() switch
        {
            "lite" => "Lite",
            "standard" => "Standard",
            "pro" => "Pro",
            "max" => "Max",
            _ => "Personal",
        };

    private static string AlibabaTokenPlanPersonalDetail(double usedPercent, double? total)
    {
        if (total is > 0)
            return $"{FmtCredits(total.Value * usedPercent / 100)} / {FmtCredits(total.Value)} credits used";
        return $"{Fmt2(usedPercent)}% used";
    }

    internal static ProviderSnapshot ParseAlibabaCodingPlan(string json)
    {
        using var doc = ParseDocument(json);
        var root = doc.RootElement;
        var updatedAt = DateTimeOffset.UtcNow;

        ThrowIfAlibabaCodingPlanError(root);

        var activeInstance = AlibabaCodingPlanActiveInstance(root, updatedAt);
        if (activeInstance is null && AlibabaCodingPlanAllInstancesExpired(root, updatedAt))
        {
            return new ProviderSnapshot
            {
                ProviderId = "alibaba",
                Name = "Alibaba",
                Primary = new RateWindow
                {
                    Label = "Plan expired",
                    UsedPercent = 100,
                    ResetDescription = "Expired",
                },
                Balance = AlibabaCloudBalance(root),
                SourceLabel = "Alibaba Coding Plan WebView",
                Confidence = Confidence.Official,
                EntitlementStatus = EntitlementStatus.Expired,
                UpdatedAt = updatedAt,
            };
        }

        var quotaSource = activeInstance is { } instanceWithQuota
            ? FirstObject(instanceWithQuota, "codingPlanQuotaInfo", "coding_plan_quota_info")
            : null;
        quotaSource ??= AlibabaCodingPlanQuotaSource(root);

        var planName = activeInstance is { } instance
            ? AlibabaCodingPlanName(instance) ?? AlibabaCodingPlanName(root)
            : AlibabaCodingPlanName(root);
        var displayPlan = DisplayName(planName);

        var windows = new List<RateWindow>();
        if (quotaSource is { } quota)
        {
            AddIfNotNull(windows, AlibabaCodingPlanWindow(
                "5h Pool",
                quota,
                AlibabaCodingPlanFiveHourUsedKeys,
                AlibabaCodingPlanFiveHourTotalKeys,
                AlibabaCodingPlanFiveHourResetKeys,
                5 * 60,
                normalizeFiveHourReset: true,
                updatedAt));
            AddIfNotNull(windows, AlibabaCodingPlanWindow(
                "Weekly",
                quota,
                AlibabaCodingPlanWeeklyUsedKeys,
                AlibabaCodingPlanWeeklyTotalKeys,
                AlibabaCodingPlanWeeklyResetKeys,
                7 * 24 * 60,
                normalizeFiveHourReset: false,
                updatedAt));
            AddIfNotNull(windows, AlibabaCodingPlanWindow(
                "Monthly",
                quota,
                AlibabaCodingPlanMonthlyUsedKeys,
                AlibabaCodingPlanMonthlyTotalKeys,
                AlibabaCodingPlanMonthlyResetKeys,
                30L * 24L * 60L,
                normalizeFiveHourReset: false,
                updatedAt));
        }

        if (windows.Count == 0)
        {
            var source = activeInstance ?? root;
            if (string.IsNullOrWhiteSpace(planName) || !AlibabaCodingPlanHasActiveSignal(source, root, updatedAt))
                throw new ProviderException("Parse error: Missing Alibaba Coding Plan quota data.");

            windows.Add(new RateWindow
            {
                Label = "Coding plan",
                UsedPercent = 0,
                ResetDescription = "Plan active",
            });
        }

        return new ProviderSnapshot
        {
            ProviderId = "alibaba",
            Name = displayPlan is null ? "Alibaba" : $"Alibaba · {displayPlan}",
            PlanName = displayPlan,
            Primary = windows[0],
            Secondary = windows.Count > 1 ? windows[1] : null,
            Tertiary = windows.Count > 2 ? windows[2] : null,
            Balance = AlibabaCloudBalance(root),
            SourceLabel = "Alibaba Coding Plan WebView",
            Confidence = Confidence.Official,
            EntitlementStatus = displayPlan is null
                ? EntitlementStatus.Unknown
                : EntitlementStatus.Active,
            UpdatedAt = updatedAt,
        };

        static void AddIfNotNull(List<RateWindow> windows, RateWindow? window)
        {
            if (window is not null)
                windows.Add(window);
        }
    }

    private sealed record UsageWindow(double UsedPercent, string? ResetsAt, string? ResetDescription);
    private sealed record UsageWindowSet(UsageWindow? Rolling, UsageWindow? Weekly, UsageWindow? Monthly, string? RenewsAt);
    private sealed record WindowCandidate(UsageWindow Window, string PathLower);

    private sealed class MistralTotals
    {
        public double Cost { get; set; }
        public long InputTokens { get; set; }
        public long CachedTokens { get; set; }
        public long OutputTokens { get; set; }
        public int ModelCount { get; set; }
    }

    private static readonly string[] AlibabaTokenPlanPlanKeys =
    {
        "planName", "plan_name", "packageName", "package_name", "commodityName", "commodity_name",
        "instanceName", "instance_name", "displayName", "display_name", "ProductName", "productName",
        "name", "title", "planType", "plan_type",
    };

    private static readonly string[] AlibabaTokenPlanUsedKeys =
    {
        "usedQuota", "used_quota", "usedCredits", "usedCredit", "consumedCredits", "usage", "used",
        "usedAmount", "consumeAmount", "usedValue", "UsedValue", "consumedValue", "ConsumedValue",
    };

    private static readonly string[] AlibabaTokenPlanTotalKeys =
    {
        "totalQuota", "total_quota", "totalCredits", "totalCredit", "quota", "creditLimit", "creditsTotal",
        "monthlyTotalQuota", "amount", "totalValue", "TotalValue",
    };

    private static readonly string[] AlibabaTokenPlanRemainingKeys =
    {
        "remainingQuota", "remainQuota", "remainingCredits", "remainingCredit", "availableCredits", "balance",
        "remaining", "availableAmount", "remainAmount", "totalSurplusValue", "TotalSurplusValue",
        "surplusValue", "SurplusValue",
    };

    private static readonly string[] AlibabaTokenPlanCountKeys =
    {
        "totalCount", "TotalCount", "subscriptionTotalNumber", "SubscriptionTotalNumber",
    };

    private static readonly string[] AlibabaTokenPlanResetKeys =
    {
        "nextRefreshTime", "resetTime", "periodEndTime", "billingCycleEnd", "billCycleEndTime", "expireTime",
        "expirationTime", "endTime", "validEndTime", "instanceEndTime", "nearestExpireDate", "NearestExpireDate",
    };

    private static readonly string[] AlibabaCloudBalanceAvailableKeys =
    {
        "AvailableAmount", "availableAmount", "available_amount",
    };

    private static readonly string[] AlibabaCloudBalanceCashKeys =
    {
        "AvailableCashAmount", "availableCashAmount", "available_cash_amount",
    };

    private static readonly string[] AlibabaCloudBalanceCurrencyKeys =
    {
        "Currency", "currency",
    };

    private static readonly string[] AlibabaCodingPlanPlanKeys =
    {
        "planName", "plan_name", "instanceName", "instance_name", "packageName", "package_name",
        "commodityName", "commodity_name", "displayName", "display_name", "name", "title",
    };

    private static readonly string[] AlibabaCodingPlanFiveHourUsedKeys =
    {
        "per5HourUsedQuota", "perFiveHourUsedQuota", "per_5_hour_used_quota", "fiveHourUsedQuota",
    };

    private static readonly string[] AlibabaCodingPlanFiveHourTotalKeys =
    {
        "per5HourTotalQuota", "perFiveHourTotalQuota", "per_5_hour_total_quota", "fiveHourTotalQuota",
    };

    private static readonly string[] AlibabaCodingPlanFiveHourResetKeys =
    {
        "per5HourQuotaNextRefreshTime", "perFiveHourQuotaNextRefreshTime", "per_5_hour_quota_next_refresh_time",
        "fiveHourNextRefreshTime", "fiveHourResetTime",
    };

    private static readonly string[] AlibabaCodingPlanWeeklyUsedKeys =
    {
        "perWeekUsedQuota", "per_week_used_quota", "weeklyUsedQuota",
    };

    private static readonly string[] AlibabaCodingPlanWeeklyTotalKeys =
    {
        "perWeekTotalQuota", "per_week_total_quota", "weeklyTotalQuota",
    };

    private static readonly string[] AlibabaCodingPlanWeeklyResetKeys =
    {
        "perWeekQuotaNextRefreshTime", "per_week_quota_next_refresh_time", "weeklyNextRefreshTime",
        "weeklyResetTime",
    };

    private static readonly string[] AlibabaCodingPlanMonthlyUsedKeys =
    {
        "perBillMonthUsedQuota", "perMonthUsedQuota", "per_bill_month_used_quota", "monthlyUsedQuota",
    };

    private static readonly string[] AlibabaCodingPlanMonthlyTotalKeys =
    {
        "perBillMonthTotalQuota", "perMonthTotalQuota", "per_bill_month_total_quota", "monthlyTotalQuota",
    };

    private static readonly string[] AlibabaCodingPlanMonthlyResetKeys =
    {
        "perBillMonthQuotaNextRefreshTime", "perMonthQuotaNextRefreshTime", "per_bill_month_quota_next_refresh_time",
        "monthlyNextRefreshTime", "monthlyResetTime",
    };

    private static RateWindow? AlibabaCodingPlanWindow(
        string label,
        JsonElement quota,
        string[] usedKeys,
        string[] totalKeys,
        string[] resetKeys,
        long windowMinutes,
        bool normalizeFiveHourReset,
        DateTimeOffset updatedAt)
    {
        var used = FirstDouble(quota, usedKeys);
        var total = FirstDouble(quota, totalKeys);
        if (used is null || total is not > 0)
            return null;

        var normalizedUsed = Math.Clamp(used.Value, 0, total.Value);
        var resetsAt = FirstIso(quota, resetKeys);
        if (normalizeFiveHourReset)
            resetsAt = NormalizeAlibabaFiveHourReset(resetsAt, updatedAt);

        return new RateWindow
        {
            Label = label,
            UsedPercent = Quota.ClampPercent(normalizedUsed / total.Value * 100),
            ResetsAt = resetsAt,
            ResetDescription = $"{Fmt0(normalizedUsed)} / {Fmt0(total.Value)} used",
            WindowMinutes = windowMinutes,
        };
    }

    private static string? NormalizeAlibabaFiveHourReset(string? rawIso, DateTimeOffset updatedAt)
    {
        if (rawIso is null)
            return null;
        if (!DateTimeOffset.TryParse(rawIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var raw))
            return rawIso;
        if ((raw - updatedAt).TotalSeconds >= 60)
            return raw.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        var shifted = raw.AddHours(5);
        return ((shifted - updatedAt).TotalSeconds >= 60 ? shifted : updatedAt.AddHours(5))
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? AlibabaCodingPlanName(JsonElement root) =>
        FirstDeepString(root, AlibabaCodingPlanPlanKeys);

    private static BalanceInfo? AlibabaCloudBalance(JsonElement root)
    {
        var available = FirstDeepDouble(root, AlibabaCloudBalanceAvailableKeys);
        var cash = FirstDeepDouble(root, AlibabaCloudBalanceCashKeys);
        if (available is null && cash is null)
            return null;

        var total = Math.Max(0, available ?? cash ?? 0);
        var paid = Math.Max(0, cash ?? total);
        return new BalanceInfo
        {
            Currency = FirstDeepString(root, AlibabaCloudBalanceCurrencyKeys) ?? "CNY",
            Total = total,
            Paid = paid,
            Granted = Math.Max(0, total - paid),
        };
    }

    private static BalanceInfo? AlibabaTokenPlanBalance(double? used, double? total, double? remaining)
    {
        if (total is not > 0 && remaining is null)
            return null;

        var granted = Math.Max(0, total ?? remaining ?? 0);
        var balance = Math.Max(0, remaining ?? (total is not null && used is not null ? total.Value - used.Value : granted));
        var paid = Math.Max(0, used ?? (total is not null ? total.Value - balance : 0));
        return new BalanceInfo
        {
            Currency = "credits",
            Total = balance,
            Paid = paid,
            Granted = granted,
        };
    }

    private static JsonElement? AlibabaCodingPlanQuotaSource(JsonElement root) =>
        FirstObjectDeep(root, "codingPlanQuotaInfo", "coding_plan_quota_info")
        ?? FirstObjectMatchingAnyKey(root, AlibabaCodingPlanFiveHourUsedKeys
            .Concat(AlibabaCodingPlanFiveHourTotalKeys)
            .Concat(AlibabaCodingPlanWeeklyUsedKeys)
            .Concat(AlibabaCodingPlanWeeklyTotalKeys)
            .Concat(AlibabaCodingPlanMonthlyUsedKeys)
            .Concat(AlibabaCodingPlanMonthlyTotalKeys)
            .ToArray());

    private static JsonElement? AlibabaCodingPlanActiveInstance(JsonElement root, DateTimeOffset now)
    {
        var instances = FirstArrayDeep(root, "codingPlanInstanceInfos", "coding_plan_instance_infos");
        if (instances is not { ValueKind: JsonValueKind.Array } array)
            return null;

        JsonElement? best = null;
        var bestScore = int.MinValue;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var score = AlibabaCodingPlanActiveScore(item, now);
            if (score > bestScore)
            {
                best = item;
                bestScore = score;
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static bool AlibabaCodingPlanAllInstancesExpired(JsonElement root, DateTimeOffset now)
    {
        var instances = FirstArrayDeep(root, "codingPlanInstanceInfos", "coding_plan_instance_infos");
        if (instances is not { ValueKind: JsonValueKind.Array } array)
            return false;

        var scores = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => AlibabaCodingPlanActiveScore(item, now))
            .ToList();
        return scores.Count > 0 && scores.All(score => score < 0);
    }

    private static bool AlibabaCodingPlanHasActiveSignal(JsonElement source, JsonElement payload, DateTimeOffset now)
    {
        var containsInstances = FirstArrayDeep(payload, "codingPlanInstanceInfos", "coding_plan_instance_infos") is { ValueKind: JsonValueKind.Array };
        if (containsInstances)
            return AlibabaCodingPlanActiveScore(source, now) > 0;
        return AlibabaCodingPlanActiveScore(source, now) > 0 || AlibabaCodingPlanActiveScore(payload, now) > 0;
    }

    private static int AlibabaCodingPlanActiveScore(JsonElement source, DateTimeOffset now)
    {
        var status = FirstString(source, "status", "instanceStatus");
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToUpperInvariant();
            if (normalized is "VALID" or "ACTIVE")
                return 3;
            if (normalized is "EXPIRED" or "INVALID" or "INACTIVE" or "DISABLED" or "TERMINATED" or "STOPPED")
                return -1;
        }

        if (FirstBool(source, "isActive", "active") is { } active)
            return active ? 3 : -1;

        var expiry = FirstIso(source, "endTime", "periodEndTime", "expireTime", "expirationTime");
        if (expiry is not null
            && DateTimeOffset.TryParse(expiry, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
            && when > now)
        {
            return 1;
        }

        return 0;
    }

    private static UsageWindowSet ParseUsageWindows(
        JsonElement root,
        bool includeMonthly,
        bool preserveMissingNamedWindows = false)
    {
        var candidates = new List<WindowCandidate>();
        CollectUsageWindowCandidates(root, "", candidates);

        static bool IsRolling(WindowCandidate candidate) =>
            candidate.PathLower.Contains("rolling", StringComparison.Ordinal)
            || candidate.PathLower.Contains("hour", StringComparison.Ordinal)
            || candidate.PathLower.Contains("5h", StringComparison.Ordinal)
            || candidate.PathLower.Contains("5-hour", StringComparison.Ordinal);
        static bool IsWeekly(WindowCandidate candidate) =>
            candidate.PathLower.Contains("weekly", StringComparison.Ordinal)
            || candidate.PathLower.Contains("week", StringComparison.Ordinal);
        static bool IsMonthly(WindowCandidate candidate) =>
            candidate.PathLower.Contains("monthly", StringComparison.Ordinal)
            || candidate.PathLower.Contains("month", StringComparison.Ordinal);

        UsageWindow? Pick(
            Func<WindowCandidate, bool> preferred,
            bool shorterReset,
            UsageWindow? exclude = null,
            Func<WindowCandidate, bool>? fallback = null)
        {
            var scoped = candidates
                .Where(candidate => (exclude is null || !ReferenceEquals(candidate.Window, exclude)) && preferred(candidate))
                .ToList();
            if (scoped.Count == 0)
            {
                scoped = candidates
                    .Where(candidate => (exclude is null || !ReferenceEquals(candidate.Window, exclude))
                        && (fallback?.Invoke(candidate) ?? !preserveMissingNamedWindows))
                    .ToList();
            }
            if (scoped.Count == 0)
                return null;

            return scoped
                .OrderBy(candidate => ResetSortSeconds(candidate.Window, shorterReset))
                .ThenByDescending(candidate => candidate.Window.UsedPercent)
                .First()
                .Window;
        }

        var rolling = Pick(IsRolling, shorterReset: true, fallback: candidate => !IsWeekly(candidate) && !IsMonthly(candidate));
        var weekly = Pick(
            IsWeekly,
            shorterReset: false,
            exclude: rolling);
        var monthly = includeMonthly
            ? Pick(
                IsMonthly,
                shorterReset: false,
                exclude: weekly ?? rolling)
            : null;

        var renewsAt = FirstDeepIso(root, "renewAt", "renew_at", "renewsAt", "renews_at");
        return new UsageWindowSet(rolling, weekly, monthly, renewsAt);
    }

    private static RateWindow UsageWindowRate(string label, UsageWindow window, long windowMinutes) => new()
    {
        Label = label,
        UsedPercent = window.UsedPercent,
        ResetsAt = window.ResetsAt,
        ResetDescription = window.ResetDescription,
        WindowMinutes = windowMinutes,
    };

    private static RateWindow RenewalWindow(string? renewsAt) => new()
    {
        Label = "Renews",
        UsedPercent = 0,
        ResetsAt = renewsAt,
        ResetDescription = "Subscription renewal",
    };

    private static double ResetSortSeconds(UsageWindow window, bool shorterReset)
    {
        if (window.ResetsAt is not null
            && DateTimeOffset.TryParse(window.ResetsAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var when))
        {
            var seconds = Math.Max(0, (when - DateTimeOffset.UtcNow).TotalSeconds);
            return shorterReset ? seconds : -seconds;
        }

        return shorterReset ? double.MaxValue : double.MinValue;
    }

    private static void CollectUsageWindowCandidates(JsonElement element, string path, List<WindowCandidate> candidates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (ParseUsageWindow(element) is { } window)
                candidates.Add(new WindowCandidate(window, path.ToLowerInvariant()));

            foreach (var property in element.EnumerateObject())
                CollectUsageWindowCandidates(property.Value, string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}", candidates);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                CollectUsageWindowCandidates(item, $"{path}[{index++}]", candidates);
        }
    }

    private static UsageWindow? ParseUsageWindow(JsonElement obj)
    {
        var percent = FirstDouble(obj,
            "usagePercent", "usedPercent", "percentUsed", "percent",
            "usage_percent", "used_percent", "utilization", "utilizationPercent", "utilization_percent");
        if (percent is null)
        {
            var used = FirstDouble(obj, "used", "usage", "consumed", "count", "usedTokens");
            var limit = FirstDouble(obj, "limit", "total", "quota", "max", "cap", "tokenLimit");
            if (used is not null && limit is > 0)
                percent = used.Value / limit.Value * 100;
        }
        if (percent is null)
            return null;

        var reset = ResetFromWindow(obj);
        var description = WindowDescription(obj);
        return new UsageWindow(Quota.ClampPercent(percent.Value <= 1 ? percent.Value * 100 : percent.Value), reset, description);
    }

    private static string? ResetFromWindow(JsonElement obj)
    {
        var seconds = FirstDouble(obj,
            "resetInSec", "resetInSeconds", "resetSeconds", "reset_sec", "reset_in_sec",
            "resetsInSec", "resetsInSeconds", "resetIn", "resetSec");
        if (seconds is not null)
            return DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds.Value)).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        return FirstIso(obj, "resetAt", "resetsAt", "reset_at", "resets_at", "nextReset", "next_reset", "renewAt", "renew_at");
    }

    private static string? WindowDescription(JsonElement obj)
    {
        var used = FirstDouble(obj, "used", "usage", "consumed", "count", "usedTokens");
        var limit = FirstDouble(obj, "limit", "total", "quota", "max", "cap", "tokenLimit");
        if (used is not null && limit is > 0)
            return $"{Fmt0(used.Value)}/{Fmt0(limit.Value)}";
        return null;
    }

    private static Dictionary<string, double> MistralPrices(JsonElement root)
    {
        var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("prices", out var array) || array.ValueKind != JsonValueKind.Array)
            return prices;

        foreach (var price in array.EnumerateArray())
        {
            var metric = JString(price, "billing_metric");
            var group = JString(price, "billing_group");
            var value = JDouble(price, "price");
            if (!string.IsNullOrWhiteSpace(metric) && !string.IsNullOrWhiteSpace(group) && value is not null)
                prices[$"{metric}::{group}"] = value.Value;
        }
        return prices;
    }

    private static void MistralAggregateCategory(
        JsonElement root,
        IReadOnlyDictionary<string, double> prices,
        string category,
        bool countsTokens,
        bool countCompletionModels,
        MistralTotals totals)
    {
        if (FirstObject(root, category) is not { } obj)
            return;
        MistralAggregateModelMap(FirstObject(obj, "models"), prices, countsTokens, countCompletionModels, totals);
    }

    private static void MistralAggregateNestedModels(
        JsonElement parent,
        IReadOnlyDictionary<string, double> prices,
        string key,
        bool countsTokens,
        bool countCompletionModels,
        MistralTotals totals)
    {
        if (FirstObject(parent, key) is not { } obj)
            return;
        MistralAggregateModelMap(FirstObject(obj, "models"), prices, countsTokens, countCompletionModels, totals);
    }

    private static void MistralAggregateModelMap(
        JsonElement? models,
        IReadOnlyDictionary<string, double> prices,
        bool countsTokens,
        bool countCompletionModels,
        MistralTotals totals)
    {
        if (models is not { ValueKind: JsonValueKind.Object } modelObj)
            return;

        foreach (var model in modelObj.EnumerateObject())
        {
            var beforeTokens = totals.InputTokens + totals.CachedTokens + totals.OutputTokens;
            MistralAggregateEntries(model.Value, prices, "input", countsTokens, totals);
            MistralAggregateEntries(model.Value, prices, "cached", countsTokens, totals);
            MistralAggregateEntries(model.Value, prices, "output", countsTokens, totals);
            if (countCompletionModels && totals.InputTokens + totals.CachedTokens + totals.OutputTokens > beforeTokens)
                totals.ModelCount++;
        }
    }

    private static void MistralAggregateEntries(
        JsonElement modelData,
        IReadOnlyDictionary<string, double> prices,
        string key,
        bool countsTokens,
        MistralTotals totals)
    {
        if (!modelData.TryGetProperty(key, out var entries) || entries.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in entries.EnumerateArray())
        {
            var units = JDouble(entry, "value_paid") ?? JDouble(entry, "value") ?? 0;
            var metric = JString(entry, "billing_metric");
            var group = JString(entry, "billing_group");
            if (!string.IsNullOrWhiteSpace(metric) && !string.IsNullOrWhiteSpace(group)
                && prices.TryGetValue($"{metric}::{group}", out var price))
            {
                totals.Cost += units * price;
            }

            if (!countsTokens)
                continue;
            switch (key)
            {
                case "input": totals.InputTokens += (long)Math.Round(units); break;
                case "cached": totals.CachedTokens += (long)Math.Round(units); break;
                case "output": totals.OutputTokens += (long)Math.Round(units); break;
            }
        }
    }

    private static double? FirstDeepDouble(JsonElement root, params string[] keys)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (FirstDouble(root, keys) is { } value)
                return value;
            foreach (var property in root.EnumerateObject())
            {
                if (FirstDeepDouble(property.Value, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (FirstDeepDouble(item, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.String)
        {
            var text = root.GetString();
            if (!string.IsNullOrWhiteSpace(text) && LooksLikeJson(text))
            {
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    return FirstDeepDouble(nested.RootElement, keys);
                }
                catch
                {
                    return null;
                }
            }
        }
        return null;
    }

    private static string? FirstDeepString(JsonElement root, params string[] keys)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in keys)
            {
                if (JString(root, key) is { } value)
                    return value;
            }
            foreach (var property in root.EnumerateObject())
            {
                if (FirstDeepString(property.Value, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (FirstDeepString(item, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.String)
        {
            var text = root.GetString();
            if (!string.IsNullOrWhiteSpace(text) && LooksLikeJson(text))
            {
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    return FirstDeepString(nested.RootElement, keys);
                }
                catch
                {
                    return null;
                }
            }
        }
        return null;
    }

    private static string? FirstDeepIso(JsonElement root, params string[] keys)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (FirstIso(root, keys) is { } value)
                return value;
            foreach (var property in root.EnumerateObject())
            {
                if (FirstDeepIso(property.Value, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (FirstDeepIso(item, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.String)
        {
            var text = root.GetString();
            if (!string.IsNullOrWhiteSpace(text) && LooksLikeJson(text))
            {
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    return FirstDeepIso(nested.RootElement, keys);
                }
                catch
                {
                    return null;
                }
            }
        }
        return null;
    }

    private static double? FirstDouble(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (JDouble(obj, key) is { } value)
                return value;
        }
        return null;
    }

    private static string? FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (JString(obj, key) is { } value)
                return value;
        }
        return null;
    }

    private static string? FirstIso(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (JDateIso(obj, key) is { } value)
                return value;
        }
        return null;
    }

    // Rust {:.0}/{:.1}/{:.2} → invariant-culture fixed formats (matches other providers).
    private static string Fmt0(double v) => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
    private static string Fmt1(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    private static string Fmt2(double v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (Exception e)
        {
            throw new ProviderException($"Parse error: Invalid JSON: {e.Message}", e);
        }
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    private static void ThrowIfAlibabaTokenPlanError(JsonElement root)
    {
        if (FirstDeepString(root, "Success", "success") is { } successText
            && bool.TryParse(successText, out var success)
            && !success)
        {
            var message = FirstDeepString(root, "Message", "message", "msg", "Code", "code")
                ?? "request was not successful";
            ThrowAlibabaTokenPlanMessage(message);
        }

        if (FirstDeepString(root, "statusCode", "status_code", "code", "Code", "status") is { } codeText)
        {
            var code = codeText.Trim();
            var lowered = code.ToLowerInvariant();
            if (lowered.Contains("needlogin", StringComparison.Ordinal)
                || lowered.Contains("login", StringComparison.Ordinal))
            {
                throw new ProviderException("Login required: Alibaba Token Plan session is not available.");
            }

            if (int.TryParse(code, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var status)
                && status != 0
                && status != 200)
            {
                var message = FirstDeepString(root, "statusMessage", "status_msg", "message", "Message", "msg")
                    ?? $"status code {status}";
                ThrowAlibabaTokenPlanMessage(message);
            }
        }

        if (FirstDeepString(root, "message", "Message", "msg", "statusMessage") is { } text)
            ThrowAlibabaTokenPlanMessage(text, onlyLoginMessages: true);
    }

    private static void ThrowIfAlibabaCodingPlanError(JsonElement root)
    {
        if (FirstDeepString(root, "Success", "success") is { } successText
            && bool.TryParse(successText, out var success)
            && !success)
        {
            var message = FirstDeepString(root, "Message", "message", "msg", "Code", "code")
                ?? "request was not successful";
            ThrowAlibabaCodingPlanMessage(message);
        }

        if (FirstDeepString(root, "statusCode", "status_code", "code", "Code", "status") is { } codeText)
        {
            var code = codeText.Trim();
            var lowered = code.ToLowerInvariant();
            if (lowered.Contains("needlogin", StringComparison.Ordinal)
                || lowered.Contains("login", StringComparison.Ordinal))
            {
                throw new ProviderException("Login required: Alibaba Coding Plan session is not available.");
            }

            if (int.TryParse(code, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var status)
                && status != 0
                && status != 200)
            {
                var message = FirstDeepString(root, "statusMessage", "status_msg", "message", "Message", "msg")
                    ?? $"status code {status}";
                ThrowAlibabaCodingPlanMessage(message);
            }
        }

        if (FirstDeepString(root, "message", "Message", "msg", "statusMessage") is { } text)
            ThrowAlibabaCodingPlanMessage(text, onlyLoginMessages: true);
    }

    private static void ThrowAlibabaCodingPlanMessage(string message, bool onlyLoginMessages = false)
    {
        var lowered = message.ToLowerInvariant();
        if (lowered.Contains("needlogin", StringComparison.Ordinal)
            || lowered.Contains("log in", StringComparison.Ordinal)
            || lowered.Contains("login", StringComparison.Ordinal))
        {
            throw new ProviderException("Login required: Alibaba Coding Plan session is not available.");
        }

        if (!onlyLoginMessages)
            throw new ProviderException($"Not available: Alibaba Coding Plan API error: {message}");
    }

    private static void ThrowAlibabaTokenPlanMessage(string message, bool onlyLoginMessages = false)
    {
        var lowered = message.ToLowerInvariant();
        if (lowered.Contains("needlogin", StringComparison.Ordinal)
            || lowered.Contains("log in", StringComparison.Ordinal)
            || lowered.Contains("login", StringComparison.Ordinal))
        {
            throw new ProviderException("Login required: Alibaba Token Plan session is not available.");
        }

        if (!onlyLoginMessages)
            throw new ProviderException($"Not available: Alibaba Token Plan API error: {message}");
    }

    private static string? AlibabaTokenPlanQuotaDetail(double? used, double? total, double? remaining)
    {
        if (used is not null && total is > 0)
            return $"{FmtCredits(used.Value)} / {FmtCredits(total.Value)} credits used";
        if (remaining is not null && total is > 0)
            return $"{FmtCredits(remaining.Value)} / {FmtCredits(total.Value)} credits left";
        if (remaining is not null)
            return $"{FmtCredits(remaining.Value)} credits left";
        return null;
    }

    private static string FmtCredits(double value) =>
        value == Math.Truncate(value)
            ? value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonElement? FirstObject(JsonElement? root, params string[] keys)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        foreach (var key in keys)
        {
            if (TryGetProperty(obj, key, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        }

        return null;
    }

    private static JsonElement? FirstObjectDeep(JsonElement root, params string[] keys)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (FirstObject(root, keys) is { } direct)
                return direct;
            foreach (var property in root.EnumerateObject())
            {
                if (FirstObjectDeep(property.Value, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (FirstObjectDeep(item, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.String)
        {
            var text = root.GetString();
            if (!string.IsNullOrWhiteSpace(text) && LooksLikeJson(text))
            {
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    return FirstObjectDeep(nested.RootElement, keys);
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static JsonElement? FirstObjectMatchingAnyKey(JsonElement root, params string[] keys)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (keys.Any(key => TryGetProperty(root, key, out _)))
                return root;
            foreach (var property in root.EnumerateObject())
            {
                if (FirstObjectMatchingAnyKey(property.Value, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (FirstObjectMatchingAnyKey(item, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.String)
        {
            var text = root.GetString();
            if (!string.IsNullOrWhiteSpace(text) && LooksLikeJson(text))
            {
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    return FirstObjectMatchingAnyKey(nested.RootElement, keys);
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static JsonElement? FirstArrayDeep(JsonElement root, params string[] keys)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in keys)
            {
                if (TryGetProperty(root, key, out var value) && value.ValueKind == JsonValueKind.Array)
                    return value;
            }
            foreach (var property in root.EnumerateObject())
            {
                if (FirstArrayDeep(property.Value, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (FirstArrayDeep(item, keys) is { } child)
                    return child;
            }
        }
        else if (root.ValueKind == JsonValueKind.String)
        {
            var text = root.GetString();
            if (!string.IsNullOrWhiteSpace(text) && LooksLikeJson(text))
            {
                try
                {
                    using var nested = JsonDocument.Parse(text);
                    return FirstArrayDeep(nested.RootElement, keys);
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static double? JDouble(JsonElement? root, string key)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj || !TryGetProperty(obj, key, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    private static string? JString(JsonElement? root, string key)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj || !TryGetProperty(obj, key, out var value))
            return null;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool? FirstBool(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (JBool(obj, key) is { } value)
                return value;
        }
        return null;
    }

    private static bool? JBool(JsonElement? root, string key)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj || !TryGetProperty(obj, key, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetDouble(out var number) => Math.Abs(number) > 0.001,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) => Math.Abs(number) > 0.001,
            JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "active" or "valid" or "yes" => true,
                "inactive" or "invalid" or "expired" or "no" => false,
                _ => null,
            },
            _ => null,
        };
    }

    private static string? JDateIso(JsonElement? root, string key)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj || !TryGetProperty(obj, key, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric))
            return NumericEpochToIso(numeric);
        if (value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return NumericEpochToIso(number);
        return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static bool TryGetProperty(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.TryGetProperty(key, out value))
            return true;

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? NumericEpochToIso(double value)
    {
        if (value <= 0 || !double.IsFinite(value))
            return null;
        var seconds = Math.Abs(value) > 10_000_000_000 ? value / 1000.0 : value;
        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(seconds)).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? EpochSecondsToIso(double? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(seconds.Value)).ToString("O", System.Globalization.CultureInfo.InvariantCulture) : null;

    private static string? EpochMillisecondsToIso(double? milliseconds)
    {
        if (milliseconds is not > 0)
            return null;
        var seconds = milliseconds.Value > 10_000_000_000 ? milliseconds.Value / 1000.0 : milliseconds.Value;
        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(seconds)).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? DisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var spaced = value.Trim().Replace("_", " ", StringComparison.Ordinal).Replace("-", " ", StringComparison.Ordinal);
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    private static JsonElement? FindT3CustomerData(string text)
    {
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (FindT3CustomerData(doc.RootElement) is { } found)
                    return found.Clone();
            }
            catch
            {
                // Skip non-JSON lines.
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return FindT3CustomerData(doc.RootElement)?.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? FindT3CustomerData(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("usageFourHourPercentage", out _)
                || element.TryGetProperty("usageMonthPercentage", out _)
                || (element.TryGetProperty("subscription", out _) && element.TryGetProperty("usageBand", out _)))
            {
                return element;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (FindT3CustomerData(property.Value) is { } found)
                    return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindT3CustomerData(item) is { } found)
                    return found;
            }
        }

        return null;
    }

    private static string T3Description(string label, string? usageBand) =>
        string.IsNullOrWhiteSpace(usageBand) ? label : $"{label} - {usageBand.Trim()}";

    private static (string Name, double MonthlyCredits)? CommandCodePlan(string? planId) =>
        planId?.Trim().ToLowerInvariant() switch
        {
            "individual-go" => ("Go", 10),
            "individual-pro" => ("Pro", 30),
            "individual-max" => ("Max", 150),
            "individual-ultra" => ("Ultra", 300),
            _ => null,
        };

    private static string CommandCodeUsd(double value) =>
        value < 100
            ? $"${value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}"
            : $"${value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}";
    // ---- hardcoded URLs (provider config fields are UI-facing login hints) ----

    // ---- injected scripts (verbatim from src-tauri/src/main.rs) ------------

    // BayesDL initialization_script (open_bayesdl_login).
    private const string WebMessageBridgeScript = """
(function() {
  if (window.__ql_web_message_bridge_installed) return;
  window.__ql_web_message_bridge_installed = true;
  function postToNative(message) {
    try {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage(message);
        return true;
      }
    } catch (_) {}
    return false;
  }
  function relayToTop(message) {
    try {
      if (window.top && window.top !== window && window.top.postMessage) {
        window.top.postMessage(message, '*');
        return true;
      }
    } catch (_) {}
    return false;
  }
  function captureMessage(json) {
    return { type: 'quotalens-capture-json', json: json, href: location.href };
  }
  function hashMessage() {
    return { type: 'quotalens-hash', href: location.href };
  }
  window.__qlCaptureJson = function(value) {
    try {
      var json = (typeof value === 'string') ? value : JSON.stringify(value);
      var message = captureMessage(json);
      postToNative(message);
      relayToTop(message);
      return json;
    } catch (_) {
      return '';
    }
  };
  window.addEventListener('message', function(event) {
    try {
      var data = event && event.data;
      if (!data || (data.type !== 'quotalens-capture-json' && data.type !== 'quotalens-hash')) return;
      postToNative(data);
    } catch (_) {}
  }, true);
  function send() {
    try {
      if (String(location.href).indexOf('#__ql__') < 0) return;
      var message = hashMessage();
      postToNative(message);
      relayToTop(message);
    } catch (_) {}
  }
  window.addEventListener('hashchange', send, true);
  setTimeout(send, 0);
})();
""";

    private static string WebLoginModeScript(bool hidden)
    {
        var hiddenLiteral = hidden ? "true" : "false";
        var visibleLiteral = hidden ? "false" : "true";
        return $$"""
(function() {
  window.__qlWebLoginMode = { hidden: {{hiddenLiteral}}, visible: {{visibleLiteral}} };
  window.__qlHiddenCapture = {{hiddenLiteral}};
  window.__qlVisibleLogin = {{visibleLiteral}};
  window.__qlSuppressBanner = {{visibleLiteral}};
  function suppressVisibleOrNestedBanners() {
    try {
      if (window.__qlSuppressBanner !== true && window.top === window) return;
      var css = '#__ql_banner,[id^="__ql_"][id$="_banner"]{display:none!important;visibility:hidden!important;pointer-events:none!important;}';
      var style = document.getElementById('__ql_banner_suppression');
      if (style) return;
      style = document.createElement('style');
      style.id = '__ql_banner_suppression';
      style.textContent = css;
      (document.head || document.documentElement || document.body).appendChild(style);
    } catch (_) {
      setTimeout(suppressVisibleOrNestedBanners, 50);
    }
  }
  suppressVisibleOrNestedBanners();
})();
""";
    }

    private const string BayesdlInitScript = @"
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }

    var banner = document.createElement('div');
    banner.id = '__ql_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#cc0000;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: initializing...';
    document.body.appendChild(banner);

    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }

    function fetchQuota() {
      Promise.all([
        fetch('/api/maas/v1/server/combo/home/queryComboPage', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ pageNum: 1, pageSize: 10 })
        }).then(function(r) { return r.json(); }),
        fetch('/api/user/pandect/queryCost', { method: 'GET' }).then(function(r) { return r.json(); })
      ]).then(function(results) {
        var combo = results[0];
        var cost = results[1];
        var costData = (cost && cost.code == 0 && cost.data) ? cost.data : null;
        if (combo.code == 0 && combo.data && Array.isArray(combo.data.rows) && combo.data.rows.length) {
          var rows = combo.data.rows.map(function(row) {
            return { tokensTotal: row.tokensTotal, tokensUse: row.tokensUse, comboName: row.comboName, comboStartTime: row.comboStartTime, comboEndTime: row.comboEndTime, statusDict: row.statusDict ? { name: row.statusDict.name } : null, comboAttributeDict: row.comboAttributeDict ? { name: row.comboAttributeDict.name } : null, isCodingPlan: row.isCodingPlan };
          });
          update('QuotaLens: captured ' + (rows[0].comboName||'?') + ' (bal:' + (costData ? costData.balance : '?') + ')', '#00aa00');
          var slim = { code: '0', data: { rows: rows, cost: costData } };
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(slim));
        } else if (costData) {
          var slim2 = { code: '0', data: { rows: [], cost: costData } };
          update('QuotaLens: cost captured (bal:' + costData.balance + ')', '#00aa00');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(slim2));
        } else {
          update('QuotaLens: not logged in. Retrying...', '#cc6600');
          setTimeout(fetchQuota, 5000);
        }
      }).catch(function(e) {
        update('QuotaLens: error - ' + (e.message || String(e)) + '. Retrying...', '#cc0000');
        setTimeout(fetchQuota, 5000);
      });
    }

    setTimeout(fetchQuota, 1000);
  }
  tryInit();
})();
";

    // BayesDL backup eval-fetch script (eval'd every 3s).
    private const string BayesdlFetchScript = @"
function qlEncode(s) {
  return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
}
Promise.all([
  fetch('/api/maas/v1/server/combo/home/queryComboPage', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pageNum: 1, pageSize: 10 })
  }).then(function(r) {
    if (!r.ok) return null;
    return r.json();
  }),
  fetch('/api/user/pandect/queryCost', { method: 'GET' }).then(function(r) {
    if (!r.ok) return null;
    return r.json();
  })
]).then(function(results) {
  var combo = results[0];
  var cost = results[1];
  var costData = (cost && cost.code == 0 && cost.data) ? cost.data : null;
  if (combo && combo.code == 0 && combo.data && Array.isArray(combo.data.rows) && combo.data.rows.length) {
    var rows = combo.data.rows.map(function(row) {
      return { tokensTotal: row.tokensTotal, tokensUse: row.tokensUse, comboName: row.comboName, comboStartTime: row.comboStartTime, comboEndTime: row.comboEndTime, statusDict: row.statusDict ? { name: row.statusDict.name } : null, comboAttributeDict: row.comboAttributeDict ? { name: row.comboAttributeDict.name } : null, isCodingPlan: row.isCodingPlan };
    });
    var slim = { code: '0', data: { rows: rows, cost: costData } };
    window.location.hash = '#__ql__' + qlEncode(JSON.stringify(slim));
  } else if (costData) {
    var slim2 = { code: '0', data: { rows: [], cost: costData } };
    window.location.hash = '#__ql__' + qlEncode(JSON.stringify(slim2));
  } else {
    var s = JSON.stringify({combo:combo,cost:cost});
    window.location.hash = '#__ql__NODATA_' + qlEncode(s.length > 600 ? s.substring(0,600) : s);
  }
}).catch(function(e) {
  window.location.hash = '#__ql__ERR_' + qlEncode((e.message || String(e)).substring(0,300));
});
";

    // MiMo initialization_script (open_mimo_login).
    private const string MimoInitScript = @"
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }

    var banner = document.createElement('div');
    banner.id = '__ql_mimo_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#ff6600;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking MiMo...';
    document.body.appendChild(banner);

    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }

    function fetchJson(path, signal) {
      return fetch(path, {
        method: 'GET',
        headers: { 'Accept': 'application/json, text/plain, */*' },
        signal: signal
      }).then(function(r) {
        if (!r.ok) throw new Error(path + ' HTTP ' + r.status);
        return r.json();
      });
    }
    function optionalJson(path) {
      var controller = new AbortController();
      var timer = setTimeout(function() { controller.abort(); }, 4000);
      return fetchJson(path, controller.signal)
        .catch(function() { return null; })
        .finally(function() { clearTimeout(timer); });
    }
    function successfulPayload(response) {
      return !!response && Number(response.code) === 0 && !!response.data;
    }

    function fetchUsage() {
      Promise.all([
        optionalJson('/api/v1/tokenPlan/usage'),
        optionalJson('/api/v1/tokenPlan/detail'),
        optionalJson('/api/v1/balance')
      ]).then(function(results) {
        var usage = results[0];
        var detail = results[1];
        var balance = results[2];
        var hasUsage = successfulPayload(usage);
        var hasBalance = successfulPayload(balance);
        if (hasUsage || hasBalance) {
          update('QuotaLens: MiMo data captured', '#00aa00');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify({
            usage: hasUsage ? usage : null,
            detail: successfulPayload(detail) ? detail : null,
            balance: hasBalance ? balance : null
          }));
        } else {
          update('QuotaLens: not logged in or no data. Retrying...', '#cc6600');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function(e) {
        update('QuotaLens: error - ' + (e.message || String(e)) + '. Retrying...', '#cc0000');
        setTimeout(fetchUsage, 5000);
      });
    }

    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
";

    // Kimi WebView capture: CodexBar reads the kimi-auth cookie and posts to the
    // billing gateway. In QuotaLens the WebView is already on kimi.com, so this
    // runs in the user's logged-in page and reuses the WebView2 session directly.
    //
    // LIMITATION: kimi-auth is HttpOnly, so document.cookie cannot read it and the
    // gateway rejects header-less calls with 401. This script therefore rarely
    // captures on its own; the working paths are the native cookie capture
    // (CookieManager sees HttpOnly cookies) and GetUsages response sniffing —
    // see NativeCookieCaptures / IsNativeCapturedResponse. Kept as a backup in
    // case Kimi ever drops the HttpOnly flag.
    private const string KimiInitScript = @"
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function cookie(name) {
    var escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    var match = document.cookie.match(new RegExp('(?:^|; )' + escaped + '=([^;]*)'));
    return match ? decodeURIComponent(match[1]) : '';
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }

    var banner = document.createElement('div');
    banner.id = '__ql_kimi_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#111827;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Kimi...';
    document.body.appendChild(banner);

    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }

    function fetchUsage() {
      var token = cookie('kimi-auth');
      var headers = {
        'Content-Type': 'application/json',
        'Accept': '*/*',
        'connect-protocol-version': '1',
        'x-language': 'en-US',
        'x-msh-platform': 'web'
      };
      if (token) {
        headers.Authorization = 'Bearer ' + token;
      }

      fetch('/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages', {
        method: 'POST',
        credentials: 'include',
        headers: headers,
        body: JSON.stringify({ scope: ['FEATURE_CODING'] })
      }).then(function(r) {
        if (!r.ok) {
          window.location.hash = '#__ql__HTTP_' + r.status;
          throw new Error('HTTP ' + r.status);
        }
        return r.json();
      }).then(function(data) {
        if (data && data.usages && data.usages.length) {
          update('QuotaLens: Kimi data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(data));
        } else {
          update('QuotaLens: no Kimi quota data yet. Retrying...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function(e) {
        update('QuotaLens: login required or quota fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }

    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
";

    private const string AlibabaCodingPlanInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function cookie(name) {
    var escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    var match = document.cookie.match(new RegExp('(?:^|; )' + escaped + '=([^;]*)'));
    return match ? decodeURIComponent(match[1]) : '';
  }
  function secToken() {
    var fromCookie = cookie('sec_token');
    if (fromCookie) return fromCookie;
    var html = document.documentElement ? document.documentElement.innerHTML : '';
    var patterns = [
      /"SEC_TOKEN"\s*:\s*"([^"]+)"/,
      /"secToken"\s*:\s*"([^"]+)"/,
      /"sec_token"\s*:\s*"([^"]+)"/,
      /SEC_TOKEN\s*:\s*['"]([^'"]+)['"]/,
      /secToken['"]?\s*[:=]\s*['"]([^'"]+)['"]/,
      /sec_token['"]?\s*[:=]\s*['"]([^'"]+)['"]/
    ];
    for (var i = 0; i < patterns.length; i++) {
      var match = html.match(patterns[i]);
      if (match && match[1]) return match[1];
    }
    return '';
  }
  function region() {
    var host = location.hostname.toLowerCase();
    if (host.indexOf('aliyun.com') >= 0) {
      return {
        quotaUrl: 'https://bailian-cs.console.aliyun.com/data/api.json?action=BroadScopeAspnGateway&product=sfm_bailian&api=zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2&_v=undefined',
        dashboardUrl: 'https://bailian.console.aliyun.com/cn-beijing/?tab=model#/efm/coding_plan',
        referer: 'https://bailian.console.aliyun.com/cn-beijing/?tab=model',
        domain: 'bailian.console.aliyun.com',
        site: 'BAILIAN_ALIYUN',
        regionId: 'cn-beijing',
        commodityCode: 'sfm_codingplan_public_cn',
        productCode: 'p_efm'
      };
    }
    return {
      quotaUrl: 'https://bailian-singapore-cs.alibabacloud.com/data/api.json?action=IntlBroadScopeAspnGateway&product=sfm_bailian&api=zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2&_v=undefined',
      dashboardUrl: 'https://modelstudio.console.alibabacloud.com/ap-southeast-1/?tab=coding-plan#/efm/coding_plan',
      referer: 'https://modelstudio.console.alibabacloud.com/ap-southeast-1/?tab=coding-plan',
      domain: 'modelstudio.console.alibabacloud.com',
      site: 'MODELSTUDIO_ALIBABACLOUD',
      regionId: 'ap-southeast-1',
      commodityCode: 'sfm_codingplan_public_intl',
      productCode: 'p_efm'
    };
  }
  function requestBody(r, token) {
    var traceId = (window.crypto && window.crypto.randomUUID) ? window.crypto.randomUUID().toLowerCase() : String(Date.now()) + Math.random().toString(16).slice(2);
    var paramsObject = {
      Api: 'zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2',
      V: '1.0',
      Data: {
        queryCodingPlanInstanceInfoRequest: {
          commodityCode: r.commodityCode,
          onlyLatestOne: true
        },
        cornerstoneParam: {
          feTraceId: traceId,
          feURL: r.dashboardUrl,
          protocol: 'V2',
          console: 'ONE_CONSOLE',
          productCode: r.productCode,
          domain: r.domain,
          consoleSite: r.site,
          userNickName: '',
          userPrincipalName: '',
          xsp_lang: 'en-US'
        }
      }
    };
    var cna = cookie('cna');
    if (cna) paramsObject.Data.cornerstoneParam['X-Anonymous-Id'] = cna;
    var body = new URLSearchParams();
    body.set('params', JSON.stringify(paramsObject));
    body.set('region', r.regionId);
    if (token) body.set('sec_token', token);
    return body.toString();
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_alibaba_coding_plan_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#ff6a00;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Alibaba Coding Plan...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function hasAnyKeyDeep(value, keys) {
      if (!value || typeof value !== 'object') return false;
      if (Array.isArray(value)) {
        for (var i = 0; i < value.length; i++) {
          if (hasAnyKeyDeep(value[i], keys)) return true;
        }
        return false;
      }
      for (var name in value) {
        if (!Object.prototype.hasOwnProperty.call(value, name)) continue;
        var lower = String(name).toLowerCase();
        for (var k = 0; k < keys.length; k++) {
          if (lower === keys[k]) return true;
        }
        if (hasAnyKeyDeep(value[name], keys)) return true;
      }
      return false;
    }
    function codingPayloadLooksUseful(value) {
      return hasAnyKeyDeep(value, [
        'codingplanquotainfo',
        'coding_plan_quota_info',
        'codingplaninstanceinfos',
        'coding_plan_instance_infos',
        'per5hourusedquota',
        'perfivehourusedquota',
        'per_5_hour_used_quota'
      ]);
    }
    function isVisibleLogin() {
      return window.__qlVisibleLogin === true ||
        !!(window.__qlWebLoginMode && window.__qlWebLoginMode.visible === true);
    }
    function finishTerminalError(message) {
      update('QuotaLens: ' + message, '#b91c1c');
      var payload = { __quotalensError: message };
      var json = window.__qlCaptureJson ? window.__qlCaptureJson(payload) : JSON.stringify(payload);
      window.location.hash = '#__ql__' + qlEncode(json);
    }
    function waitForManualCapture(message) {
      update('QuotaLens: ' + message, '#b45309');
      setTimeout(fetchUsage, 5000);
    }
    function fetchUsage() {
      var r = region();
      var token = secToken();
      var headers = {
        'Accept': '*/*',
        'Content-Type': 'application/x-www-form-urlencoded',
        'X-Requested-With': 'XMLHttpRequest'
      };
      var csrf = cookie('login_aliyunid_csrf') || cookie('csrf');
      if (csrf) {
        headers['x-xsrf-token'] = csrf;
        headers['x-csrf-token'] = csrf;
      }
      fetch(r.quotaUrl, {
        method: 'POST',
        credentials: 'include',
        referrer: r.referer,
        headers: headers,
        body: requestBody(r, token)
      }).then(function(response) {
        if (!response.ok) throw new Error('Alibaba Coding Plan HTTP ' + response.status);
        return response.text();
      }).then(function(text) {
        var data = JSON.parse(text);
        if (codingPayloadLooksUseful(data)) {
          update('QuotaLens: Alibaba Coding Plan data captured', '#15803d');
          var json = window.__qlCaptureJson ? window.__qlCaptureJson(data) : JSON.stringify(data);
          window.location.hash = '#__ql__' + qlEncode(json);
        } else if (data && (data.data || data.Data || data.successResponse)) {
          if (window.__qlAllowAlibabaNoQuotaError === true && !isVisibleLogin()) {
            finishTerminalError('No Alibaba Coding Plan quota was found for this account.');
          } else {
            waitForManualCapture(isVisibleLogin()
              ? 'sign in or open Alibaba Coding Plan; waiting for quota data...'
              : 'Alibaba returned no recognized Coding Plan quota yet; retrying...');
          }
        } else {
          waitForManualCapture('waiting for Alibaba Coding Plan usage...');
        }
      }).catch(function() {
        waitForManualCapture(isVisibleLogin()
          ? 'login required; please sign in, then leave this window open...'
          : 'login required or usage fetch failed. Retrying...');
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string AlibabaTokenPlanInitScript = @"
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function cookie(name) {
    var escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    var match = document.cookie.match(new RegExp('(?:^|; )' + escaped + '=([^;]*)'));
    return match ? decodeURIComponent(match[1]) : '';
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_alibaba_token_plan_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#ff6a00;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Alibaba Token Plan...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function secToken() {
      var fromCookie = cookie('sec_token');
      if (fromCookie) return fromCookie;
      var html = document.documentElement ? document.documentElement.innerHTML : '';
      var patterns = [
        /""secToken""\s*:\s*""([^""]+)""/,
        /""sec_token""\s*:\s*""([^""]+)""/,
        /secToken['""]?\s*[:=]\s*['""]([^'""]+)['""]/,
        /sec_token['""]?\s*[:=]\s*['""]([^'""]+)['""]/
      ];
      for (var i = 0; i < patterns.length; i++) {
        var match = html.match(patterns[i]);
        if (match && match[1]) return match[1];
      }
      return '';
    }
    function requestJson(url, body, headers) {
      return fetch(url, {
        method: 'POST',
        credentials: 'include',
        headers: headers,
        body: body
      }).then(function(r) {
        if (!r.ok) throw new Error('Alibaba Token Plan HTTP ' + r.status);
        return r.json();
      });
    }
    function capture(data) {
      update('QuotaLens: Alibaba Token Plan data captured', '#15803d');
      var json = window.__qlCaptureJson ? window.__qlCaptureJson(data) : JSON.stringify(data);
      window.location.hash = '#__ql__' + qlEncode(json);
    }
    function personalRequest(host, action, api, data, region, consoleSite) {
      var dashboard = window.location.href;
      var cornerstone = {
        feTraceId: Math.random().toString(36).slice(2) + Date.now().toString(36),
        feURL: dashboard,
        protocol: 'V2',
        console: 'ONE_CONSOLE',
        productCode: 'p_efm',
        switchAgent: 1233135,
        switchUserType: 3,
        domain: window.location.hostname,
        consoleSite: consoleSite,
        userNickName: '',
        userPrincipalName: '',
        xsp_lang: 'en-US'
      };
      var apiData = Object.assign({}, data || {}, { cornerstoneParam: cornerstone });
      var paramsObject = { Api: api, V: '1.0', Data: apiData };
      var body = new URLSearchParams();
      body.set('product', 'sfm_bailian');
      body.set('action', action);
      body.set('region', region);
      body.set('language', 'en-US');
      body.set('params', JSON.stringify(paramsObject));
      var query = new URLSearchParams({
        action: action,
        product: 'sfm_bailian',
        api: api,
        _v: 'undefined'
      });
      return requestJson(host + '/data/api.json?' + query.toString(), body.toString(), {
        'Content-Type': 'application/x-www-form-urlencoded',
        'Accept': 'application/json, text/plain, */*',
        'X-Requested-With': 'XMLHttpRequest'
      });
    }
    function fetchPersonal() {
      var isInternational = /alibabacloud\.com$/i.test(window.location.hostname) || /ap-southeast-1/i.test(window.location.href);
      var host = isInternational
        ? 'https://bailian-singapore-cs.alibabacloud.com'
        : 'https://bailian-cs.console.aliyun.com';
      var action = isInternational ? 'IntlBroadScopeAspnGateway' : 'BroadScopeAspnGateway';
      var region = isInternational ? 'ap-southeast-1' : 'cn-beijing';
      var consoleSite = isInternational ? 'MODELSTUDIO_ALBABACLOUD' : 'BAILIAN_ALIYUN';
      var productCode = isInternational ? 'sfm_tokenplansolo_public_intl' : 'sfm_tokenplansolo_public_cn';
      var usageApi = 'zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/usage';
      var subscriptionApi = 'zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/subscription';
      var quotaConfigApi = 'zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/quota-config';
      return personalRequest(host, action, usageApi, {}, region, consoleSite).then(function(usage) {
        var subscription = personalRequest(
          host, action, subscriptionApi, { commodityCode: productCode }, region, consoleSite).catch(function() { return null; });
        var quotaConfig = personalRequest(
          host, action, quotaConfigApi, {}, region, consoleSite).catch(function() { return null; });
        return Promise.all([subscription, quotaConfig]).then(function(metadata) {
          return { usage: usage, subscription: metadata[0], quotaConfig: metadata[1] };
        });
      });
    }
    function fetchTeam() {
      var params = new URLSearchParams();
      params.set('product', 'BssOpenAPI-V3');
      params.set('action', 'GetSubscriptionSummary');
      params.set('params', JSON.stringify({ ProductCode: 'sfm_tokenplanteams_dp_cn' }));
      params.set('region', 'cn-beijing');
      var token = secToken();
      if (token) params.set('sec_token', token);
      var headers = {
        'Content-Type': 'application/x-www-form-urlencoded',
        'Accept': '*/*',
        'X-Requested-With': 'XMLHttpRequest'
      };
      var csrf = cookie('login_aliyunid_csrf') || cookie('csrf');
      if (csrf) {
        headers['x-xsrf-token'] = csrf;
        headers['x-csrf-token'] = csrf;
      }
      return requestJson(
        'https://bailian.console.aliyun.com/data/api.json?action=GetSubscriptionSummary&product=BssOpenAPI-V3&_tag=',
        params.toString(),
        headers);
    }
    function fetchUsage() {
      fetchPersonal().then(function(data) {
        var text = JSON.stringify(data && data.usage || {});
        if (/per5HourPercentage|per1WeekPercentage/.test(text)) {
          capture(data);
          return;
        }
        throw new Error('Personal usage windows unavailable');
      }).catch(function() {
        return fetchTeam().then(function(data) {
          if (data && (data.Data || data.data || data.successResponse || data.totalQuota || data.remainingQuota || data.totalSurplusValue)) {
            capture(data);
            return;
          }
          throw new Error('Team usage unavailable');
        });
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
";

    // Amp settings capture. CodexBar fetches https://ampcode.com/settings with
    // a session cookie, then parses the embedded freeTierUsage payload. In
    // QuotaLens this runs inside the logged-in settings page and sends only the
    // compact numeric payload back through the shared hash channel.
    private const string AmpInitScript = @"
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function extractObject(token, text) {
    var pos = text.indexOf(token);
    if (pos < 0) return null;
    var brace = text.indexOf('{', pos + token.length);
    if (brace < 0) return null;
    var depth = 0, inString = false, escaped = false;
    for (var i = brace; i < text.length; i++) {
      var ch = text[i];
      if (inString) {
        if (escaped) escaped = false;
        else if (ch === '\\') escaped = true;
        else if (ch === '""') inString = false;
      } else {
        if (ch === '""') inString = true;
        else if (ch === '{') depth++;
        else if (ch === '}') {
          depth--;
          if (depth === 0) return text.substring(brace, i + 1);
        }
      }
    }
    return null;
  }
  function numberFor(key, text) {
    var match = text.match(new RegExp('\\b' + key + '\\b\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)'));
    return match ? Number(match[1]) : null;
  }
  function numeric(value) {
    return value == null ? null : Number(String(value).replace(/,/g, ''));
  }
  function parseAmp(html, visibleText) {
    var object = extractObject('freeTierUsage', html) || extractObject('getFreeTierUsage', html);
    var payload = {};
    if (object) {
      var quota = numberFor('quota', object);
      var used = numberFor('used', object);
      var hourly = numberFor('hourlyReplenishment', object);
      if (quota != null && used != null && hourly != null) {
        payload.freeQuota = quota;
        payload.freeUsed = used;
        payload.hourlyReplenishment = hourly;
        payload.windowHours = numberFor('windowHours', object);
      }
    }

    var text = visibleText || '';
    var free = /^\s*Amp Free:\s*\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s*\/\s*\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s+remaining(?:\s*\(replenishes\s*\+\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s*\/\s*hour\))?/im.exec(text);
    if (free) {
      var remaining = numeric(free[1]);
      var freeQuota = numeric(free[2]);
      var replenishment = numeric(free[3]) || 0;
      payload.freeQuota = freeQuota;
      payload.freeUsed = Math.max(0, freeQuota - remaining);
      payload.hourlyReplenishment = replenishment;
      payload.windowHours = replenishment > 0 ? Math.max(1, Math.round(freeQuota / replenishment)) : null;
    } else {
      var daily = /^\s*Amp Free:\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*%\s+remaining(?:\s+today)?(?:\s*\(resets\s+daily\))?/im.exec(text);
      if (daily) {
        var remainingPercent = Math.max(0, Math.min(100, numeric(daily[1])));
        payload.freeQuota = 100;
        payload.freeUsed = 100 - remainingPercent;
        payload.hourlyReplenishment = 0;
        payload.windowHours = 24;
      }
    }

    var subscription = /^\s*Subscription\s+(.+?):\s*(.+)$/im.exec(text);
    if (subscription) {
      var details = subscription[2];
      var other = /([0-9][0-9,]*(?:\.[0-9]+)?)\s*%\s+other\s+usage/i.exec(details);
      var orb = /([0-9][0-9,]*(?:\.[0-9]+)?)\s*%\s+orb\s+usage/i.exec(details);
      var renewal = /renewal\s+in\s+([0-9][0-9,]*)\s+days?/i.exec(details);
      if (other || orb) {
        payload.subscription = {
          plan: subscription[1].trim(),
          otherRemainingPercent: other ? numeric(other[1]) : null,
          orbRemainingPercent: orb ? numeric(orb[1]) : null,
          renewalDays: renewal ? numeric(renewal[1]) : null
        };
      }
    }

    var individual = /^\s*Individual credits:\s*\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s+remaining/im.exec(text);
    if (individual) payload.individualCredits = numeric(individual[1]);
    var workspacePattern = /^\s*Workspace\s+.+?:\s*\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s+remaining/gim;
    var workspaceMatch, workspaceTotal = 0, workspaceCount = 0;
    while ((workspaceMatch = workspacePattern.exec(text)) !== null) {
      workspaceTotal += numeric(workspaceMatch[1]);
      workspaceCount++;
    }
    if (workspaceCount > 0) {
      payload.workspaceCreditTotal = workspaceTotal;
      payload.workspaceCount = workspaceCount;
    }

    return Object.keys(payload).length > 0 ? payload : null;
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }

    var banner = document.createElement('div');
    banner.id = '__ql_amp_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#111827;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Amp...';
    document.body.appendChild(banner);

    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function capture() {
      var html = document.documentElement ? document.documentElement.outerHTML : '';
      var visibleText = document.body ? document.body.innerText : '';
      var usage = parseAmp(html, visibleText);
      if (usage) {
        update('QuotaLens: Amp data captured', '#15803d');
        window.location.hash = '#__ql__' + qlEncode(JSON.stringify(usage));
        return;
      }
      if (/sign in|log in|login|\/login/i.test(html)) {
        update('QuotaLens: login required. Retrying...', '#b91c1c');
      } else {
        update('QuotaLens: waiting for Amp usage data...', '#b45309');
      }
      setTimeout(capture, 5000);
    }

    setTimeout(capture, 1000);
  }
  tryInit();
})();
";

    private const string CursorInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_cursor_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#111827;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Cursor...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchJson(path) {
      return fetch('https://cursor.com' + path, {
        method: 'GET',
        credentials: 'include',
        headers: { 'Accept': 'application/json' }
      }).then(function(r) {
        if (!r.ok) throw new Error(path + ' HTTP ' + r.status);
        return r.json();
      });
    }
    function fetchUsage() {
      Promise.all([
        fetchJson('/api/usage-summary'),
        fetchJson('/api/auth/me').catch(function() { return null; })
      ]).then(function(results) {
        var summary = results[0];
        var user = results[1];
        var legacy = user && user.sub ? fetchJson('/api/usage?user=' + encodeURIComponent(user.sub)).catch(function() { return null; }) : Promise.resolve(null);
        return legacy.then(function(requestUsage) {
          return { usageSummary: summary, userInfo: user, requestUsage: requestUsage };
        });
      }).then(function(payload) {
        if (payload.usageSummary && (payload.usageSummary.individualUsage || payload.usageSummary.teamUsage || payload.usageSummary.billingCycleEnd)) {
          update('QuotaLens: Cursor data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(payload));
        } else {
          update('QuotaLens: waiting for Cursor usage data...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string AugmentInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_augment_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#0f766e;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Augment...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchJson(path) {
      return fetch('https://app.augmentcode.com' + path, {
        method: 'GET',
        credentials: 'include',
        headers: { 'Accept': 'application/json' }
      }).then(function(r) {
        if (!r.ok) throw new Error(path + ' HTTP ' + r.status);
        return r.json();
      });
    }
    function fetchUsage() {
      Promise.all([
        fetchJson('/api/credits'),
        fetchJson('/api/subscription').catch(function() { return {}; })
      ]).then(function(results) {
        var payload = { creditsResponse: results[0], subscriptionResponse: results[1] || {} };
        if (payload.creditsResponse && (
          payload.creditsResponse.usageUnitsRemaining != null ||
          payload.creditsResponse.usageUnitsConsumedThisBillingCycle != null ||
          payload.creditsResponse.usageUnitsAvailable != null)) {
          update('QuotaLens: Augment data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(payload));
        } else {
          update('QuotaLens: waiting for Augment credits...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string FactoryInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_factory_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#6d28d9;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Factory...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchJson(url) {
      return fetch(url, {
        method: 'GET',
        credentials: 'include',
        headers: {
          'Accept': 'application/json',
          'Content-Type': 'application/json',
          'x-factory-client': 'web-app'
        }
      }).then(function(r) {
        if (!r.ok) throw new Error(url + ' HTTP ' + r.status);
        return r.json();
      });
    }
    function firstOk(urls) {
      var index = 0;
      function next() {
        if (index >= urls.length) return Promise.resolve(null);
        return fetchJson(urls[index++]).catch(next);
      }
      return next();
    }
    function fetchUsage() {
      Promise.all([
        fetchJson('https://api.factory.ai/api/billing/limits').catch(function() { return null; }),
        firstOk([
          'https://api.factory.ai/api/app/auth/me',
          'https://app.factory.ai/api/app/auth/me',
          'https://auth.factory.ai/api/app/auth/me'
        ]),
        firstOk([
          'https://api.factory.ai/api/organization/subscription/usage?useCache=true',
          'https://app.factory.ai/api/organization/subscription/usage?useCache=true',
          'https://auth.factory.ai/api/organization/subscription/usage?useCache=true'
        ])
      ]).then(function(results) {
        var payload = { billingLimits: results[0], authInfo: results[1], usageResponse: results[2] };
        if ((payload.billingLimits && payload.billingLimits.limits) || (payload.usageResponse && payload.usageResponse.usage)) {
          update('QuotaLens: Factory data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(payload));
        } else {
          update('QuotaLens: waiting for Factory usage...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string MiniMaxInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_minimax_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#fe603c;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking MiniMax...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchJson(url) {
      return fetch(url, {
        method: 'GET',
        credentials: 'include',
        headers: {
          'Accept': 'application/json, text/plain, */*',
          'X-Requested-With': 'XMLHttpRequest'
        }
      }).then(function(r) {
        if (!r.ok) throw new Error(url + ' HTTP ' + r.status);
        return r.json();
      });
    }
    function fetchFirst(urls) {
      var index = 0;
      function next() {
        if (index >= urls.length) throw new Error('MiniMax quota endpoint unavailable');
        return fetchJson(urls[index++]).catch(next);
      }
      return next();
    }
    function fetchUsage() {
      fetchFirst([
        'https://api.minimax.io/v1/token_plan/remains',
        'https://platform.minimax.io/v1/api/openplatform/coding_plan/remains',
        'https://api.minimaxi.com/v1/token_plan/remains',
        'https://platform.minimaxi.com/v1/api/openplatform/coding_plan/remains',
        'https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains'
      ]).then(function(data) {
        if (data && ((data.data && (data.data.model_remains || data.data.services)) || data.model_remains || data.services)) {
          update('QuotaLens: MiniMax data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(data));
        } else {
          update('QuotaLens: waiting for MiniMax coding plan data...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string WindsurfInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function storageValue(key) {
    var value = localStorage.getItem(key);
    if (!value) return '';
    value = String(value).trim();
    try {
      var parsed = JSON.parse(value);
      if (typeof parsed === 'string') return parsed.trim();
    } catch (_) {}
    return value.replace(/^["']|["']$/g, '').trim();
  }
  function appendVarint(bytes, value) {
    value = Number(value || 0);
    while (value >= 128) {
      bytes.push((value & 127) | 128);
      value = Math.floor(value / 128);
    }
    bytes.push(value);
  }
  function appendKey(bytes, field, wire) {
    appendVarint(bytes, field * 8 + wire);
  }
  function appendString(bytes, value) {
    var encoded = new TextEncoder().encode(value);
    appendVarint(bytes, encoded.length);
    for (var i = 0; i < encoded.length; i++) bytes.push(encoded[i]);
  }
  function encodeRequest(token) {
    var bytes = [];
    appendKey(bytes, 1, 2);
    appendString(bytes, token);
    appendKey(bytes, 2, 0);
    appendVarint(bytes, 1);
    return new Uint8Array(bytes);
  }
  function reader(data) {
    return {
      data: data,
      i: 0,
      varint: function() {
        var result = 0;
        var shift = 0;
        while (this.i < this.data.length) {
          var b = this.data[this.i++];
          result += (b & 127) * Math.pow(2, shift);
          if ((b & 128) === 0) return result;
          shift += 7;
        }
        throw new Error('truncated varint');
      },
      bytes: function() {
        var len = this.varint();
        var end = this.i + len;
        if (end > this.data.length) throw new Error('truncated bytes');
        var out = this.data.slice(this.i, end);
        this.i = end;
        return out;
      },
      string: function() {
        return new TextDecoder().decode(this.bytes());
      },
      skip: function(wire) {
        if (wire === 0) { this.varint(); return; }
        if (wire === 2) { this.bytes(); return; }
        if (wire === 1) { this.i += 8; return; }
        if (wire === 5) { this.i += 4; return; }
        throw new Error('unsupported wire ' + wire);
      }
    };
  }
  function decodeTimestamp(bytes) {
    var r = reader(bytes);
    var seconds = null;
    while (r.i < r.data.length) {
      var key = r.varint();
      var field = Math.floor(key / 8);
      var wire = key & 7;
      if (field === 1 && wire === 0) seconds = r.varint();
      else r.skip(wire);
    }
    return seconds ? new Date(seconds * 1000).toISOString() : null;
  }
  function decodePlanInfo(bytes) {
    var r = reader(bytes);
    var info = {};
    while (r.i < r.data.length) {
      var key = r.varint();
      var field = Math.floor(key / 8);
      var wire = key & 7;
      if (field === 1 && wire === 0) info.teamsTier = r.varint();
      else if (field === 2 && wire === 2) info.planName = r.string();
      else r.skip(wire);
    }
    return info;
  }
  function decodePlanStatus(bytes) {
    var r = reader(bytes);
    var status = {};
    while (r.i < r.data.length) {
      var key = r.varint();
      var field = Math.floor(key / 8);
      var wire = key & 7;
      if (field === 1 && wire === 2) status.planInfo = decodePlanInfo(r.bytes());
      else if (field === 2 && wire === 2) status.planStart = decodeTimestamp(r.bytes());
      else if (field === 3 && wire === 2) status.planEnd = decodeTimestamp(r.bytes());
      else if (field === 14 && wire === 0) status.dailyQuotaRemainingPercent = r.varint();
      else if (field === 15 && wire === 0) status.weeklyQuotaRemainingPercent = r.varint();
      else if (field === 17 && wire === 0) status.dailyQuotaResetAtUnix = r.varint();
      else if (field === 18 && wire === 0) status.weeklyQuotaResetAtUnix = r.varint();
      else r.skip(wire);
    }
    return status;
  }
  function decodeResponse(buffer) {
    var r = reader(new Uint8Array(buffer));
    var payload = {};
    while (r.i < r.data.length) {
      var key = r.varint();
      var field = Math.floor(key / 8);
      var wire = key & 7;
      if (field === 1 && wire === 2) payload.planStatus = decodePlanStatus(r.bytes());
      else r.skip(wire);
    }
    return payload;
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_windsurf_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#0f766e;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Windsurf...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchUsage() {
      var sessionToken = storageValue('devin_session_token');
      var auth1Token = storageValue('devin_auth1_token');
      var accountId = storageValue('devin_account_id');
      var primaryOrgId = storageValue('devin_primary_org_id');
      if (!sessionToken || !auth1Token || !accountId || !primaryOrgId) {
        update('QuotaLens: waiting for Windsurf sign-in...', '#b45309');
        setTimeout(fetchUsage, 5000);
        return;
      }
      fetch('https://windsurf.com/_backend/exa.seat_management_pb.SeatManagementService/GetPlanStatus', {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/proto',
          'Connect-Protocol-Version': '1',
          'x-auth-token': sessionToken,
          'x-devin-session-token': sessionToken,
          'x-devin-auth1-token': auth1Token,
          'x-devin-account-id': accountId,
          'x-devin-primary-org-id': primaryOrgId
        },
        body: encodeRequest(sessionToken)
      }).then(function(r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.arrayBuffer();
      }).then(function(buffer) {
        var payload = decodeResponse(buffer);
        if (payload && payload.planStatus) {
          update('QuotaLens: Windsurf data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(payload));
        } else {
          update('QuotaLens: waiting for Windsurf plan data...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string ManusInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function cookie(name) {
    var escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    var match = document.cookie.match(new RegExp('(?:^|; )' + escaped + '=([^;]*)'));
    return match ? decodeURIComponent(match[1]) : '';
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_manus_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#111827;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Manus...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchUsage() {
      var token = cookie('session_id') || cookie('sessionid') || cookie('__Secure-session_id');
      var headers = {
        'Accept': 'application/json',
        'Content-Type': 'application/json',
        'Connect-Protocol-Version': '1'
      };
      if (token) headers.Authorization = 'Bearer ' + token;
      fetch('https://api.manus.im/user.v1.UserService/GetAvailableCredits', {
        method: 'POST',
        credentials: 'include',
        headers: headers,
        body: '{}'
      }).then(function(r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      }).then(function(data) {
        if (data && (data.totalCredits != null || data.data || data.result || data.availableCredits)) {
          update('QuotaLens: Manus data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(data));
        } else {
          update('QuotaLens: waiting for Manus credits...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string PerplexityInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_perplexity_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#0f766e;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Perplexity...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchUsage() {
      fetch('/rest/billing/credits?version=2.18&source=default', {
        method: 'GET',
        credentials: 'include',
        headers: { 'Accept': 'application/json' }
      }).then(function(r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      }).then(function(data) {
        if (data && data.credit_grants) {
          update('QuotaLens: Perplexity data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(data));
        } else {
          update('QuotaLens: waiting for Perplexity credits...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string T3ChatInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_t3chat_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#ea580c;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking T3 Chat...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchUsage() {
      var input = encodeURIComponent(JSON.stringify({0:{json:{sessionId:null},meta:{values:{sessionId:['undefined']}}}}));
      fetch('/api/trpc/getCustomerData?batch=1&input=' + input, {
        method: 'GET',
        credentials: 'include',
        headers: {
          'Accept': '*/*',
          'trpc-accept': 'application/jsonl',
          'x-trpc-source': 'web-client',
          'x-trpc-batch': 'true'
        }
      }).then(function(r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.text();
      }).then(function(text) {
        if (text && /usageFourHourPercentage|usageMonthPercentage|usagePeriodPercentage/.test(text)) {
          update('QuotaLens: T3 Chat data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(text);
        } else {
          update('QuotaLens: waiting for T3 Chat usage...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string CommandCodeInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_commandcode_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#0f172a;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Command Code...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchJson(path) {
      return fetch('https://api.commandcode.ai' + path, {
        method: 'GET',
        credentials: 'include',
        headers: {
          'Accept': 'application/json, text/plain, */*'
        }
      }).then(function(r) {
        if (!r.ok) throw new Error(path + ' HTTP ' + r.status);
        return r.json();
      });
    }
    function fetchUsage() {
      Promise.all([
        fetchJson('/internal/billing/credits'),
        fetchJson('/internal/billing/subscriptions')
      ]).then(function(results) {
        var data = { creditsResponse: results[0], subscriptionResponse: results[1] };
        if (results[0] && results[0].credits) {
          update('QuotaLens: Command Code data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(data));
        } else {
          update('QuotaLens: waiting for Command Code credits...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string OllamaInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function firstCapture(text, re) {
    var match = re.exec(text);
    return match ? match[1].trim() : null;
  }
  function parseDate(text) {
    var raw = firstCapture(text, /data-time=\"([^\"]+)\"/);
    return raw || null;
  }
  function parsePercent(text) {
    var used = firstCapture(text, /([0-9]+(?:\.[0-9]+)?)\s*%\s*used/i);
    if (used != null) return Number(used);
    var width = firstCapture(text, /width:\s*([0-9]+(?:\.[0-9]+)?)%/i);
    return width != null ? Number(width) : null;
  }
  function blockAfter(label, html) {
    var pos = html.indexOf(label);
    if (pos < 0) return '';
    var tail = html.substring(pos + label.length);
    var labels = ['Session usage', 'Hourly usage', 'Weekly usage'];
    var end = tail.length;
    labels.forEach(function(other) {
      if (other === label) return;
      var idx = tail.indexOf(other);
      if (idx >= 0 && idx < end) end = idx;
    });
    return tail.substring(0, Math.min(end, 4000));
  }
  function parseUsage(html) {
    var sessionBlock = blockAfter('Session usage', html) || blockAfter('Hourly usage', html);
    var weeklyBlock = blockAfter('Weekly usage', html);
    var plan = firstCapture(html, /Cloud Usage\s*<\/span>\s*<span[^>]*>([^<]+)<\/span>/i);
    var email = firstCapture(html, /id=\"header-email\"[^>]*>([^<]+)</i);
    if (email && email.indexOf('@') < 0) email = null;
    return {
      planName: plan,
      accountEmail: email,
      sessionUsedPercent: parsePercent(sessionBlock),
      weeklyUsedPercent: parsePercent(weeklyBlock),
      sessionResetsAt: parseDate(sessionBlock),
      weeklyResetsAt: parseDate(weeklyBlock),
      sessionWindowMinutes: 300
    };
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_ollama_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#374151;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Ollama...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function capture() {
      var html = document.documentElement ? document.documentElement.outerHTML : '';
      var usage = parseUsage(html);
      if (usage.sessionUsedPercent != null || usage.weeklyUsedPercent != null) {
        update('QuotaLens: Ollama data captured', '#15803d');
        window.location.hash = '#__ql__' + qlEncode(JSON.stringify(usage));
        return;
      }
      update('QuotaLens: waiting for Ollama usage data...', /sign in|log in|login|\/login/i.test(html) ? '#b91c1c' : '#b45309');
      setTimeout(capture, 5000);
    }
    setTimeout(capture, 1000);
  }
  tryInit();
})();
""";

    private const string AbacusInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_abacus_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#0369a1;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Abacus AI...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchJson(path, method) {
      return fetch('https://apps.abacus.ai/api/' + path, {
        method: method || 'GET',
        credentials: 'include',
        headers: { 'Accept': 'application/json', 'Content-Type': 'application/json' },
        body: method === 'POST' ? '{}' : undefined
      }).then(function(r) {
        if (!r.ok) throw new Error(path + ' HTTP ' + r.status);
        return r.json();
      }).then(function(data) {
        if (data && data.success && data.result) return data.result;
        throw new Error(path + ' returned no result');
      });
    }
    function fetchUsage() {
      Promise.all([
        fetchJson('_getOrganizationComputePoints', 'GET'),
        fetchJson('_getBillingInfo', 'POST').catch(function() { return {}; })
      ]).then(function(results) {
        var payload = { computePoints: results[0], billingInfo: results[1] || {} };
        if (payload.computePoints && payload.computePoints.totalComputePoints != null && payload.computePoints.computePointsLeft != null) {
          update('QuotaLens: Abacus AI data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(payload));
        } else {
          update('QuotaLens: waiting for Abacus AI credits...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string StepFunInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_stepfun_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#1976d2;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking StepFun...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function postJson(path) {
      return fetch('https://platform.stepfun.com' + path, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Accept': 'application/json',
          'Content-Type': 'application/json',
          'oasis-appid': '10300',
          'oasis-platform': 'web',
          'oasis-webid': 'c8a1002d2c457e758785a9979832217c7c0b884c'
        },
        body: '{}'
      }).then(function(r) {
        if (!r.ok) throw new Error(path + ' HTTP ' + r.status);
        return r.json();
      });
    }
    function fetchUsage() {
      Promise.all([
        postJson('/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit'),
        postJson('/api/step.openapi.devcenter.Dashboard/GetStepPlanStatus').catch(function() { return {}; })
      ]).then(function(results) {
        var usage = results[0] || {};
        var plan = results[1] && results[1].subscription ? results[1].subscription.name : null;
        if (plan) usage.planName = plan;
        var credit = usage.plan_credit_rate_limit;
        var hasCredit = !!credit && (
          credit.subscription_credit_left_rate != null ||
          credit.topup_credit_left_rate != null ||
          (Array.isArray(credit.credit_buckets) && credit.credit_buckets.length > 0));
        var hasWindows = usage.five_hour_usage_left_rate != null && usage.weekly_usage_left_rate != null;
        if (hasWindows || hasCredit || Number(usage.plan_family) === 2) {
          update('QuotaLens: StepFun data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(usage));
        } else {
          update('QuotaLens: waiting for StepFun quota...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string OpenCodeInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  var workspaceServerId = 'def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f';
  var subscriptionServerId = '7abeebee372f304e050aaaf92be863f4a86490e382f8c79db68fd94040d691b4';
  function workspaceIdFromText(text) {
    var match = /wrk_[A-Za-z0-9]+/.exec(text || '');
    return match ? match[0] : null;
  }
  function numberAfter(text, name) {
    var re = new RegExp('(?:' + name + ')[^0-9.]*([0-9]+(?:\\.[0-9]+)?)', 'i');
    var match = re.exec(text || '');
    return match ? Number(match[1]) : null;
  }
  function normalizeUsage(text) {
    try { return JSON.parse(text); } catch (_) {}
    var rolling = {
      usagePercent: numberAfter(text, 'rollingUsage[^}]*usagePercent|rolling[^}]*usagePercent'),
      resetInSec: numberAfter(text, 'rollingUsage[^}]*resetInSec|rolling[^}]*resetInSec')
    };
    var weekly = {
      usagePercent: numberAfter(text, 'weeklyUsage[^}]*usagePercent|weekly[^}]*usagePercent'),
      resetInSec: numberAfter(text, 'weeklyUsage[^}]*resetInSec|weekly[^}]*resetInSec')
    };
    var renew = /renew(?:s)?At[^0-9A-Za-z]*([0-9T:Z.+-]+)/i.exec(text || '');
    return { rollingUsage: rolling, weeklyUsage: weekly, renewAt: renew ? renew[1] : null };
  }
  function serverFetch(serverId, args, method, referer) {
    var url = 'https://opencode.ai/_server';
    var init = {
      method: method || 'GET',
      credentials: 'include',
      headers: {
        'Accept': 'text/javascript, application/json;q=0.9, */*;q=0.8',
        'X-Server-Id': serverId,
        'X-Server-Instance': 'server-fn:' + Math.random().toString(36).slice(2),
        'Origin': 'https://opencode.ai',
        'Referer': referer || 'https://opencode.ai'
      }
    };
    if (init.method === 'GET') {
      url += '?id=' + encodeURIComponent(serverId);
      if (args && args.length) url += '&args=' + encodeURIComponent(JSON.stringify(args));
    } else {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(args || []);
    }
    return fetch(url, init).then(function(r) {
      if (!r.ok) throw new Error('OpenCode _server HTTP ' + r.status);
      return r.text();
    });
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_opencode_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#111827;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking OpenCode...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchUsage() {
      serverFetch(workspaceServerId, null, 'GET', 'https://opencode.ai').then(function(workspacesText) {
        var workspaceId = workspaceIdFromText(workspacesText);
        if (!workspaceId) throw new Error('No workspace id');
        return serverFetch(subscriptionServerId, [workspaceId], 'GET', 'https://opencode.ai/workspace/' + workspaceId + '/billing');
      }).then(function(text) {
        var payload = normalizeUsage(text);
        if (payload && (payload.rollingUsage || payload.weeklyUsage)) {
          update('QuotaLens: OpenCode data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(payload));
        } else {
          update('QuotaLens: waiting for OpenCode quota...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string OpenCodeGoInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  var workspaceServerId = 'def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f';
  var billingServerId = 'c83b78a614689c38ebee981f9b39a8b377716db85c1fd7dbab604adc02d3313d';
  function workspaceIdFromText(text) {
    var match = /wrk_[A-Za-z0-9]+/.exec(text || '');
    return match ? match[0] : null;
  }
  function numberAfter(text, name) {
    var re = new RegExp('(?:' + name + ')[^0-9.]*([0-9]+(?:\\.[0-9]+)?)', 'i');
    var match = re.exec(text || '');
    return match ? Number(match[1]) : null;
  }
  function normalizeUsage(text) {
    function compactWindow(name) {
      var percent = numberAfter(text, name + '[^}]*usagePercent|' + name.replace('Usage', '') + '[^}]*usagePercent');
      var reset = numberAfter(text, name + '[^}]*resetInSec|' + name.replace('Usage', '') + '[^}]*resetInSec');
      var used = numberAfter(text, name + '[^}]*used');
      var limit = numberAfter(text, name + '[^}]*limit');
      if (percent == null && used != null && limit > 0) percent = used / limit * 100;
      if (percent == null) return null;
      var value = { usagePercent: percent };
      if (reset != null) value.resetInSec = reset;
      return value;
    }
    var payload = {};
    var rolling = compactWindow('rollingUsage');
    var weekly = compactWindow('weeklyUsage');
    var monthly = compactWindow('monthlyUsage');
    if (rolling) payload.rollingUsage = rolling;
    if (weekly) payload.weeklyUsage = weekly;
    if (monthly) payload.monthlyUsage = monthly;
    return payload;
  }
  function normalizeBilling(text) {
    function findRaw(value) {
      if (!value || typeof value !== 'object') return null;
      if (!Array.isArray(value) && Object.prototype.hasOwnProperty.call(value, 'balance')) {
        var customer = value.customerID;
        var raw = Number(value.balance);
        if (typeof customer === 'string' && customer.length > 0 && Number.isFinite(raw)) return raw;
      }
      var values = Array.isArray(value) ? value : Object.keys(value).map(function(key) { return value[key]; });
      for (var i = 0; i < values.length; i++) {
        var nested = findRaw(values[i]);
        if (nested != null) return nested;
      }
      return null;
    }
    var raw = null;
    try { raw = findRaw(JSON.parse(text)); } catch (_) {}
    if (raw == null) {
      var customer = /(?:"customerID"|customerID)\s*:\s*(?:\$R\[\d+\]\s*=\s*)?"([^"]+)"/.exec(text || '');
      var balance = /(?:"balance"|balance)\s*:\s*(?:\$R\[\d+\]\s*=\s*)?(-?[0-9]+(?:\.[0-9]+)?)/.exec(text || '');
      if (customer && customer[1] && balance) raw = Number(balance[1]);
    }
    return raw != null && Number.isFinite(raw) ? raw / 100000000 : null;
  }
  function serverFetch(serverId, args) {
    var url = 'https://opencode.ai/_server?id=' + encodeURIComponent(serverId);
    if (args) url += '&args=' + encodeURIComponent(args);
    return fetch(url, {
      method: 'GET',
      credentials: 'include',
      headers: {
        'Accept': 'text/javascript, application/json;q=0.9, */*;q=0.8',
        'X-Server-Id': serverId,
        'X-Server-Instance': 'server-fn:' + Math.random().toString(36).slice(2),
        'Origin': 'https://opencode.ai',
        'Referer': 'https://opencode.ai'
      }
    }).then(function(r) {
      if (!r.ok) throw new Error('OpenCode workspace HTTP ' + r.status);
      return r.text();
    });
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_opencodego_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#111827;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking OpenCode Go...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function fetchUsage() {
      serverFetch(workspaceServerId).then(function(workspacesText) {
        var workspaceId = workspaceIdFromText(workspacesText);
        if (!workspaceId) throw new Error('No workspace id');
        var usage = fetch('https://opencode.ai/workspace/' + workspaceId + '/go', {
          method: 'GET',
          credentials: 'include',
          headers: { 'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8' }
        }).then(function(r) {
          if (!r.ok) throw new Error('OpenCode Go HTTP ' + r.status);
          return r.text();
        }).catch(function() { return ''; });
        var billing = serverFetch(billingServerId, JSON.stringify([workspaceId])).catch(function() { return ''; });
        return Promise.all([usage, billing]);
      }).then(function(results) {
        var payload = normalizeUsage(results[0]);
        var balance = normalizeBilling(results[1]);
        if (balance != null) payload.zenBalanceUSD = balance;
        if (payload.rollingUsage || balance != null) {
          update('QuotaLens: OpenCode Go data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify(payload));
        } else {
          update('QuotaLens: waiting for OpenCode Go quota...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";

    private const string MistralInitScript = """
(function() {
  function qlEncode(s) {
    return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
  }
  function tryInit() {
    if (!document.body) { setTimeout(tryInit, 200); return; }
    var banner = document.createElement('div');
    banner.id = '__ql_mistral_banner';
    banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:2147483647;background:#ff500f;color:#fff;padding:8px 12px;text-align:center;font:bold 14px sans-serif;';
    banner.textContent = 'QuotaLens: checking Mistral...';
    document.body.appendChild(banner);
    function update(msg, bg) { banner.textContent = msg; if (bg) banner.style.background = bg; }
    function cookieValue(name) {
      var prefix = name + '=';
      var parts = String(document.cookie || '').split(';');
      for (var i = 0; i < parts.length; i++) {
        var part = parts[i].trim();
        if (part.indexOf(prefix) === 0) return part.substring(prefix.length);
      }
      return null;
    }
    function fetchJson(url, headers, signal) {
      return fetch(url, {
        method: 'GET',
        credentials: 'include',
        headers: headers || { 'Accept': '*/*' },
        signal: signal
      }).then(function(r) {
        if (!r.ok) throw new Error(url + ' HTTP ' + r.status);
        return r.json();
      });
    }
    function optionalJson(url, headers) {
      var controller = new AbortController();
      var timer = setTimeout(function() { controller.abort(); }, 4000);
      return fetchJson(url, headers, controller.signal)
        .catch(function() { return null; })
        .finally(function() { clearTimeout(timer); });
    }
    function normalizeVibe(data) {
      var value = data && data[0] && data[0].result && data[0].result.data && data[0].result.data.json;
      if (!value) return null;
      var percent = Number(value.usage_percentage);
      if (!Number.isFinite(percent) || percent < 0 || percent > 100) return null;
      return { usagePercentage: percent, resetAt: value.reset_at || null };
    }
    function fetchUsage() {
      var now = new Date();
      var month = now.getUTCMonth() + 1;
      var year = now.getUTCFullYear();
      var usageUrl = 'https://admin.mistral.ai/api/billing/v2/usage?month=' + month + '&year=' + year;
      fetchJson(usageUrl, { 'Accept': '*/*' }).then(function(usage) {
        var csrf = cookieValue('csrftoken');
        var adminHeaders = { 'Accept': '*/*' };
        if (csrf) adminHeaders['X-CSRFTOKEN'] = csrf;
        var credits = optionalJson('https://admin.mistral.ai/api/billing/credits', adminHeaders);
        var vibe = csrf
          ? optionalJson(
              'https://console.mistral.ai/api-ui/trpc/billing.vibeUsage?batch=1&input=%7B%220%22%3A%7B%22json%22%3Anull%2C%22meta%22%3A%7B%22values%22%3A%5B%22undefined%22%5D%2C%22v%22%3A1%7D%7D%7D',
              { 'Accept': '*/*', 'X-CSRFToken': csrf })
              .then(normalizeVibe)
          : Promise.resolve(null);
        return Promise.all([Promise.resolve(usage), credits, vibe]);
      }).then(function(results) {
        var usage = results[0];
        if (usage && (usage.completion || usage.prices || usage.currency)) {
          update('QuotaLens: Mistral data captured', '#15803d');
          window.location.hash = '#__ql__' + qlEncode(JSON.stringify({
            usage: usage,
            credits: results[1],
            vibe: results[2]
          }));
        } else {
          update('QuotaLens: waiting for Mistral billing usage...', '#b45309');
          setTimeout(fetchUsage, 5000);
        }
      }).catch(function() {
        update('QuotaLens: login required or usage fetch failed. Retrying...', '#b91c1c');
        setTimeout(fetchUsage, 5000);
      });
    }
    setTimeout(fetchUsage, 1000);
  }
  tryInit();
})();
""";
}
