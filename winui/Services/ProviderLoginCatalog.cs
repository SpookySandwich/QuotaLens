namespace QuotaLens.Services;

/// <summary>
/// How a CLI-backed provider is signed into. Pure data — no I/O — so it can be
/// asserted on in tests without touching the filesystem or spawning anything.
/// </summary>
/// <param name="CliCommand">Bare binary name resolved against PATH (e.g. "claude", "kiro-cli").</param>
/// <param name="LoginArgs">
/// Arguments that start the interactive sign-in. May be EMPTY when the CLI signs in by
/// simply being launched (Gemini has no login verb — it opens a picker on start).
/// </param>
/// <param name="CliPathFieldKey">Per-instance config key holding a user-set CLI path.</param>
/// <param name="ProfileFieldKey">Config key appended as a profile argument (AWS SSO).</param>
/// <param name="InstallUrl">Where to get the CLI. Always a page — never a pipe-to-shell command.</param>
/// <param name="InteractiveHintKey">
/// I18n key for a follow-up instruction shown beside the terminal, for CLIs whose sign-in
/// is a slash-command typed inside their own REPL rather than an argv verb.
/// </param>
public sealed record ProviderLoginDescriptor(
    string ProviderType,
    string CliCommand,
    IReadOnlyList<string> LoginArgs,
    string? CliPathFieldKey = null,
    string? ProfileFieldKey = null,
    string? InstallUrl = null,
    string? InteractiveHintKey = null);

/// <summary>
/// The providers whose sign-in QuotaLens can actually start for the user.
///
/// Entries exist ONLY where the login invocation was verified — against the CLI's own
/// --help on a machine that has it, or against vendor documentation. Guessing a verb
/// here produces a button that opens a terminal and immediately errors, which is worse
/// than the honest message it replaced. Providers whose verb could not be established
/// (Doubao's arkcli) or which have no CLI sign-in at all (Antigravity, codex-lb,
/// JetBrains) are deliberately absent and keep their existing guidance.
/// </summary>
public static class ProviderLoginCatalog
{
    public static IReadOnlyDictionary<string, ProviderLoginDescriptor> Descriptors { get; } =
        new Dictionary<string, ProviderLoginDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            // Verified: `claude auth login` (claude --help lists the auth subcommand).
            ["claude"] = new(
                "claude",
                "claude",
                ["auth", "login"],
                CliPathFieldKey: "claude_path",
                InstallUrl: "https://docs.claude.com/en/docs/claude-code/setup"),

            // Verified: `codex login`. Resolves the .cmd shim, not the .ps1.
            ["codex"] = new(
                "codex",
                "codex",
                ["login"],
                CliPathFieldKey: "codex_path",
                InstallUrl: "https://developers.openai.com/codex/cli/"),

            // The Gemini CLI has NO login verb: launching it bare opens the Google
            // account picker. CodexBar's runner does the same (cd ~; "$binary").
            ["gemini"] = new(
                "gemini",
                "gemini",
                [],
                CliPathFieldKey: "gemini_path",
                InstallUrl: "https://github.com/google-gemini/gemini-cli"),

            // Verified: kiro-cli --help lists "login  Log in to Kiro".
            ["kiro"] = new(
                "kiro",
                "kiro-cli",
                ["login"],
                CliPathFieldKey: "kiro_cli_path",
                InstallUrl: "https://kiro.dev"),

            // Verified: grok --help lists "login  Sign in to Grok".
            ["grok"] = new(
                "grok",
                "grok",
                ["login"],
                CliPathFieldKey: "grok_path",
                InstallUrl: "https://x.ai/cli"),

            // Azure CLI: `az login` opens the browser consent flow itself.
            ["azureopenai"] = new(
                "azureopenai",
                "az",
                ["login"],
                InstallUrl: "https://learn.microsoft.com/cli/azure/install-azure-cli-windows"),

            // gcloud ADC login covers the whole Vertex flow in one command.
            ["vertexai"] = new(
                "vertexai",
                "gcloud",
                ["auth", "application-default", "login"],
                InstallUrl: "https://cloud.google.com/sdk/docs/install"),

            // AWS SSO is per-profile; the profile is appended when configured.
            ["bedrock"] = new(
                "bedrock",
                "aws",
                ["sso", "login"],
                ProfileFieldKey: "bedrock_profile",
                InstallUrl: "https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html"),

            // Qoder signs in with a slash command typed inside its own REPL, so the
            // terminal opens the CLI and the card tells the user what to type. Shipping
            // a bare `qodercli login` would be a guess — the docs page 404s.
            ["qoder"] = new(
                "qoder",
                "qodercli",
                [],
                CliPathFieldKey: "qoder_cli_path",
                InstallUrl: "https://qoder.com",
                InteractiveHintKey: "login.interactiveHint.qoder"),
        };

    public static bool TryGet(string providerType, out ProviderLoginDescriptor descriptor) =>
        Descriptors.TryGetValue(providerType, out descriptor!);
}
