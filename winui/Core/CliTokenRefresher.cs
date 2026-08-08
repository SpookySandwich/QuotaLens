using System.Diagnostics;

namespace QuotaLens.Core;

/// <summary>
/// Runs a CLI subcommand that makes the CLI refresh ITS OWN credential store, then
/// reports whether the observable credential state actually changed.
///
/// Three invariants, each learned the hard way:
///
/// NO PROMPT — every argv passed here must be a measured, non-prompt-bearing
/// subcommand. Sending a prompt would spend the very quota being measured, which is
/// exactly why the previous print-mode refresh was removed.
///
/// NO WRITES — this type contains no file-writing construct. The CLI rewrites its own
/// credential file; QuotaLens never does, and never takes the CLI's lock.
///
/// NEVER GATE ON EXIT CODE — measured on Kimi Code 0.28.1, `kimi login` exits
/// 0xC0000409 (a libuv teardown assert) AFTER succeeding, and exits 0 when it decides
/// to no-op. Trusting the exit code there reports failure precisely when it worked.
/// The only portable success signal is "the credential fingerprint changed".
/// </summary>
internal static class CliTokenRefresher
{
    internal enum RefreshOutcome
    {
        /// <summary>Credential changed — re-read it and retry the request once.</summary>
        Changed,

        /// <summary>Process ran but the credential is unchanged (already fresh, or declined).</summary>
        Unchanged,

        /// <summary>Binary missing, launch failed, or timed out. Not worth surfacing alone.</summary>
        CouldNotRun,
    }

    internal sealed record Request
    {
        /// <summary>Resolved by the CALLER from IConfig; this helper never reads config.</summary>
        public required string Binary { get; init; }

        /// <summary>Fixed argv ARRAY, never a joined string, so nothing can be re-parsed as a prompt.</summary>
        public required IReadOnlyList<string> Arguments { get; init; }

        public required TimeSpan Timeout { get; init; }

        /// <summary>Child-environment overlay (e.g. NO_BROWSER=true) applied on top of ours.</summary>
        public IReadOnlyDictionary<string, string>? Environment { get; init; }

        /// <summary>
        /// Run in a neutral empty directory. Required for any CLI that discovers project
        /// configuration from the working directory — `claude mcp list` health-checks and
        /// SPAWNS stdio MCP servers declared in a .mcp.json it finds there, which is a
        /// side effect a quota refresh must never cause.
        /// </summary>
        public bool UseNeutralWorkingDirectory { get; init; }
    }

    /// <summary>
    /// Runs the command and compares the credential fingerprint before and after.
    /// <paramref name="readFingerprint"/> should return something cheap and stable that
    /// changes exactly when the credential does (the access token itself works well);
    /// it must tolerate the file being briefly missing or partial, because these CLIs
    /// rewrite atomically via temp-file-then-rename.
    /// </summary>
    internal static async Task<RefreshOutcome> RefreshAsync(
        Request request,
        Func<string?> readFingerprint,
        CancellationToken ct)
    {
        var before = SafeFingerprint(readFingerprint);

        try
        {
            if (!await RunAsync(request, ct).ConfigureAwait(false))
                return RefreshOutcome.CouldNotRun;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return RefreshOutcome.CouldNotRun;
        }

        var after = SafeFingerprint(readFingerprint);
        return after is not null && !string.Equals(before, after, StringComparison.Ordinal)
            ? RefreshOutcome.Changed
            : RefreshOutcome.Unchanged;
    }

    private static string? SafeFingerprint(Func<string?> readFingerprint)
    {
        try
        {
            return readFingerprint();
        }
        catch
        {
            // Mid-rename read: treat as unknown rather than as a change.
            return null;
        }
    }

    /// <summary>Returns whether the process actually ran to completion (not whether it succeeded).</summary>
    private static async Task<bool> RunAsync(Request request, CancellationToken ct)
    {
        var startInfo = HiddenCliProcess.CreateStartInfo(request.Binary, request.Arguments);
        startInfo.RedirectStandardInput = true;

        if (request.UseNeutralWorkingDirectory)
            startInfo.WorkingDirectory = NeutralWorkingDirectory();

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
                startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return false;

        // Closing stdin turns any unexpected interactive prompt into an immediate exit
        // rather than a process that hangs until the timeout.
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return false;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

        // Exit code deliberately ignored — see the class remarks.
        return true;
    }

    /// <summary>
    /// An empty directory owned by QuotaLens, so a CLI that reads project config from
    /// the working directory finds nothing to act on.
    /// </summary>
    private static string NeutralWorkingDirectory()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuotaLens",
            "cli-neutral");
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            return Path.GetTempPath();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup after a timeout.
        }
    }
}

/// <summary>
/// The closed set of measured, non-prompt-bearing refresh commands. Centralized because
/// the argv IS the safety boundary: a stray prompt-bearing argument here silently starts
/// spending the quota the app exists to measure.
/// </summary>
internal static class CliRefreshCommands
{
    /// <summary>Measured: refreshes a 43h-expired token to 8h validity; usage API 401 -> 200.</summary>
    internal static readonly string[] Claude = ["mcp", "list"];

    /// <summary>Measured x5 on Kimi Code 0.28.1: refreshes silently, quota unchanged.</summary>
    internal static readonly string[] Kimi = ["login"];

    /// <summary>
    /// Measured on Gemini CLI 0.54.4: rotates an expired access token, then exits
    /// non-zero for a personal account (Google retired consumer Code Assist OAuth on
    /// 2026-06-18). The refresh still happens, which is why success is judged by the
    /// credential changing rather than by the exit code.
    /// </summary>
    internal static readonly string[] Gemini = ["--list-extensions"];
}
