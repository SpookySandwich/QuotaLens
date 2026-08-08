using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class ProviderLoginLauncherTests
{
    /// <summary>
    /// The regression this whole feature exists for: adding Gemini produced a card that
    /// said "Login required … Run `gemini` and sign in with Google" and rendered NO button,
    /// because the launcher was hardcoded to Claude. Every CLI-backed provider that tells
    /// the user to run something must be able to run it for them.
    /// </summary>
    [TestMethod]
    [DataRow("gemini")]
    [DataRow("claude")]
    [DataRow("codex")]
    [DataRow("kiro")]
    [DataRow("grok")]
    [DataRow("azureopenai")]
    [DataRow("vertexai")]
    [DataRow("bedrock")]
    [DataRow("qoder")]
    public void IsSupported_ForCliBackedProviders_OffersAnInAppLogin(string providerType)
    {
        Assert.IsTrue(
            ProviderLoginLauncher.IsSupported(providerType),
            $"{providerType} tells the user to sign in via a CLI, so it must offer a login action.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(ProviderLoginLauncher.CliCommandFor(providerType)));
    }

    [TestMethod]
    public void IsSupported_ForProvidersWithoutACliLogin_ReturnsFalse()
    {
        // Browser-login providers are served by WebLoginService, and these three have no
        // CLI sign-in at all — offering one would open a terminal that cannot help.
        foreach (var providerType in new[] { "cursor", "antigravity", "codex-lb", "jetbrains" })
            Assert.IsFalse(ProviderLoginLauncher.IsSupported(providerType), providerType);
    }

    [TestMethod]
    public void Descriptors_NeverGuessALoginVerb()
    {
        // A wrong verb opens a terminal that immediately errors, which is worse than the
        // honest message it replaced. Doubao's arkcli verb could not be established, so it
        // must stay out until someone verifies it.
        Assert.IsFalse(ProviderLoginCatalog.Descriptors.ContainsKey("doubao"));

        // Gemini and Qoder genuinely have no argv login verb; they sign in on launch or
        // via a slash command, so an empty argument list is correct rather than an omission.
        Assert.AreEqual(0, ProviderLoginCatalog.Descriptors["gemini"].LoginArgs.Count);
        Assert.AreEqual(0, ProviderLoginCatalog.Descriptors["qoder"].LoginArgs.Count);
        Assert.AreEqual(
            "login.interactiveHint.qoder",
            ProviderLoginCatalog.Descriptors["qoder"].InteractiveHintKey,
            "A CLI with no argv verb must tell the user what to type, or the terminal is a dead end too.");
    }

    [TestMethod]
    public void EveryCliBackedProvider_HasSomethingToDoWhenTheCliIsMissing()
    {
        // The bug this guards: clicking "Login with Gemini" did nothing at all, because
        // the CLI was not installed and the launcher just returned CliMissing. A button
        // that silently no-ops is the same dead end it was meant to remove, so every
        // CLI-backed provider must have an install page to fall back to.
        foreach (var (providerType, descriptor) in ProviderLoginCatalog.Descriptors)
        {
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(descriptor.InstallUrl),
                $"{providerType} offers a login button, so it needs an install page for when the CLI is absent.");
        }
    }

    [TestMethod]
    public void Descriptors_InstallUrlsAreWebPagesNotShellPipelines()
    {
        foreach (var (providerType, descriptor) in ProviderLoginCatalog.Descriptors)
        {
            if (descriptor.InstallUrl is null)
                continue;

            StringAssert.StartsWith(descriptor.InstallUrl, "https://", $"{providerType} install URL");
            // Never surface a curl|bash: it is unsafe advice and does not work on Windows.
            Assert.IsFalse(descriptor.InstallUrl.Contains('|'), $"{providerType} install URL");
        }
    }

    [TestMethod]
    public void EncodeLoginScript_SurvivesPathsWithShellMetacharacters()
    {
        // The reason the payload is base64 rather than a cmd string: this path breaks
        // `cmd /k "..."` outright.
        const string nastyPath = @"C:\Program Files\A & B; ""quoted""\cli's.exe";

        var encoded = TerminalLauncher.EncodeLoginScript(nastyPath, ["auth", "login"], "claude");
        var script = System.Text.Encoding.Unicode.GetString(System.Convert.FromBase64String(encoded));

        // The path survives byte-identical, with only PowerShell's own '' quote escape.
        StringAssert.Contains(script, nastyPath.Replace("'", "''"));
        StringAssert.Contains(script, "'auth', 'login'");
        StringAssert.Contains(script, "& $binary @cliArguments");
    }

    [TestMethod]
    public void EncodeLoginScript_WithNoLoginVerb_InvokesTheCliBare()
    {
        var encoded = TerminalLauncher.EncodeLoginScript("gemini", [], "gemini");
        var script = System.Text.Encoding.Unicode.GetString(System.Convert.FromBase64String(encoded));

        Assert.IsTrue(script.Contains("$cliArguments = @()"), "Gemini signs in by being launched, with no verb.");
    }

    [TestMethod]
    public void BuildStartInfo_UsesAVisibleShellAndKeepsTheWindowOpen()
    {
        var startInfo = TerminalLauncher.BuildStartInfo(null, "ZW5jb2RlZA==", "claude");

        // Visible: signing in is interactive, and UseShellExecute gives it its own console
        // instead of hiding inside whatever console launched QuotaLens.
        Assert.IsTrue(startInfo.UseShellExecute);
        Assert.IsFalse(startInfo.CreateNoWindow);
        StringAssert.Contains(startInfo.FileName, "powershell");
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "-NoExit");
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "-EncodedCommand");
        // -NonInteractive would break the very thing this window exists for.
        CollectionAssert.DoesNotContain(startInfo.ArgumentList.ToArray(), "-NonInteractive");
    }

    [TestMethod]
    public void BuildStartInfo_ForWindowsTerminal_TerminatesItsOwnOptions()
    {
        var startInfo = TerminalLauncher.BuildStartInfo(@"C:\wt.exe", "ZW5jb2RlZA==", "gemini");
        var arguments = startInfo.ArgumentList.ToArray();

        // Without the -- terminator, wt parses -NoLogo as one of its own options.
        var terminator = System.Array.IndexOf(arguments, "--");
        Assert.IsTrue(terminator > 0, "wt invocation must terminate its options with --");
        Assert.IsTrue(System.Array.IndexOf(arguments, "-EncodedCommand") > terminator);
    }

    [TestMethod]
    public void TryResolveCli_TreatsAnUnresolvedBareNameAsMissing()
    {
        // HiddenCliProcess.ResolveBinary echoes the bare name back when nothing matches,
        // so a non-empty result is NOT success — the existence check is load-bearing.
        var descriptor = ProviderLoginCatalog.Descriptors["gemini"];

        var resolved = TerminalLauncher.TryResolveCli(
            descriptor, "gemini", new EmptyConfig(), out _,
            resolve: name => name,
            fileExists: _ => false);

        Assert.IsFalse(resolved);
    }

    [TestMethod]
    public void LoginArguments_AppendsTheConfiguredAwsProfile()
    {
        var descriptor = ProviderLoginCatalog.Descriptors["bedrock"];
        var config = new ScopedConfig("bedrock", "bedrock_profile", "my-sso");

        var arguments = TerminalLauncher.LoginArguments(descriptor, "bedrock", config);

        CollectionAssert.AreEqual(new[] { "sso", "login", "--profile", "my-sso" }, arguments.ToArray());
    }

    private sealed class EmptyConfig : IConfig
    {
        public string Get(string key, string fallback = "") => fallback;
        public string GetScoped(string instanceId, string key, string fallback = "") => fallback;
        public bool HasScoped(string instanceId, string key) => false;
        public bool GetBool(string key, bool fallback = false) => fallback;
    }

    private sealed class ScopedConfig(string instanceId, string key, string value) : IConfig
    {
        public string Get(string k, string fallback = "") => fallback;

        public string GetScoped(string id, string k, string fallback = "") =>
            id == instanceId && k == key ? value : fallback;

        public bool HasScoped(string id, string k) => id == instanceId && k == key;
        public bool GetBool(string k, bool fallback = false) => fallback;
    }
}
