using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>
/// Kiro provider. Runs the documented non-interactive `/usage` command and parses
/// its read-only billing/credit output. Unknown output fails closed instead of
/// being rendered as zero usage.
/// </summary>
public sealed class KiroProvider : IProvider
{
    public string Type => "kiro";
    public string Name => "Kiro";
    public string SourceLabel => "kiro-cli /usage";
    public Confidence Confidence => Confidence.Official;

    // ANSI strip: CSI sequences (ESC [ ... letter) and OSC sequences (ESC ] ... BEL).
    // Mirrors Rust: \x1b\[[0-9;?]*[A-Za-z]|\x1b\].*?\x07
    private static readonly Regex AnsiRe =
        new(@"\x1b\[[0-9;?]*[A-Za-z]|\x1b\].*?\x07", RegexOptions.Compiled);

    // Credits.*?((\d+\.?\d*) of (\d+) covered) -> group1 = used, group2 = total.
    private static readonly Regex CreditsRe =
        new(
            @"Credits.*?\((\d+\.?\d*)\s+of\s+(\d+)\s+covered",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Usage bar followed by a percent: █+ (\d+)%
    private static readonly Regex PercentRe =
        new(@"█+\s*(\d+)%", RegexOptions.Compiled);

    // resets on YYYY-MM-DD or MM/DD
    private static readonly Regex ResetRe =
        new(@"resets on\s+(\d{4}-\d{2}-\d{2}|\d{2}/\d{2})", RegexOptions.Compiled);

    // | KIRO <word>
    private static readonly Regex PlanRe =
        new(@"\|[ \t]*(KIRO[ \t]+\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NewPlanRe =
        new(@"Plan:[ \t]*([^\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EstimatedPlanRe =
        new(@"Estimated Usage[ \t]*\|[^\r\n|]*\|[ \t]*([^\r\n|]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BonusCreditsRe =
        new(@"Bonus credits:\s*(\d+\.?\d*)\s*/\s*(\d+\.?\d*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BonusExpiryRe =
        new(@"expires in\s+(\d+)\s+days?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OverageCreditsRe =
        new(@"Credits used:\s*(\d+\.?\d*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OverageCostRe =
        new(@"Est\.\s*cost:\s*\$?(\d+\.?\d*)\s*USD", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ContextWindowRe =
        new(@"Context window:\s*(\d+\.?\d*)%\s+used", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<ProviderSnapshot> FetchAsync(string instanceId, IConfig config, CancellationToken ct)
    {
        // Binary resolution, mirroring the Rust:
        //   config "kiro_cli_path" (non-empty) -> env KIRO_CLI_PATH -> default path.
        var binary = ProviderConfig.Resolve(instanceId, config, "kiro", "kiro_cli_path")
            ?? DefaultKiroCliPath();

        var usage = await RunCommandAsync(binary, "/usage", TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        var combined = $"{usage.Stdout}\n{usage.Stderr}";
        if (usage.ExitCode != 0)
        {
            throw new ProviderException(
                $"Not available: kiro-cli /usage failed with exit code {usage.ExitCode}: " +
                ProviderConfig.ResponseSummary(AnsiRe.Replace(combined, "")));
        }

        string? contextOutput = null;
        try
        {
            var context = await RunCommandAsync(binary, "/context", TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            if (context.ExitCode == 0)
                contextOutput = string.IsNullOrWhiteSpace(context.Stdout) ? context.Stderr : context.Stdout;
        }
        catch (ProviderException)
        {
            // Context detail is optional; a working /usage result remains authoritative.
        }

        return ParseUsage(instanceId, combined, DateTimeOffset.UtcNow, contextOutput);
    }

    internal static ProcessStartInfo CreateStartInfo(string binary) => CreateStartInfo(binary, "/usage");

    internal static ProcessStartInfo CreateStartInfo(string binary, string command)
    {
        // Shared launch path: resolves .cmd/.ps1 shims instead of a bare CreateProcess.
        var startInfo = HiddenCliProcess.CreateStartInfo(binary, new[] { "chat", "--no-interactive", command });
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        return startInfo;
    }

    internal static ProviderSnapshot ParseUsage(string instanceId, string output, DateTimeOffset now)
        => ParseUsage(instanceId, output, now, contextOutput: null);

    internal static ProviderSnapshot ParseUsage(
        string instanceId,
        string output,
        DateTimeOffset now,
        string? contextOutput)
    {
        var stripped = AnsiRe.Replace(output, "").Trim();
        if (stripped.Length == 0)
            throw new ProviderException("Parse error: Empty output from kiro-cli /usage.");

        var lower = stripped.ToLowerInvariant();
        if (lower.Contains("not logged in", StringComparison.Ordinal)
            || lower.Contains("login required", StringComparison.Ordinal)
            || lower.Contains("kiro-cli login", StringComparison.Ordinal)
            || lower.Contains("oauth error", StringComparison.Ordinal))
        {
            throw new ProviderException("Login required: Run 'kiro-cli login' first.");
        }

        var planName = ParsePlanName(stripped);
        var credits = ParsePair(CreditsRe.Match(stripped));
        var percent = ParseFirstDouble(PercentRe.Match(stripped));
        if (percent is null && credits is { Total: > 0 })
            percent = credits.Value.Used / credits.Value.Total * 100;

        var managed = lower.Contains("managed by admin", StringComparison.Ordinal)
            || lower.Contains("managed by organization", StringComparison.Ordinal);
        if (percent is null && credits is null)
        {
            throw managed
                ? new ProviderException(
                    $"Not available: {planName} usage is managed by the organization and the CLI did not expose credit totals.",
                    ProviderErrorKind.Unsupported)
                : new ProviderException("Parse error: No recognizable usage values were found in kiro-cli /usage output.");
        }

        var resetMatch = ResetRe.Match(stripped);
        var reset = resetMatch.Success ? NormalizeResetDate(resetMatch.Groups[1].Value, now) : null;
        var bonus = ParsePair(BonusCreditsRe.Match(stripped));
        var expiryDays = ParseFirstInt(BonusExpiryRe.Match(stripped));
        var overageCredits = ParseFirstDouble(OverageCreditsRe.Match(stripped));
        var overageCost = ParseFirstDouble(OverageCostRe.Match(stripped));
        var overageText = overageCredits is null && overageCost is null
            ? null
            : $"Overage: {overageCredits?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?"} credits" +
              (overageCost is null ? "" : $" · ${overageCost.Value.ToString("0.00", CultureInfo.InvariantCulture)} USD");

        var total = credits?.Total;
        var used = credits?.Used;
        var description = used is null || total is null
            ? overageText
            : $"{used.Value.ToString("0.##", CultureInfo.InvariantCulture)} / " +
              $"{total.Value.ToString("0.##", CultureInfo.InvariantCulture)} credits" +
              (overageText is null ? "" : $" · {overageText}");
        var bonusReset = expiryDays is null ? null : now.AddDays(expiryDays.Value).ToString("O", CultureInfo.InvariantCulture);

        return new ProviderSnapshot
        {
            ProviderId = instanceId,
            Name = $"Kiro · {DisplayPlanName(planName)}",
            PlanName = ProviderSnapshotIdentity.NormalizePlanName("Kiro", DisplayPlanName(planName)),
            Primary = new RateWindow
            {
                Label = "Monthly credits",
                UsedPercent = Quota.ClampPercent(percent ?? 0),
                ResetsAt = reset,
                DetailText = description,
            },
            Secondary = bonus is null || bonus.Value.Total <= 0
                ? null
                : new RateWindow
                {
                    Label = "Bonus credits",
                    UsedPercent = Quota.ClampPercent(bonus.Value.Used / bonus.Value.Total * 100),
                    ResetsAt = bonusReset,
                    DetailText = $"{bonus.Value.Used.ToString("0.##", CultureInfo.InvariantCulture)} / " +
                        $"{bonus.Value.Total.ToString("0.##", CultureInfo.InvariantCulture)} credits",
                },
            Accounts = new List<AccountInfo>
            {
                new()
                {
                    Plan = DisplayPlanName(planName),
                    UsedPercent = Quota.ClampPercent(percent ?? 0),
                    CreditsUsed = used,
                    CreditsTotal = total,
                },
            },
            AdditionalWindows = ParseContextWindows(contextOutput),
            SourceLabel = "kiro-cli /usage (upstream-compatible output)",
            Confidence = Confidence.SemiOfficial,
            UpdatedAt = now,
        };
    }

    internal static List<RateWindow> ParseContextWindows(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new List<RateWindow>();

        var stripped = AnsiRe.Replace(output, "");
        var total = ParseFirstDouble(ContextWindowRe.Match(stripped));
        if (total is null)
            return new List<RateWindow>();

        var windows = new List<RateWindow>
        {
            ContextWindow("Context window", total.Value),
        };
        AddComponent("Context files");
        AddComponent("Tools");
        AddComponent("Kiro responses");
        AddComponent("Your prompts");
        return windows;

        void AddComponent(string label)
        {
            var match = Regex.Match(
                stripped,
                $@"{Regex.Escape(label)}\s+(\d+\.?\d*)%",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (ParseFirstDouble(match) is { } percent)
                windows.Add(ContextWindow(label, percent));
        }
    }

    private static RateWindow ContextWindow(string label, double usedPercent) => new()
    {
        Label = label,
        UsedPercent = Quota.ClampPercent(usedPercent),
        DetailText = "Current conversation context",
        CountsForAvailability = false,
    };

    private static string ParsePlanName(string output)
    {
        foreach (var regex in new[] { NewPlanRe, EstimatedPlanRe, PlanRe })
        {
            var match = regex.Match(output);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                return match.Groups[1].Value.Trim();
        }

        return "Kiro";
    }

    private static string DisplayPlanName(string planName) => string.Join(
        " ",
        planName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word =>
            word.Equals("KIRO", StringComparison.OrdinalIgnoreCase)
                ? "Kiro"
                : word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

    private static (double Used, double Total)? ParsePair(Match match)
    {
        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var used)
            || !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
        {
            return null;
        }

        return (used, total);
    }

    private static double? ParseFirstDouble(Match match) =>
        match.Success
        && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? ParseFirstInt(Match match) =>
        match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string? NormalizeResetDate(string value, DateTimeOffset now)
    {
        if (DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var fullDate))
        {
            return fullDate.ToString("O", CultureInfo.InvariantCulture);
        }

        if (!DateTime.TryParseExact(value, "MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthDay))
            return null;

        var candidate = new DateTimeOffset(now.Year, monthDay.Month, monthDay.Day, 0, 0, 0, now.Offset);
        if (candidate <= now)
            candidate = candidate.AddYears(1);
        return candidate.ToString("O", CultureInfo.InvariantCulture);
    }

    // Rust: %LOCALAPPDATA%\Kiro-Cli\kiro-cli.exe, or "kiro-cli.exe" if LOCALAPPDATA unset.
    private static string DefaultKiroCliPath()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrEmpty(local) ? "kiro-cli.exe" : $@"{local}\Kiro-Cli\kiro-cli.exe";
    }

    private static async Task<KiroCommandResult> RunCommandAsync(
        string binary,
        string command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(binary, command) };
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            throw new ProviderException($"Not available: Cannot launch kiro-cli at {binary}: {e.Message}", e);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdout = await stdoutTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stderr = await stderrTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new KiroCommandResult(stdout, stderr, process.ExitCode);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKillTree(process);
            throw new ProviderException("Timeout");
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort kill, mirroring Rust's `let _ = child.start_kill();`.
        }
    }

    private sealed record KiroCommandResult(string Stdout, string Stderr, int ExitCode);
}
