using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Providers;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class ProviderConnectionTests
{
    [TestMethod]
    public void State_HealthySelectedSourceHidesSignIn()
    {
        var source = Source(ProviderSourceMode.Cli, available: true);
        var snapshot = new ProviderSnapshot
        {
            SourceState = new ProviderSourceState("cli", "cli", false),
        };

        var state = ProviderConnectionStates.Evaluate(
            source, "gemini", new MapConfig(), "cli", snapshot);
        var action = new CliProviderConnectionAction("gemini");

        Assert.IsTrue(state.IsVerified);
        Assert.IsFalse(action.ShouldOffer(state));
    }

    [TestMethod]
    public void State_MissingCredentialsShowsSignInBeforeFetch()
    {
        var source = Source(ProviderSourceMode.Cli, available: false);
        var state = ProviderConnectionStates.Evaluate(
            source, "gemini", new MapConfig(), "cli", snapshot: null);

        Assert.IsTrue(new CliProviderConnectionAction("gemini").ShouldOffer(state));
    }

    [TestMethod]
    public void State_UnknownAvailabilityDoesNotClaimTheUserIsSignedOut()
    {
        var source = new ProviderSource(
            ProviderSourceMode.Cli,
            (_, _) => throw new IOException("probe failed"),
            (_, _, _) => Task.FromResult(new ProviderSnapshot()));
        var state = ProviderConnectionStates.Evaluate(
            source, "gemini", new MapConfig(), "cli", snapshot: null);

        Assert.IsFalse(state.AvailabilityKnown);
        Assert.IsFalse(new CliProviderConnectionAction("gemini").ShouldOffer(state));
    }

    [TestMethod]
    public void State_AuthenticationErrorFromDifferentSourceDoesNotShowSignIn()
    {
        var source = Source(ProviderSourceMode.Cli, available: true);
        var snapshot = new ProviderSnapshot
        {
            Error = "Login required",
            ErrorKind = ProviderErrorKind.AuthenticationRequired,
            SourceState = new ProviderSourceState("app", "app", false),
        };
        var state = ProviderConnectionStates.Evaluate(
            source, "gemini", new MapConfig(), "app", snapshot);

        Assert.IsFalse(state.AuthenticationRequired);
        Assert.IsFalse(new CliProviderConnectionAction("gemini").ShouldOffer(state));
    }

    [TestMethod]
    public void State_CurrentDialogAuthenticationErrorAppliesToDraftSource()
    {
        var source = Source(ProviderSourceMode.Cli, available: true);
        var state = ProviderConnectionStates.Evaluate(
            source,
            "gemini",
            new MapConfig(),
            "app",
            snapshot: null,
            currentErrorKind: ProviderErrorKind.AuthenticationRequired);

        Assert.IsTrue(new CliProviderConnectionAction("gemini").ShouldOffer(state));
    }

    [TestMethod]
    public void Done_AppSourceRequiresARealVerifiedFetch()
    {
        var action = AppAction("gemini", launchInBackground: true);
        var unverified = new ProviderConnectionState(true, true, false, false);
        var verified = unverified with { IsVerified = true };

        Assert.IsFalse(ProviderConnectionStates.CanFinish(true, action, unverified));
        Assert.IsTrue(ProviderConnectionStates.CanFinish(true, action, verified));
        Assert.IsFalse(ProviderConnectionStates.CanFinish(false, action, verified));
        Assert.IsFalse(ProviderConnectionStates.CanFinish(
            true,
            action,
            verified,
            connectionInProgress: true));
    }

    [TestMethod]
    public void AppAction_UnverifiedSourceAlwaysOffersOpenApp()
    {
        var action = AppAction("kimi", launchInBackground: false);

        Assert.IsTrue(action.ShouldOffer(new ProviderConnectionState(true, true, false, false)));
        Assert.IsFalse(action.ShouldOffer(new ProviderConnectionState(true, true, true, false)));
        Assert.AreEqual("editProvider.openApp", action.LabelKey);
        Assert.AreEqual("editProvider.startingApp", action.ProgressLabelKey);
    }

    [TestMethod]
    public async Task Coordinator_PollsTheExactSourceUntilRealDataSucceeds()
    {
        var fetches = 0;
        var action = new FakeConnectionAction(started: true);
        var source = new ProviderSource(
            ProviderSourceMode.App,
            (_, _) => true,
            (_, _, _) =>
            {
                fetches++;
                if (fetches == 1)
                    throw new ProviderException("Login required: not ready");
                return Task.FromResult(new ProviderSnapshot { Name = "verified" });
            },
            connectionAction: action);

        var result = await ProviderConnectionCoordinator.ConnectAndVerifyAsync(
            source, "gemini", new MapConfig(), CancellationToken.None);

        Assert.IsTrue(result.Verified);
        Assert.AreEqual("verified", result.Snapshot?.Name);
        Assert.AreEqual(2, fetches);
        Assert.AreEqual(1, action.StartCount);
    }

    [TestMethod]
    public async Task Coordinator_CancelledExternalFlowDoesNotFetch()
    {
        var fetched = false;
        var source = new ProviderSource(
            ProviderSourceMode.Web,
            (_, _) => false,
            (_, _, _) =>
            {
                fetched = true;
                return Task.FromResult(new ProviderSnapshot());
            },
            connectionAction: new FakeConnectionAction(started: false));

        var result = await ProviderConnectionCoordinator.ConnectAndVerifyAsync(
            source, "kimi", new MapConfig(), CancellationToken.None);

        Assert.IsFalse(result.Started);
        Assert.IsFalse(fetched);
    }

    [TestMethod]
    public void Coordinator_RetriesOnlyConnectionTransitions()
    {
        Assert.IsTrue(ProviderConnectionCoordinator.CanRetry(
            new ProviderException("Login required: waiting")));
        Assert.IsTrue(ProviderConnectionCoordinator.CanRetry(
            new ProviderException("Not available: app is starting")));
        Assert.IsFalse(ProviderConnectionCoordinator.CanRetry(
            new ProviderException("Parse error: invalid quota payload")));
        Assert.IsFalse(ProviderConnectionCoordinator.CanRetry(
            new ProviderException("Not configured: missing path")));
    }

    [TestMethod]
    public async Task AppAction_CheckedAutoLaunchStartsHiddenAndWaitsForReadiness()
    {
        var ready = false;
        (string Provider, bool Background)? launch = null;
        var action = new AppProviderConnectionAction(
            "gemini",
            "gemini_app_path",
            () => ready,
            launchInBackground: true,
            autoLaunchConfigKey: "gemini_auto_launch_app",
            verificationFieldKeys: null,
            (provider, _, _, hidden) =>
            {
                launch = (provider, hidden);
                ready = true;
                return null;
            });
        const string instanceId = "gemini-4031e902";
        var config = new MapConfig
        {
            [$"{instanceId}.gemini_auto_launch_app"] = "true",
        };

        await action.PrepareAsync(instanceId, config, CancellationToken.None);

        Assert.AreEqual(("gemini", true), launch);
        Assert.IsTrue(ready);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task AppAction_BackgroundConnectionRemainsBusyUntilTheAppIsReady()
    {
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = new AppProviderConnectionAction(
            "gemini",
            "gemini_app_path",
            () => ready.Task.IsCompletedSuccessfully,
            launchInBackground: true,
            autoLaunchConfigKey: null,
            verificationFieldKeys: null,
            (_, _, _, _) =>
            {
                launched.SetResult();
                return null;
            });

        var connection = action.StartAsync(
            "gemini-4031e902",
            new MapConfig(),
            CancellationToken.None);
        await launched.Task;

        Assert.IsFalse(connection.IsCompleted);
        ready.SetResult();
        Assert.IsTrue(await connection);
    }

    [TestMethod]
    public async Task AppAction_ForegroundConnectionDoesNotWaitForBackgroundReadiness()
    {
        var launches = 0;
        var action = new AppProviderConnectionAction(
            "kimi",
            "kimi_app_path",
            () => false,
            launchInBackground: false,
            autoLaunchConfigKey: null,
            verificationFieldKeys: null,
            (_, _, _, background) =>
            {
                Assert.IsFalse(background);
                launches++;
                return null;
            });

        Assert.IsTrue(await action.StartAsync(
            "kimi-cab0cc4a",
            new MapConfig(),
            CancellationToken.None));
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public void AppAction_CheckedAutoLaunchRequestsImmediateConnectionForItsOwnField()
    {
        const string instanceId = "gemini-4031e902";
        var action = new AppProviderConnectionAction(
            "gemini",
            "gemini_app_path",
            () => false,
            launchInBackground: true,
            autoLaunchConfigKey: "gemini_auto_launch_app");
        var config = new MapConfig
        {
            [$"{instanceId}.gemini_auto_launch_app"] = "true",
        };

        Assert.IsTrue(action.ShouldConnectAfterConfigChange(
            "gemini_auto_launch_app", instanceId, config));
        Assert.IsFalse(action.ShouldConnectAfterConfigChange(
            "gemini_app_path", instanceId, config));
        Assert.IsFalse(action.ShouldConnectAfterConfigChange(
            "gemini_auto_launch_app", instanceId, new MapConfig()));
    }

    [TestMethod]
    public async Task AppAction_UncheckedAutoLaunchNeverStartsTheApp()
    {
        var launches = 0;
        var action = new AppProviderConnectionAction(
            "gemini",
            "gemini_app_path",
            () => false,
            launchInBackground: true,
            autoLaunchConfigKey: "gemini_auto_launch_app",
            verificationFieldKeys: null,
            (_, _, _, _) =>
            {
                launches++;
                return null;
            });

        await action.PrepareAsync("gemini", new MapConfig(), CancellationToken.None);

        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    [DataRow("kimi", false)]
    [DataRow("gemini", true)]
    [DataRow("zai", false)]
    public async Task AppAction_LaunchUsesProviderTypeNotGeneratedInstanceId(
        string providerType,
        bool background)
    {
        var ready = false;
        var launches = new List<(string Provider, string Instance, bool Background)>();
        var action = new AppProviderConnectionAction(
            providerType,
            providerType + "_app_path",
            () => ready,
            background,
            autoLaunchConfigKey: null,
            verificationFieldKeys: null,
            (id, instanceId, _, hidden) =>
            {
                launches.Add((id, instanceId, hidden));
                ready = true;
                return null;
            });

        var generatedInstanceId = providerType + "-4031e902";
        await action.StartAsync(
            generatedInstanceId,
            new MapConfig(),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { (providerType, generatedInstanceId, background) },
            launches.ToArray());
    }

    [TestMethod]
    public async Task SourceRunner_PreparesSelectedSourceBeforeAvailabilityCheck()
    {
        var ready = false;
        var action = new FakeConnectionAction(
            started: true,
            prepare: () => ready = true);
        var source = new ProviderSource(
            ProviderSourceMode.App,
            (_, _) => ready,
            (_, _, _) => Task.FromResult(new ProviderSnapshot { Name = "prepared" }),
            connectionAction: action);
        var config = new MapConfig
        {
            ["gemini.provider_source"] = "app",
        };

        var snapshot = await ProviderSourceRunner.FetchAsync(
            new UnusedProvider(),
            new[] { source },
            "gemini",
            config,
            CancellationToken.None);

        Assert.AreEqual("prepared", snapshot.Name);
        Assert.AreEqual(1, action.PrepareCount);
    }

    [TestMethod]
    public void Registry_EveryCliDescriptorExposesItsActionAfterTheExecutableField()
    {
        foreach (var (providerType, descriptor) in ProviderLoginCatalog.Descriptors)
        {
            var provider = ProviderRegistry.Create(providerType);
            var source = ProviderRegistry.ConnectionSourcesFor(provider)
                .Single(item => item.ConnectionAction is CliProviderConnectionAction);

            Assert.AreEqual(descriptor.CliPathFieldKey, source.ConnectionAction?.PlacementFieldKey, providerType);
            Assert.IsTrue(source.ConfigFieldKeys.Contains(descriptor.CliPathFieldKey), providerType);
        }
    }

    [TestMethod]
    public void Providers_AppSourcesOwnOpenAppActions()
    {
        foreach (var providerType in new[] { "gemini", "kimi", "zcode" })
        {
            var action = ProviderRegistry.Create(providerType).Sources
                .Single(source => source.ConnectionAction?.Kind == ProviderConnectionActionKind.OpenApp)
                .ConnectionAction!;

            Assert.IsTrue(Catalog.Fields[providerType].Any(field => field.Key == action.PlacementFieldKey));
        }
    }

    [TestMethod]
    public void Gemini_BackgroundPreferenceDoesNotInvalidateAnAlreadyVerifiedConnection()
    {
        var action = ProviderRegistry.Create("gemini").Sources
            .Single(source => source.Mode == ProviderSourceMode.App)
            .ConnectionAction!;

        CollectionAssert.Contains(action.VerificationFieldKeys.ToArray(), "gemini_app_path");
        CollectionAssert.DoesNotContain(action.VerificationFieldKeys.ToArray(), "gemini_auto_launch_app");
    }

    [TestMethod]
    public void Registry_EveryBrowserSourceOwnsASignInActionAfterItsUrlField()
    {
        foreach (var providerType in WebLoginService.SupportedTypes)
        {
            var provider = ProviderRegistry.Create(providerType);
            var source = ProviderRegistry.ConnectionSourcesFor(provider)
                .Single(item => item.Mode == ProviderSourceMode.Web);
            var action = source.ConnectionAction;

            Assert.IsNotNull(action, providerType);
            Assert.AreEqual(ProviderConnectionActionKind.SignIn, action.Kind, providerType);
            Assert.IsTrue(source.ConfigFieldKeys.Contains(action.PlacementFieldKey), providerType);
        }
    }

    [TestMethod]
    public void IdeLauncher_BackgroundStartInfoIsHiddenButForegroundIsNormal()
    {
        var background = IdeLauncher.BuildStartInfo(@"C:\Apps\Antigravity.exe", background: true);
        var foreground = IdeLauncher.BuildStartInfo(@"C:\Apps\Kimi.exe", background: false);

        Assert.AreEqual(ProcessWindowStyle.Hidden, background.WindowStyle);
        Assert.AreEqual(ProcessWindowStyle.Normal, foreground.WindowStyle);
        Assert.IsTrue(background.UseShellExecute);
    }

    [TestMethod]
    public void IdeLauncher_UsesUnsavedGlobalAppPathFromOverlay()
    {
        const string draftPath = @"C:\Draft Apps\Antigravity.exe";
        var config = new OverlayConfig(
            new MapConfig(),
            "gemini",
            globalValues: new Dictionary<string, string>
            {
                ["gemini_app_path"] = draftPath,
            });

        var configured = IdeLauncher.ConfiguredPath(Catalog.LaunchTargets["gemini"], config);

        Assert.AreEqual(draftPath, configured);
    }

    private static ProviderSource Source(ProviderSourceMode mode, bool available) => new(
        mode,
        (_, _) => available,
        (_, _, _) => Task.FromResult(new ProviderSnapshot()));

    private static AppProviderConnectionAction AppAction(string provider, bool launchInBackground) => new(
        provider,
        provider + "_app_path",
        () => false,
        launchInBackground,
        autoLaunchConfigKey: null,
        verificationFieldKeys: null,
        (_, _, _, _) => null);

    private sealed class FakeConnectionAction(
        bool started,
        Action? prepare = null) : IProviderConnectionAction
    {
        public ProviderConnectionActionKind Kind => ProviderConnectionActionKind.OpenApp;
        public string LabelKey => "test";
        public string PlacementFieldKey => "test_path";
        public TimeSpan VerificationTimeout => TimeSpan.FromSeconds(1);
        public TimeSpan VerificationRetryDelay => TimeSpan.FromMilliseconds(1);
        public int StartCount { get; private set; }
        public int PrepareCount { get; private set; }
        public bool ShouldOffer(ProviderConnectionState state) => true;
        public Task<bool> StartAsync(string instanceId, IConfig config, CancellationToken ct)
        {
            StartCount++;
            return Task.FromResult(started);
        }

        public Task PrepareAsync(string instanceId, IConfig config, CancellationToken ct)
        {
            PrepareCount++;
            prepare?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedProvider : IProvider
    {
        public string Type => "gemini";
        public string Name => "Gemini";
        public string SourceLabel => "unused";
        public Confidence Confidence => Confidence.Official;
        public Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct) =>
            throw new AssertFailedException("Provider fallback must not run.");
    }

    private sealed class MapConfig : IConfig
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string this[string key] { set => _values[key] = value; }
        public string Get(string key, string fallback = "") =>
            _values.TryGetValue(key, out var value) ? value : fallback;
        public string GetScoped(string instanceId, string key, string fallback = "") =>
            Get($"{instanceId}.{key}", fallback);
        public bool HasScoped(string instanceId, string key) =>
            _values.ContainsKey($"{instanceId}.{key}");
        public bool GetBool(string key, bool fallback = false) =>
            _values.TryGetValue(key, out var value) ? value == "true" : fallback;
    }
}
