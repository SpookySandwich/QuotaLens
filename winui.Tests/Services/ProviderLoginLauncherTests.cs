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
    [DataRow("kimi")]
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
    public void Descriptors_EveryLoginHasAVisibleExecutablePathField()
    {
        foreach (var (providerType, descriptor) in ProviderLoginCatalog.Descriptors)
        {
            Assert.IsTrue(Catalog.Fields.TryGetValue(providerType, out var fields), providerType);
            var pathField = fields.SingleOrDefault(field =>
                string.Equals(field.Key, descriptor.CliPathFieldKey, StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(pathField, $"{providerType} must expose {descriptor.CliPathFieldKey} before sign-in.");
            Assert.IsTrue(pathField.IsFilePath, $"{providerType}.{descriptor.CliPathFieldKey} must validate as a path.");
        }
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
    public void BuildStartInfo_LaunchesAVisiblePowerShellThatClosesOnSuccess()
    {
        var startInfo = TerminalLauncher.BuildStartInfo("ZW5jb2RlZA==");

        // Visible: signing in is interactive, and UseShellExecute gives it its own console
        // instead of hiding inside whatever console launched QuotaLens.
        Assert.IsTrue(startInfo.UseShellExecute);
        Assert.IsFalse(startInfo.CreateNoWindow);
        StringAssert.Contains(startInfo.FileName, "powershell");
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "-EncodedCommand");
        // -NoExit is gone: the script itself exits on success and Read-Hosts on failure.
        CollectionAssert.DoesNotContain(startInfo.ArgumentList.ToArray(), "-NoExit");
        // -NonInteractive would break the very thing this window exists for.
        CollectionAssert.DoesNotContain(startInfo.ArgumentList.ToArray(), "-NonInteractive");
    }

    [TestMethod]
    public void EncodeLoginScript_AutoClosesOnSuccessAndKeepsOpenOnFailure()
    {
        var encoded = TerminalLauncher.EncodeLoginScript("grok", ["login"], "grok");
        var script = System.Text.Encoding.Unicode.GetString(System.Convert.FromBase64String(encoded));

        StringAssert.Contains(script, "exit 0");               // auto-close on success
        StringAssert.Contains(script, "Read-Host");            // keep open on failure
        StringAssert.Contains(script, "Start-Sleep -Seconds 2");
    }

    [TestMethod]
    public void EncodeCliScript_InvokesBareBinaryAndKeepsOnlyFailuresOpen()
    {
        const string path = @"C:\Program Files\A & B\cli's.exe";

        var encoded = TerminalLauncher.EncodeCliScript(path, "gemini");
        var script = System.Text.Encoding.Unicode.GetString(System.Convert.FromBase64String(encoded));

        StringAssert.Contains(script, path.Replace("'", "''"));
        StringAssert.Contains(script, "& $binary");
        Assert.IsFalse(script.Contains("$cliArguments", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("login", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(script, "exit 0");
        StringAssert.Contains(script, "Read-Host");
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

    [TestMethod]
    public void TryResolveCli_OverlayUsesUnsavedExecutablePath()
    {
        var descriptor = ProviderLoginCatalog.Descriptors["gemini"];
        var draftPath = @"C:\Draft Tools\gemini.exe";
        var config = new OverlayConfig(
            new EmptyConfig(),
            "gemini",
            scopedValues: new Dictionary<string, string>
            {
                [descriptor.CliPathFieldKey] = draftPath,
            });

        var resolved = TerminalLauncher.TryResolveCli(
            descriptor,
            "gemini",
            config,
            out var path,
            resolve: candidate => candidate,
            fileExists: candidate => string.Equals(candidate, draftPath, StringComparison.Ordinal));

        Assert.IsTrue(resolved);
        Assert.AreEqual(draftPath, path);
    }

    [TestMethod]
    public void ResolveTerminalIconExecutable_PrefersWindowsTerminalAppAlias()
    {
        const string localAppData = @"C:\Users\test\AppData\Local";
        var expected = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");

        var resolved = TerminalLauncher.ResolveTerminalIconExecutable(
            localAppData,
            resolve: _ => @"C:\Other\wt.exe",
            fileExists: path => path == expected);

        Assert.AreEqual(expected, resolved);
    }

    [TestMethod]
    public void ProviderException_StandardPrefixesInferBehavioralKinds()
    {
        Assert.AreEqual(
            ProviderErrorKind.AuthenticationRequired,
            new ProviderException("Login required: expired").Kind);
        Assert.AreEqual(
            ProviderErrorKind.Misconfigured,
            new ProviderException("Not configured: missing path").Kind);
        Assert.AreEqual(
            ProviderErrorKind.Unknown,
            new ProviderException("Network error: offline").Kind);
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
