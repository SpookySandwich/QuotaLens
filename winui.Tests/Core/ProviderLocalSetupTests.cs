using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ProviderLocalSetupTests
{
    [TestMethod]
    public void NeedsSetup_ForProviderWithoutProbe_ReturnsFalse()
    {
        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "claude-new",
            "claude",
            new FakeConfig(new Dictionary<string, string>()),
            fileExists: _ => false,
            commandExists: _ => false));
    }

    [TestMethod]
    public void NeedsSetup_ForConfiguredLocalProvider_ReturnsFalse()
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["qoder-new.qoder_cli_path"] = @"C:\Tools\qodercli.exe",
            ["kilo-new.kilo_key"] = "kilo-token",
        });

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "qoder-new",
            "qoder",
            config,
            fileExists: _ => false,
            commandExists: _ => false));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "kilo-new",
            "kilo",
            config,
            fileExists: _ => false,
            commandExists: _ => false));
    }

    [TestMethod]
    public void NeedsSetup_ForMissingLocalTool_ReturnsTrue()
    {
        var config = new FakeConfig(new Dictionary<string, string>());

        foreach (var providerType in new[] { "qoder", "grok", "kilo", "jetbrains", "codex", "gemini", "bedrock", "vertexai" })
        {
            Assert.IsTrue(ProviderLocalSetup.NeedsSetup(
                $"{providerType}-new",
                providerType,
                config,
                fileExists: _ => false,
                commandExists: _ => false),
                $"{providerType} should require setup when no configured, env, file, or command source exists.");
        }
    }

    [TestMethod]
    public void NeedsSetup_ForDetectedEnvironmentDefaultFileOrPathCommand_ReturnsFalse()
    {
        var config = new FakeConfig(new Dictionary<string, string>());

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "qoder-new",
            "qoder",
            config,
            fileExists: path => path.Contains("qodercli", StringComparison.OrdinalIgnoreCase),
            commandExists: _ => false));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "kiro-new",
            "kiro",
            config,
            fileExists: _ => false,
            commandExists: command => command.StartsWith("kiro-cli", StringComparison.OrdinalIgnoreCase)));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "grok-new",
            "grok",
            config,
            fileExists: _ => false,
            commandExists: _ => false,
            environmentValue: key => key == "GROK_CLI_PATH" ? @"C:\Tools\grok.exe" : null));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "jetbrains-new",
            "jetbrains",
            config,
            fileExists: path => path.Contains("AIAssistantQuotaManager2.xml", StringComparison.OrdinalIgnoreCase),
            commandExists: _ => false));
    }

    [TestMethod]
    public void NeedsSetup_ForConfiguredCredentialHomes_RequiresCredentialFile()
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["codex-new.codex_home"] = @"C:\Users\me\.codex",
            ["gemini-new.gemini_home"] = @"C:\Users\me",
            ["vertexai-new.vertexai_credentials_path"] = @"C:\Users\me\AppData\Roaming\gcloud\application_default_credentials.json",
        });

        Assert.IsTrue(ProviderLocalSetup.NeedsSetup(
            "codex-new",
            "codex",
            config,
            fileExists: _ => false,
            commandExists: _ => false));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "codex-new",
            "codex",
            config,
            fileExists: path => path.EndsWith(@"\.codex\auth.json", StringComparison.OrdinalIgnoreCase),
            commandExists: _ => false));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "gemini-new",
            "gemini",
            config,
            fileExists: path => path.EndsWith(@"\.gemini\oauth_creds.json", StringComparison.OrdinalIgnoreCase),
            commandExists: _ => false));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "vertexai-new",
            "vertexai",
            config,
            fileExists: path => path.EndsWith(@"\gcloud\application_default_credentials.json", StringComparison.OrdinalIgnoreCase),
            commandExists: _ => false));
    }

    [TestMethod]
    public void NeedsSetup_ForBedrockStaticCredentials_RequiresAccessKeyAndSecret()
    {
        var partialConfig = new FakeConfig(new Dictionary<string, string>
        {
            ["bedrock-new.bedrock_access_key_id"] = "AKIA...",
        });
        var completeConfig = new FakeConfig(new Dictionary<string, string>
        {
            ["bedrock-new.bedrock_access_key_id"] = "AKIA...",
            ["bedrock-new.bedrock_secret_access_key"] = "secret",
        });

        Assert.IsTrue(ProviderLocalSetup.NeedsSetup(
            "bedrock-new",
            "bedrock",
            partialConfig,
            fileExists: _ => false,
            commandExists: _ => false));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "bedrock-new",
            "bedrock",
            completeConfig,
            fileExists: _ => false,
            commandExists: _ => false));
    }

    [TestMethod]
    public void NeedsSetup_ForBedrockProfile_RequiresProfileAndAwsCli()
    {
        var config = new FakeConfig(new Dictionary<string, string>
        {
            ["bedrock-new.bedrock_profile"] = "work",
        });

        Assert.IsTrue(ProviderLocalSetup.NeedsSetup(
            "bedrock-new",
            "bedrock",
            config,
            fileExists: _ => false,
            commandExists: _ => false));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "bedrock-new",
            "bedrock",
            config,
            fileExists: _ => false,
            commandExists: command => command.Equals("aws", StringComparison.OrdinalIgnoreCase)));

        var cliConfig = new FakeConfig(new Dictionary<string, string>
        {
            ["bedrock-new.bedrock_profile"] = "work",
            ["bedrock-new.bedrock_aws_cli_path"] = @"C:\Program Files\Amazon\AWSCLIV2\aws.exe",
        });

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "bedrock-new",
            "bedrock",
            cliConfig,
            fileExists: _ => false,
            commandExists: _ => false));
    }

    [TestMethod]
    public void NeedsSetup_ForEnvironmentCredentialSources_UsesConfiguredSemantics()
    {
        var config = new FakeConfig(new Dictionary<string, string>());

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "bedrock-new",
            "bedrock",
            config,
            fileExists: _ => false,
            commandExists: _ => false,
            environmentValue: key => key is "AWS_ACCESS_KEY_ID" or "AWS_SECRET_ACCESS_KEY" ? key : null));

        Assert.IsTrue(ProviderLocalSetup.NeedsSetup(
            "bedrock-new",
            "bedrock",
            config,
            fileExists: _ => false,
            commandExists: _ => false,
            environmentValue: key => key == "AWS_ACCESS_KEY_ID" ? "AKIA..." : null));

        Assert.IsFalse(ProviderLocalSetup.NeedsSetup(
            "codex-new",
            "codex",
            config,
            fileExists: path => path.EndsWith(@"\CodexHome\auth.json", StringComparison.OrdinalIgnoreCase),
            commandExists: _ => false,
            environmentValue: key => key == "CODEX_HOME" ? @"C:\CodexHome" : null));
    }

    [TestMethod]
    public void FilePathExists_WithGlobbedDirectory_FindsMatchingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuotaLens.Tests", Guid.NewGuid().ToString("N"));
        var quotaFile = Path.Combine(root, "JetBrains", "WebStorm2025.2", "options", "AIAssistantQuotaManager2.xml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(quotaFile)!);
            File.WriteAllText(quotaFile, "<application />");

            Assert.IsTrue(ProviderLocalSetup.FilePathExists(
                Path.Combine(root, "JetBrains", "*", "options", "AIAssistantQuotaManager2.xml"),
                File.Exists));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeConfig(IReadOnlyDictionary<string, string> values) : IConfig
    {
        public string Get(string key, string fallback = "") =>
            values.TryGetValue(key, out var value) ? value : fallback;

        public string GetScoped(string instanceId, string key, string fallback = "") =>
            values.TryGetValue($"{instanceId}.{key}", out var value) ? value : fallback;

        public bool HasScoped(string instanceId, string key) =>
            values.ContainsKey($"{instanceId}.{key}");

        public bool GetBool(string key, bool fallback = false) =>
            values.TryGetValue(key, out var value)
                ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                : fallback;
    }
}
