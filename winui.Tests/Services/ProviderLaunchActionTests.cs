using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class ProviderLaunchActionTests
{
    [TestMethod]
    public void NativeProvidersDeclareLaunchBehaviorPerSourceWithoutModeInference()
    {
        var gemini = ProviderRegistry.Create("gemini").Sources;
        Assert.IsInstanceOfType<AppProviderLaunchAction>(
            gemini.Single(source => source.Mode == ProviderSourceMode.App).LaunchAction);
        Assert.IsInstanceOfType<CliProviderLaunchAction>(
            gemini.Single(source => source.Mode == ProviderSourceMode.Cli).LaunchAction);

        var kimi = ProviderRegistry.Create("kimi").Sources;
        Assert.IsInstanceOfType<AppProviderLaunchAction>(
            kimi.Single(source => source.Mode == ProviderSourceMode.App).LaunchAction);
        Assert.IsInstanceOfType<CliProviderLaunchAction>(
            kimi.Single(source => source.Mode == ProviderSourceMode.Cli).LaunchAction);
        Assert.IsInstanceOfType<WebProviderLaunchAction>(
            kimi.Single(source => source.Mode == ProviderSourceMode.Web).LaunchAction);

        var zai = ProviderRegistry.Create("zai").Sources;
        Assert.IsInstanceOfType<AppProviderLaunchAction>(
            zai.Single(source => source.Mode == ProviderSourceMode.Cli).LaunchAction);
        Assert.IsNull(zai.Single(source => source.Mode == ProviderSourceMode.Web).LaunchAction);

        Assert.IsInstanceOfType<WebProviderLaunchAction>(
            ProviderRegistry.Create("cursor").Sources.Single().LaunchAction);
    }

    [TestMethod]
    public void Resolver_ExplicitAppAndCliSelectionsOwnDifferentActions()
    {
        const string instanceId = "gemini-work";
        var provider = ProviderRegistry.Create("gemini");
        var config = new MapConfig
        {
            [$"{instanceId}.{ProviderSourceRunner.SourceConfigKey}"] = "app",
        };

        Assert.IsInstanceOfType<AppProviderLaunchAction>(
            ProviderRegistry.LaunchActionFor(provider, instanceId, config));

        config[$"{instanceId}.{ProviderSourceRunner.SourceConfigKey}"] = "cli";

        Assert.IsInstanceOfType<CliProviderLaunchAction>(
            ProviderRegistry.LaunchActionFor(provider, instanceId, config));
    }

    [TestMethod]
    public void Resolver_WithoutExplicitSelectionUsesTheDefaultDisplayedBySettings()
    {
        const string instanceId = "gemini-work";
        var provider = ProviderRegistry.Create("gemini");

        var action = ProviderRegistry.LaunchActionFor(
            provider,
            instanceId,
            new MapConfig());

        Assert.AreSame(
            provider.Sources[0],
            ProviderRegistry.ConfiguredOrDefaultSourceFor(
                provider.Sources,
                instanceId,
                new MapConfig()));
        Assert.IsInstanceOfType<AppProviderLaunchAction>(action);
    }

    [TestMethod]
    public void GeminiWithoutPersistedSelectionLaunchesAntigravityNotLastAutomaticFallback()
    {
        var executable = TempExecutable("Antigravity.exe");
        try
        {
            File.WriteAllBytes(executable, []);
            const string instanceId = "gemini-work";
            var config = new MapConfig
            {
                ["gemini_app_path"] = executable,
            };
            var provider = ProviderRegistry.Create("gemini");

            var action = ProviderRegistry.LaunchActionFor(provider, instanceId, config);
            var info = action?.GetInfo(instanceId, config);

            Assert.IsInstanceOfType<AppProviderLaunchAction>(action);
            Assert.IsNotNull(info);
            Assert.AreEqual("Antigravity", info.DisplayName);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void Resolver_SelectedNativeSourceWithoutActionDoesNotBorrowAnotherSourceAction()
    {
        const string instanceId = "zai-work";
        var provider = ProviderRegistry.Create("zai");
        var config = new MapConfig
        {
            [$"{instanceId}.{ProviderSourceRunner.SourceConfigKey}"] = "web",
        };

        var action = ProviderRegistry.LaunchActionFor(provider, instanceId, config);

        Assert.IsNull(action);
    }

    [TestMethod]
    public void Resolver_LegacyProviderRetainsDefaultEditorCompatibility()
    {
        var executable = TempExecutable("Editor.exe");
        try
        {
            File.WriteAllBytes(executable, []);
            var config = new MapConfig
            {
                [Catalog.DefaultLaunchEditorPathKey] = executable,
            };
            var provider = ProviderRegistry.Create("deepseek");

            var action = ProviderRegistry.LaunchActionFor(
                provider,
                "deepseek-work",
                config);
            var info = action?.GetInfo("deepseek-work", config);

            Assert.IsNotNull(info);
            Assert.AreEqual("Default editor", info.DisplayName);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    [DataRow("Antigravity.exe", "Antigravity")]
    [DataRow("Antigravity IDE.exe", "Antigravity IDE")]
    public void GeminiApp_ResolvedExecutableControlsLaunchName(
        string executableName,
        string expectedName)
    {
        var executable = TempExecutable(executableName);
        try
        {
            File.WriteAllBytes(executable, []);
            const string instanceId = "gemini-work";
            var config = new MapConfig
            {
                ["gemini_app_path"] = executable,
                [$"{instanceId}.{ProviderSourceRunner.SourceConfigKey}"] = "app",
            };
            var provider = ProviderRegistry.Create("gemini");
            var action = ProviderRegistry.LaunchActionFor(provider, instanceId, config);

            var info = action?.GetInfo(instanceId, config);

            Assert.IsNotNull(info);
            Assert.AreEqual(expectedName, info.DisplayName);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void GeminiCli_UsesScopedExecutableAndCliIdentity()
    {
        var executable = TempExecutable("gemini.exe");
        try
        {
            File.WriteAllBytes(executable, []);
            const string instanceId = "gemini-work";
            var config = new MapConfig
            {
                [$"{instanceId}.{ProviderSourceRunner.SourceConfigKey}"] = "cli",
                [$"{instanceId}.gemini_path"] = executable,
            };
            var provider = ProviderRegistry.Create("gemini");
            var resolvedAction = ProviderRegistry.LaunchActionFor(provider, instanceId, config);
            const string terminalIcon = @"C:\icons\WindowsTerminal.png";
            var action = new CliProviderLaunchAction("gemini", () => terminalIcon);

            var info = action?.GetInfo(instanceId, config);

            Assert.IsInstanceOfType<CliProviderLaunchAction>(resolvedAction);
            Assert.IsNotNull(info);
            Assert.AreEqual("Gemini CLI", info.DisplayName);
            Assert.AreEqual(terminalIcon, info.IconPath);
            Assert.IsFalse(info.DisplayName.Contains("Antigravity", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void ZCodeAppLaunch_UsesItsPerInstancePathEvenThoughSourceModeIsCli()
    {
        var executable = TempExecutable("ZCode.exe");
        try
        {
            File.WriteAllBytes(executable, []);
            const string instanceId = "zai-work";
            var config = new MapConfig
            {
                [$"{instanceId}.{ProviderSourceRunner.SourceConfigKey}"] = "cli",
                [$"{instanceId}.zai_app_path"] = executable,
            };
            var provider = ProviderRegistry.Create("zai");
            var action = ProviderRegistry.LaunchActionFor(provider, instanceId, config);

            var info = action?.GetInfo(instanceId, config);

            Assert.IsInstanceOfType<AppProviderLaunchAction>(action);
            Assert.IsNotNull(info);
            Assert.AreEqual("ZCode", info.DisplayName);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void WebAction_UsesConfiguredOrProviderDefaultWebsite()
    {
        const string instanceId = "kimi-work";
        var action = new WebProviderLaunchAction("kimi", "kimi_url");
        var configured = new MapConfig
        {
            [$"{instanceId}.kimi_url"] = "https://example.test/account",
        };

        Assert.AreEqual("https://example.test/account", action.ResolveUrl(instanceId, configured)?.TrimEnd('/'));
        Assert.IsNotNull(action.ResolveUrl(instanceId, new MapConfig()));
        Assert.IsTrue(WebProviderLaunchAction.BuildStartInfo("https://example.test").UseShellExecute);
    }

    private static string TempExecutable(string executableName)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"QuotaLensLaunch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, executableName);
    }

    private sealed class MapConfig : Dictionary<string, string>, IConfig
    {
        public string Get(string key, string fallback = "") =>
            TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            Get($"{instanceId}.{key}", fallback);

        public bool HasScoped(string instanceId, string key) =>
            ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) =>
            TryGetValue(key, out var value) ? value == "true" : fallback;
    }
}
