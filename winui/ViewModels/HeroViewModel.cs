using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using QuotaLens.Core;
using QuotaLens.Helpers;

namespace QuotaLens.ViewModels;

/// <summary>
/// The hero answers one question: "which platform should I use right now?"
/// It uses the same recommended priority chain as the dashboard card order, while
/// excluding pay-as-you-go API balances from the top pick.
/// </summary>
public sealed partial class HeroViewModel : ObservableObject
{
    private const int MaxUsageTimelineSegments = 6;

    // "Use right now" recommendation.
    [ObservableProperty] public partial bool HasPick { get; set; }
    [ObservableProperty] public partial string PickName { get; set; } = "";
    [ObservableProperty] public partial string PickDetail { get; set; } = "";
    [ObservableProperty] public partial Brush PickBrush { get; set; } = new SolidColorBrush();
    [ObservableProperty] public partial Severity PickSeverity { get; set; } = Severity.Good;

    [ObservableProperty] public partial string NextText { get; set; } = "";   // "Next: codex-lb 99% · Antigravity 52%"
    [ObservableProperty] public partial bool HasNext { get; set; }

    [ObservableProperty] public partial string EmptyText { get; set; } = "";  // shown when nothing has capacity

    // Secondary footer stats.
    [ObservableProperty] public partial string OnlineValue { get; set; } = "0/0";
    [ObservableProperty] public partial string OnlineDetail { get; set; } = "";
    [ObservableProperty] public partial Severity OnlineSeverity { get; set; } = Severity.Good;
    [ObservableProperty] public partial bool HasUsageTimeline { get; set; }
    private string _pickProviderType = "";

    public ObservableCollection<UsageTimelineSegmentViewModel> UsageTimelineSegments { get; } = new();

    public string PickProviderType
    {
        get => _pickProviderType;
        private set => SetProperty(ref _pickProviderType, value);
    }

    public string Eyebrow => I18n.T("summary.bestPaidPlan");
    public string OnlineLabel => I18n.T("summary.online");
    public string UsageTimelineAutomationName => I18n.T("summary.usageTimeline");

    public void Update(
        IProviderService svc,
        IReadOnlyList<ProviderSortTerm>? recommendedPriorityOrder = null,
        bool hideSensitiveInfo = false)
    {
        var present = svc.Instances
            .Select(i => (Id: i.Id, Snap: svc.GetSnapshot(i.Id)))
            .Where(x => x.Snap != null)
            .Select(x => (x.Id, Snap: x.Snap!))
            .ToList();

        var total = svc.Instances.Count;
        var connected = present.Count(x => string.IsNullOrEmpty(x.Snap.Error));
        var errors = present.Count(x => !string.IsNullOrEmpty(x.Snap.Error));

        var ranked = ProviderSortPolicy.Order(
            present
                .Where(x => string.IsNullOrEmpty(x.Snap.Error))
                .Select(x => new ProviderPriorityCandidate(
                    x.Id,
                    x.Snap,
                    ProviderPriority.Score(x.Id, x.Snap, svc.Config)))
                .Where(x => x.Score.Bucket == ProviderPriority.UsableSubscriptionBucket),
            ProviderSortMode.PlanValue,
            x => x.Score,
            recommendedPriorityOrder);

        if (ranked.Count > 0)
        {
            var pick = ranked[0];
            HasPick = true;
            PickName = SensitiveDisplay.ProviderName(pick.Snapshot.Name, hideSensitiveInfo);
            PickSeverity = Quota.SeverityForAvailable(pick.Score.Availability);
            var providerType = Catalog.ProviderTypeForInstance(pick.Id, svc.Config);
            PickProviderType = providerType;
            PickBrush = Brand.Brush(providerType);
            PickDetail = BuildPickDetail(pick.Id, pick.Snapshot, pick.Score, svc.Config);
            EmptyText = "";

            // "Next" = the following usable subscriptions by the same priority chain (up to 2).
            var next = ranked.Skip(1).Take(2).ToList();
            if (next.Count > 0)
            {
                HasNext = true;
                NextText = "Next: " + string.Join(" · ",
                    next.Select(s => $"{SensitiveDisplay.ProviderName(ShortName(s.Snapshot.Name), hideSensitiveInfo)} {Quota.DisplayPct(s.Score.Availability)}"));
            }
            else { HasNext = false; NextText = ""; }
        }
        else
        {
            HasPick = false;
            PickProviderType = "";
            HasNext = false;
            NextText = "";
            EmptyText = connected == 0 ? I18n.T("summary.waiting") : I18n.T("summary.noPaidCapacity");
        }

        // Footer: account health.
        OnlineValue = $"{connected}/{total}";
        if (errors > 0) { OnlineDetail = $"{errors} {I18n.T("summary.needAttention")}"; OnlineSeverity = Severity.Warning; }
        else if (connected < total) { OnlineDetail = $"{total - connected} {I18n.T("summary.pending")}"; OnlineSeverity = Severity.Busy; }
        else { OnlineDetail = I18n.T("summary.allChecked"); OnlineSeverity = Severity.Good; }

        BuildUsageTimeline(svc, present, recommendedPriorityOrder, hideSensitiveInfo);
    }

    internal static string BuildPickDetail(
        string instanceId,
        ProviderSnapshot snapshot,
        ProviderPriorityScore score,
        IConfig config)
    {
        var displayPlan = ProviderPriority.DisplayPlanValue(instanceId, snapshot, config);
        var valuePart = displayPlan is { MonthlyValue: > 0 }
            ? $"{displayPlan.FormatMonthlyPrice()} · "
            : "";
        return $"{valuePart}{Quota.DisplayPct(score.Availability)} {I18n.T("common.available")}";
    }

    /// First segment of a provider name (drop the " · plan" suffix) for the compact Next line.
    private static string ShortName(string name)
    {
        var i = name.IndexOf(" · ", StringComparison.Ordinal);
        return i < 0 ? name : name[..i];
    }

    private void BuildUsageTimeline(
        IProviderService svc,
        IReadOnlyList<(string Id, ProviderSnapshot Snap)> present,
        IReadOnlyList<ProviderSortTerm>? recommendedPriorityOrder,
        bool hideSensitiveInfo)
    {
        UsageTimelineSegments.Clear();

        foreach (var segment in BuildUsageTimelineSegments(svc.Config, present, recommendedPriorityOrder, hideSensitiveInfo))
            UsageTimelineSegments.Add(segment);

        HasUsageTimeline = UsageTimelineSegments.Count > 0;
    }

    internal static IReadOnlyList<UsageTimelineSegmentViewModel> BuildUsageTimelineSegments(
        IConfig config,
        IReadOnlyList<(string Id, ProviderSnapshot Snap)> present,
        IReadOnlyList<ProviderSortTerm>? recommendedPriorityOrder = null,
        bool hideSensitiveInfo = false)
    {
        var ranked = ProviderSortPolicy.Order(
                present
                    .Where(x => string.IsNullOrEmpty(x.Snap.Error))
                    .Select(x => new ProviderPriorityCandidate(
                        x.Id,
                        x.Snap,
                        ProviderPriority.Score(x.Id, x.Snap, config)))
                    .Where(x => x.Score.Bucket == ProviderPriority.UsableSubscriptionBucket),
                ProviderSortMode.PlanValue,
                x => x.Score,
                recommendedPriorityOrder)
            .Take(MaxUsageTimelineSegments)
            .Select(x => TimelineCandidate.From(x, config, hideSensitiveInfo))
            .Where(x => x.AvailablePercent > 0.1)
            .OrderByDescending(x => x.ResetFrequencySortMinutes)
            .ThenByDescending(x => x.ResetSortMinutes)
            .ThenByDescending(x => x.Priority.Score.PlanValue)
            .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (ranked.Count == 0)
            return Array.Empty<UsageTimelineSegmentViewModel>();

        // The bar answers "what should I use now": each segment is a provider's
        // estimated tokens REMAINING this week (consistent community-estimate units
        // across providers). Order comes from the ranked pipeline above: slowest
        // reset cadence on the left (a weekly pool spent today is gone for days;
        // a 5h pool replenishes over lunch). Spent capacity is deliberately not
        // rendered: a mostly-grey bar answers a question nobody is asking.
        var segments = new List<UsageTimelineSegmentViewModel>();
        foreach (var candidate in ranked)
        {
            var tokensRemaining = candidate.WeeklyTokensMillions * candidate.AvailablePercent / 100.0;
            if (tokensRemaining <= 0)
                continue;

            segments.Add(new UsageTimelineSegmentViewModel(
                candidate.ProviderType,
                candidate.Label,
                tokensRemaining,
                candidate.AvailablePercent,
                candidate.ResetText,
                AppendTokenEstimate(candidate.ResetToolTip, candidate),
                candidate.ResetFrequencyText,
                candidate.ResetFrequencySortMinutes,
                instanceId: candidate.Priority.Id));
        }

        return segments;
    }

    private static string? AppendTokenEstimate(string? toolTip, TimelineCandidate candidate)
    {
        if (candidate.WeeklyTokensMillions <= 0)
            return toolTip;

        var remaining = candidate.WeeklyTokensMillions * candidate.AvailablePercent / 100.0;
        var qualifier = candidate.TokenEstimateKind switch
        {
            PlanTokenRules.TokenEstimateKind.Measured => I18n.T("timeline.tokensMeasured"),
            PlanTokenRules.TokenEstimateKind.Fallback => I18n.T("timeline.tokensEstimateUnknownPlan"),
            _ => I18n.T("timeline.tokensEstimate"),
        };
        var line = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            I18n.T("timeline.tokensRemaining"),
            FormatTokensMillions(remaining),
            FormatTokensMillions(candidate.WeeklyTokensMillions),
            qualifier);

        // Measured throughput (e.g. codex-lb's real token metrics) is shown as
        // context but never sizes the bar: one user's cache-heavy measurement is
        // not comparable with the normalized estimates used for other providers.
        if (candidate.Priority.Snapshot.MeasuredWeeklyTokensMillions is > 0)
        {
            line += "\n" + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                I18n.T("timeline.tokensMeasuredThroughput"),
                FormatTokensMillions(candidate.Priority.Snapshot.MeasuredWeeklyTokensMillions.Value));
        }

        return string.IsNullOrWhiteSpace(toolTip) ? line : $"{toolTip}\n{line}";
    }

    internal static string FormatTokensMillions(double millions) =>
        millions >= 1000
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.#}B", millions / 1000)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.#}M", millions);

    private sealed record TimelineCandidate(
        ProviderPriorityCandidate Priority,
        string ProviderType,
        string Label,
        double AvailablePercent,
        double ResetSortMinutes,
        string? ResetText,
        string? ResetToolTip,
        string? ResetFrequencyText,
        double ResetFrequencySortMinutes,
        double WeeklyTokensMillions,
        PlanTokenRules.TokenEstimateKind TokenEstimateKind)
    {
        public static TimelineCandidate From(
            ProviderPriorityCandidate candidate,
            IConfig config,
            bool hideSensitiveInfo)
        {
            var providerType = Catalog.ProviderTypeForInstance(candidate.Id, config);
            var reset = TimelineReset.For(providerType, candidate.Snapshot);
            var label = SensitiveDisplay.ProviderName(candidate.Snapshot.Name, hideSensitiveInfo);
            var weeklyTokens = PlanTokenRules.EstimateWeeklyTokensMillions(
                providerType,
                candidate.Snapshot,
                config,
                out var tokenEstimateKind,
                preferMeasured: false);
            return new TimelineCandidate(
                candidate,
                providerType,
                label,
                Math.Clamp(candidate.Score.Availability, 0, 100),
                reset.SortMinutes,
                reset.DisplayText,
                reset.ToolTip,
                reset.FrequencyText,
                reset.FrequencyMinutes,
                weeklyTokens,
                tokenEstimateKind);
        }
    }

    private sealed record TimelineReset(
        double SortMinutes,
        string? DisplayText,
        string? ToolTip,
        double FrequencyMinutes,
        string? FrequencyText)
    {
        public static TimelineReset For(string providerType, ProviderSnapshot snapshot)
        {
            var candidates = ResetCandidates(providerType, snapshot)
                .Select(window => ResetCandidate.From(providerType, window))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .ToList();

            if (candidates.Count == 0)
                return new TimelineReset(double.PositiveInfinity, null, null, double.PositiveInfinity, null);

            var selected = candidates
                .OrderBy(candidate => candidate.WindowSortMinutes)
                .ThenBy(candidate => candidate.MinutesUntil)
                .First();

            return new TimelineReset(
                selected.MinutesUntil,
                selected.DisplayText,
                selected.ToolTip,
                selected.WindowSortMinutes,
                selected.FrequencyText);
        }

        private static IEnumerable<SnapshotRateWindow> ResetCandidates(string providerType, ProviderSnapshot snapshot)
        {
            if (providerType == "antigravity" && snapshot.ModelQuotas.Count > 0)
            {
                var modelCandidates = snapshot.ModelQuotas
                    .Where(ModelQuotaPolicy.CountsForProviderAvailability)
                    .ToList();
                foreach (var quota in modelCandidates)
                    yield return new SnapshotRateWindow(quota.WindowType, quota.UsedPercent, quota.ResetsAt, null);
                if (modelCandidates.Count > 0)
                    yield break;
            }

            foreach (var window in ProviderSnapshotWindows.ResetWindows(snapshot))
                yield return window;
        }

    }

    private sealed record ResetCandidate(
        double WindowSortMinutes,
        double MinutesUntil,
        string? DisplayText,
        string? ToolTip,
        string? FrequencyText)
    {
        public static ResetCandidate? From(string providerType, SnapshotRateWindow window)
        {
            var minutesUntil = double.PositiveInfinity;
            string? displayText = null;
            string? toolTip = null;
            if (!string.IsNullOrWhiteSpace(window.ResetsAt)
                && DateTimeOffset.TryParse(
                    window.ResetsAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var when))
            {
                minutesUntil = Math.Max(0, (when - DateTimeOffset.UtcNow).TotalMinutes);
                var resetText = Quota.FmtReset(window.ResetsAt);
                if (!string.IsNullOrWhiteSpace(resetText))
                {
                    displayText = resetText is "now" or "< 1h"
                        ? resetText
                        : $"~{resetText}";
                    toolTip = when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                }
            }

            var windowSortMinutes = ResolveWindowSortMinutes(providerType, window, minutesUntil);
            return new ResetCandidate(
                windowSortMinutes,
                minutesUntil,
                displayText,
                toolTip,
                FormatFrequency(windowSortMinutes));
        }

        private static string? FormatFrequency(double minutes)
        {
            if (!double.IsFinite(minutes))
                return null;

            const double hour = 60;
            const double day = 24 * hour;
            const double week = 7 * day;

            if (Math.Abs(minutes - hour) < 0.1)
                return "reset hourly";
            if (Math.Abs(minutes - day) < 0.1)
                return "reset daily";
            if (Math.Abs(minutes - week) < 0.1)
                return "reset weekly";
            if (minutes >= 28 * day)
                return "reset monthly";

            if (minutes >= day && Math.Abs(minutes % day) < 0.1)
                return $"reset every {minutes / day:0}d";
            if (minutes >= hour && Math.Abs(minutes % hour) < 0.1)
                return $"reset every {minutes / hour:0}h";

            return $"reset every {minutes:0}m";
        }

        private static double ResolveWindowSortMinutes(
            string providerType,
            SnapshotRateWindow window,
            double minutesUntil)
        {
            if (window.WindowMinutes is > 0)
                return window.WindowMinutes.Value;

            var label = window.Label.ToLowerInvariant();
            if (label.Contains("5h", StringComparison.Ordinal)
                || label.Contains("hour", StringComparison.Ordinal)
                || label.Contains("today", StringComparison.Ordinal)
                || label.Contains("short", StringComparison.Ordinal))
            {
                return 5 * 60;
            }

            if (label.Contains("daily", StringComparison.Ordinal))
                return 24 * 60;

            if (label.Contains("7d", StringComparison.Ordinal)
                || label.Contains("week", StringComparison.Ordinal))
            {
                return 7 * 24 * 60;
            }

            if (label.Contains("month", StringComparison.Ordinal)
                || label.Contains("credit", StringComparison.Ordinal)
                || label.Contains("token plan", StringComparison.Ordinal)
                || label.Contains("compensation", StringComparison.Ordinal))
            {
                return 30 * 24 * 60;
            }

            if (string.Equals(providerType, "mimo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(providerType, "qoder", StringComparison.OrdinalIgnoreCase))
            {
                return 30 * 24 * 60;
            }

            return minutesUntil <= 24 * 60
                ? 5 * 60
                : minutesUntil <= 14 * 24 * 60
                    ? 7 * 24 * 60
                    : 30 * 24 * 60;
        }
    }
}
