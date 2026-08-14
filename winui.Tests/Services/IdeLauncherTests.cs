using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class IdeLauncherTests
{
    [TestMethod]
    public void ResolveLaunchPath_WithCustomPath_UsesConfiguredGuiPath()
    {
        var target = new ProviderLaunchTarget(
            "Qoder",
            "qoder_app_path",
            new[] { @"%ProgramFiles%\QoderWork\QoderWork\QoderWork.exe" });

        var path = IdeLauncher.ResolveLaunchPath(
            "qoder",
            target,
            @"C:\Apps\QoderWork.exe",
            fileExists: candidate => candidate == @"C:\Apps\QoderWork.exe");

        Assert.AreEqual(@"C:\Apps\QoderWork.exe", path);
    }

    [TestMethod]
    public void ResolveLaunchPath_UsesFirstInstalledDefaultGuiCandidate()
    {
        var target = new ProviderLaunchTarget(
            "Qoder",
            "qoder_app_path",
            new[]
            {
                @"C:\missing\Qoder.exe",
                @"C:\Apps\Qoder\Qoder.exe",
            });

        var path = IdeLauncher.ResolveLaunchPath(
            "qoder",
            target,
            customPath: null,
            fileExists: candidate => candidate == @"C:\Apps\Qoder\Qoder.exe");

        Assert.AreEqual(@"C:\Apps\Qoder\Qoder.exe", path);
    }

    [TestMethod]
    public void ResolveLaunchPath_WithCustomDirectory_UsesKnownExecutableInsideDirectory()
    {
        var target = new ProviderLaunchTarget(
            "Qoder",
            "qoder_app_path",
            Array.Empty<string>(),
            new[] { "QoderWork.exe", "Qoder.exe" });

        var path = IdeLauncher.ResolveLaunchPath(
            "qoder",
            target,
            customPath: @"C:\Program Files\QoderWork\QoderWork",
            fileExists: candidate => candidate == @"C:\Program Files\QoderWork\QoderWork\QoderWork.exe",
            directoryExists: candidate => candidate == @"C:\Program Files\QoderWork\QoderWork");

        Assert.AreEqual(@"C:\Program Files\QoderWork\QoderWork\QoderWork.exe", path);
    }

    [TestMethod]
    public void QoderLaunchTarget_PointsAtGuiAppNotCli()
    {
        var target = Catalog.LaunchTargets["qoder"];

        StringAssert.Contains(target.DefaultPaths[0], "QoderWork");
        Assert.IsFalse(target.DefaultPaths.Any(path => path.Contains("qodercli", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(target.DirectoryExecutableNames.Contains("QoderWork.exe"));
        Assert.AreEqual("qoder_app_path", target.ConfigKey);
    }

    [TestMethod]
    public void LaunchTargetFor_WithoutDefaultEditor_ReturnsOnlyBuiltInLaunchTargets()
    {
        var config = new FakeConfig();

        Assert.IsNotNull(Catalog.LaunchTargetFor("claude", config));
        Assert.IsNotNull(Catalog.LaunchTargetFor("codex-lb", config));
        Assert.IsNotNull(Catalog.LaunchTargetFor("codex", config));
        Assert.IsNotNull(Catalog.LaunchTargetFor("antigravity", config));
        Assert.IsNotNull(Catalog.LaunchTargetFor("kiro", config));
        Assert.IsNotNull(Catalog.LaunchTargetFor("qoder", config));
        Assert.IsNull(Catalog.LaunchTargetFor("deepseek", config));
    }

    [TestMethod]
    public void CodexLaunchTarget_PointsAtCodexGuiAppNotCodexLbOrCodexBar()
    {
        var target = Catalog.LaunchTargets["codex"];

        Assert.IsTrue(target.DefaultPaths.Any(path => path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(target.DefaultPaths.Any(path => path.Contains("CodexLB", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(target.DefaultPaths.Any(path => path.Contains("CodexBar", StringComparison.OrdinalIgnoreCase)));
        // The Codex desktop app was rebranded to ChatGPT; ChatGPT.exe is the primary entry point.
        Assert.AreEqual("ChatGPT.exe", target.DirectoryExecutableNames[0]);
        CollectionAssert.Contains(target.DirectoryExecutableNames, "Codex.exe");
        Assert.AreEqual("codex_app_path", target.ConfigKey);
    }

    [TestMethod]
    public void CodexLbLaunchTarget_UsesSeparateCodexGuiPathSetting()
    {
        var target = Catalog.LaunchTargets["codex-lb"];

        Assert.AreEqual("codex_lb_app_path", target.ConfigKey);
        Assert.AreEqual("ChatGPT.exe", target.DirectoryExecutableNames[0]);
        CollectionAssert.Contains(target.DirectoryExecutableNames, "Codex.exe");
    }

    [TestMethod]
    public void ClaudeLaunchTarget_UsesPackagedClaudeGuiApp()
    {
        var target = Catalog.LaunchTargets["claude"];

        Assert.IsTrue(target.DefaultPaths.Any(path => path.Contains(@"WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.Contains(target.DirectoryExecutableNames, "claude.exe");
    }

    [TestMethod]
    public void AntigravityLaunchTarget_IsBuiltInAndPrefersIdeApp()
    {
        var target = Catalog.LaunchTargets["antigravity"];

        StringAssert.Contains(target.DefaultPaths[0], "Antigravity IDE");
        CollectionAssert.Contains(target.DirectoryExecutableNames, "Antigravity IDE.exe");
        Assert.AreEqual("antigravity_path", target.ConfigKey);
    }

    [TestMethod]
    public void KiroLaunchTarget_IsBuiltInAndPointsAtGuiApp()
    {
        var target = Catalog.LaunchTargets["kiro"];

        StringAssert.Contains(target.DefaultPaths[0], @"Programs\Kiro\Kiro.exe");
        CollectionAssert.Contains(target.DirectoryExecutableNames, "Kiro.exe");
        Assert.AreEqual("kiro_app_path", target.ConfigKey);
        Assert.IsFalse(target.DefaultPaths.Any(path => path.Contains("kiro-cli", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ResolveLaunchPath_WithVersionedPackageWildcard_UsesNewestMatchingGuiExecutable()
    {
        var target = new ProviderLaunchTarget(
            "Codex",
            "codex_lb_app_path",
            new[] { @"C:\Program Files\WindowsApps\OpenAI.Codex_*\app\Codex.exe" });
        var oldPackage = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0";
        var newPackage = @"C:\Program Files\WindowsApps\OpenAI.Codex_2.0.0.0_x64__2p2nqsd0c76g0";
        var expected = Path.Combine(newPackage, "app", "Codex.exe");

        var path = IdeLauncher.ResolveLaunchPath(
            "codex-lb",
            target,
            customPath: null,
            fileExists: candidate => candidate == expected,
            directoryExists: candidate => candidate is @"C:\" or @"C:\Program Files" or @"C:\Program Files\WindowsApps",
            enumerateDirectories: (directory, pattern) => directory == @"C:\Program Files\WindowsApps" && pattern == "OpenAI.Codex_*"
                ? new[] { newPackage, oldPackage }
                : Array.Empty<string>());

        Assert.AreEqual(expected, path);
    }

    [TestMethod]
    public void ResolveLaunchPath_WithPackagedAppMetadata_UsesPackageInstallLocationBeforeFilesystemFallback()
    {
        var target = new ProviderLaunchTarget(
            "Codex",
            "codex_lb_app_path",
            new[] { @"C:\fallback\Codex.exe" },
            new[] { "Codex.exe" },
            "OpenAI.Codex_2p2nqsd0c76g0",
            @"app\Codex.exe");
        var expected = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.601.1994.0_x64__2p2nqsd0c76g0\app\Codex.exe";

        var path = IdeLauncher.ResolveLaunchPath(
            "codex-lb",
            target,
            customPath: null,
            fileExists: candidate => candidate == expected || candidate == @"C:\fallback\Codex.exe",
            directoryExists: _ => false,
            enumerateDirectories: (_, _) => Array.Empty<string>(),
            packageInstallLocation: packageFamilyName => packageFamilyName == "OpenAI.Codex_2p2nqsd0c76g0"
                ? @"C:\Program Files\WindowsApps\OpenAI.Codex_26.601.1994.0_x64__2p2nqsd0c76g0"
                : null);

        Assert.AreEqual(expected, path);
    }

    [TestMethod]
    public void LaunchTargetFor_WithDefaultEditor_ReturnsFallbackForOtherProviders()
    {
        var config = new FakeConfig
        {
            [Catalog.DefaultLaunchEditorPathKey] = @"C:\Apps\Editor.exe",
        };

        var target = Catalog.LaunchTargetFor("mimo", config);

        Assert.IsNotNull(target);
        Assert.AreEqual(Catalog.DefaultLaunchEditorPathKey, target!.ConfigKey);
    }

    [TestMethod]
    public void TryResolveLaunchPath_WhenDefaultPathMissing_ReturnsFalse()
    {
        var target = new ProviderLaunchTarget(
            "Qoder",
            "qoder_app_path",
            new[] { @"C:\missing\Qoder.exe" });

        var resolved = IdeLauncher.TryResolveLaunchPath(
            "qoder",
            target,
            customPath: null,
            out var path,
            fileExists: _ => false,
            directoryExists: _ => false);

        Assert.IsFalse(resolved);
        Assert.AreEqual("", path);
    }

    [TestMethod]
    public void TryResolveLaunchPath_WhenConfiguredPathMissing_ReturnsFalse()
    {
        var target = new ProviderLaunchTarget(
            "Qoder",
            "qoder_app_path",
            new[] { @"C:\fallback\Qoder.exe" });

        var resolved = IdeLauncher.TryResolveLaunchPath(
            "qoder",
            target,
            customPath: @"C:\missing\Qoder.exe",
            out var path,
            fileExists: _ => false,
            directoryExists: _ => false);

        Assert.IsFalse(resolved);
        Assert.AreEqual("", path);
    }

    private sealed class FakeConfig : IConfig
    {
        private readonly Dictionary<string, string> _values = new();

        public string this[string key]
        {
            set => _values[key] = value;
        }

        public string Get(string key, string fallback = "") =>
            _values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            Get(key, fallback);

        public bool HasScoped(string instanceId, string key) =>
            _values.ContainsKey($"{instanceId}.{key}") || _values.ContainsKey(key);

        public bool GetBool(string key, bool fallback = false) =>
            _values.TryGetValue(key, out var value) ? value == "true" : fallback;
    }
}
