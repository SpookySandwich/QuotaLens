using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using QuotaLens.Core;

namespace QuotaLens.Providers;

/// <summary>Builds OpenCode Go quota from the local OpenCode history database via its read-only CLI.</summary>
internal static class OpenCodeGoLocalUsageProvider
{
    private const double FiveHourLimit = 12;
    private const double WeeklyLimit = 30;
    private const double MonthlyLimit = 60;
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);

    private const string HasPartTableSql =
        "SELECT name FROM sqlite_master WHERE type='table' AND name='part' LIMIT 1";

    private const string MessageUsageSql = """
        SELECT
          CAST(COALESCE(json_extract(data, '$.time.created'), time_created) AS INTEGER) AS createdMs,
          CAST(json_extract(data, '$.cost') AS REAL) AS cost
        FROM message
        WHERE json_valid(data)
          AND json_extract(data, '$.providerID') = 'opencode-go'
          AND json_extract(data, '$.role') = 'assistant'
          AND json_type(data, '$.cost') IN ('integer', 'real')
        """;

    private const string MessageAndPartUsageSql = """
        WITH message_costs AS (
          SELECT
            id AS messageID,
            CAST(COALESCE(json_extract(data, '$.time.created'), time_created) AS INTEGER) AS createdMs,
            CAST(json_extract(data, '$.cost') AS REAL) AS cost
          FROM message
          WHERE json_valid(data)
            AND json_extract(data, '$.providerID') = 'opencode-go'
            AND json_extract(data, '$.role') = 'assistant'
            AND json_type(data, '$.cost') IN ('integer', 'real')
        )
        SELECT createdMs, cost
        FROM message_costs
        UNION ALL
        SELECT
          CAST(COALESCE(json_extract(p.data, '$.time.created'), p.time_created, m.time_created) AS INTEGER) AS createdMs,
          CAST(json_extract(p.data, '$.cost') AS REAL) AS cost
        FROM part p
        JOIN message m ON m.id = p.message_id
        WHERE json_valid(p.data)
          AND json_valid(m.data)
          AND json_extract(p.data, '$.type') = 'step-finish'
          AND json_type(p.data, '$.cost') IN ('integer', 'real')
          AND json_extract(m.data, '$.providerID') = 'opencode-go'
          AND json_extract(m.data, '$.role') = 'assistant'
          AND NOT EXISTS (
            SELECT 1 FROM message_costs WHERE message_costs.messageID = p.message_id
          )
        """;

    public static async Task<ProviderSnapshot?> TryFetchAsync(
        string instanceId,
        IConfig config,
        CancellationToken ct)
    {
        var binary = ProviderConfig.Resolve(instanceId, config, "opencodego", "opencodego_cli_path") ?? "opencode";
        try
        {
            var tableResult = await RunQueryAsync(binary, HasPartTableSql, ct).ConfigureAwait(false);
            var sql = HasRows(tableResult) ? MessageAndPartUsageSql : MessageUsageSql;
            var usageJson = await RunQueryAsync(binary, sql, ct).ConfigureAwait(false);
            return ParseRows(usageJson, DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is ProviderException or JsonException)
        {
            return null;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string binary, string sql)
    {
        return HiddenCliProcess.CreateStartInfo(binary, new[] { "db", sql, "--format", "json" });
    }

    internal static ProviderSnapshot ParseRows(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ProviderException("Parse error: OpenCode local history was not a JSON array.");

        var rows = document.RootElement.EnumerateArray()
            .Select(ParseRow)
            .Where(row => row is not null)
            .Select(row => row!.Value)
            .ToList();
        if (rows.Count == 0)
            throw new ProviderException("Not available: OpenCode Go local history has no usage rows.");

        var nowMs = now.ToUnixTimeMilliseconds();
        var fiveHourStart = nowMs - (long)FiveHours.TotalMilliseconds;
        var weekStart = StartOfUtcWeek(now);
        var weekEnd = weekStart.AddDays(7);
        var month = MonthBounds(now, rows.Min(row => row.CreatedMs));

        var rollingCost = Sum(rows, fiveHourStart, nowMs);
        var weeklyCost = Sum(rows, weekStart.ToUnixTimeMilliseconds(), nowMs);
        var monthlyCost = Sum(rows, month.Start.ToUnixTimeMilliseconds(), nowMs);
        var oldestRolling = rows
            .Where(row => row.CreatedMs >= fiveHourStart && row.CreatedMs < nowMs)
            .Select(row => (long?)row.CreatedMs)
            .Min() ?? nowMs;

        return new ProviderSnapshot
        {
            ProviderId = "opencodego",
            Name = "OpenCode Go · Go",
            PlanId = "opencode-go-recurring",
            PlanName = "Go",
            Primary = UsageWindow(
                "5h Window",
                rollingCost,
                FiveHourLimit,
                DateTimeOffset.FromUnixTimeMilliseconds(oldestRolling).Add(FiveHours),
                5 * 60),
            Secondary = UsageWindow("Weekly", weeklyCost, WeeklyLimit, weekEnd, 7 * 24 * 60),
            Tertiary = UsageWindow("Monthly", monthlyCost, MonthlyLimit, month.End, null, countsForAvailability: true),
            SourceLabel = "OpenCode local history",
            Confidence = Confidence.SemiOfficial,
            SourceKind = ProviderSourceKind.CliOrLocal,
            ContractStability = ProviderContractStability.UpstreamCompatibility,
            EntitlementStatus = EntitlementStatus.Active,
            AvailabilityKind = ProviderAvailabilityKind.Finite,
            UpdatedAt = now,
        };
    }

    private static async Task<string> RunQueryAsync(string binary, string sql, CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(binary, sql) };
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            throw new ProviderException($"Not available: OpenCode CLI could not be launched: {e.Message}", e);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdout = await stdoutTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            var stderr = await stderrTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new ProviderException(
                    $"Not available: OpenCode local history query failed: {ProviderConfig.ResponseSummary(stderr)}");
            }
            return stdout;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort only.
            }
            throw new ProviderException("Timeout: OpenCode local history query did not complete.");
        }
    }

    private static bool HasRows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            && document.RootElement.GetArrayLength() > 0;
    }

    private static UsageRow? ParseRow(JsonElement row)
    {
        if (!row.TryGetProperty("createdMs", out var created)
            || created.ValueKind != JsonValueKind.Number
            || !created.TryGetInt64(out var createdMs)
            || !row.TryGetProperty("cost", out var costElement)
            || costElement.ValueKind != JsonValueKind.Number
            || !costElement.TryGetDouble(out var cost)
            || createdMs <= 0
            || cost < 0
            || !double.IsFinite(cost))
        {
            return null;
        }
        return new UsageRow(createdMs, cost);
    }

    private static RateWindow UsageWindow(
        string label,
        double cost,
        double limit,
        DateTimeOffset resetsAt,
        long? windowMinutes,
        bool countsForAvailability = false) => new()
    {
        Label = label,
        UsedPercent = Quota.ClampPercent(cost / limit * 100),
        ResetsAt = resetsAt.ToString("O", CultureInfo.InvariantCulture),
        ResetDescription = $"${cost.ToString("0.##", CultureInfo.InvariantCulture)} of ${limit.ToString("0", CultureInfo.InvariantCulture)}",
        WindowMinutes = windowMinutes,
        CountsForAvailability = countsForAvailability,
    };

    private static double Sum(IEnumerable<UsageRow> rows, long startMs, long endMs) =>
        rows.Where(row => row.CreatedMs >= startMs && row.CreatedMs < endMs).Sum(row => row.Cost);

    private static DateTimeOffset StartOfUtcWeek(DateTimeOffset now)
    {
        var utc = now.UtcDateTime.Date;
        var daysSinceMonday = ((int)utc.DayOfWeek + 6) % 7;
        return new DateTimeOffset(utc.AddDays(-daysSinceMonday), TimeSpan.Zero);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) MonthBounds(DateTimeOffset now, long anchorMs)
    {
        var utcNow = now.ToUniversalTime();
        var anchor = DateTimeOffset.FromUnixTimeMilliseconds(anchorMs).ToUniversalTime();
        var start = AnchoredMonth(utcNow.Year, utcNow.Month, anchor);
        if (start > utcNow)
        {
            var previous = start.AddMonths(-1);
            start = AnchoredMonth(previous.Year, previous.Month, anchor);
        }
        var next = start.AddMonths(1);
        var end = AnchoredMonth(next.Year, next.Month, anchor);
        return (start, end);
    }

    private static DateTimeOffset AnchoredMonth(int year, int month, DateTimeOffset anchor)
    {
        var day = Math.Min(anchor.Day, DateTime.DaysInMonth(year, month));
        return new DateTimeOffset(
            year,
            month,
            day,
            anchor.Hour,
            anchor.Minute,
            anchor.Second,
            anchor.Millisecond,
            TimeSpan.Zero);
    }

    private readonly record struct UsageRow(long CreatedMs, double Cost);
}
