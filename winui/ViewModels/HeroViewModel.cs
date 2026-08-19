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
        bool hideSensitiveInfo = false,
        ProviderSortMode sortMode = ProviderSortMode.PlanValue)
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
                NextText = I18n.T("summary.nextPrefix") + string.Join(" · ",
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

        BuildUsageTimeline(svc, present, recommendedPriorityOrder, hideSensitiveInfo, sortMode);

        // Computed i18n strings: re-notify so OneWay bindings refresh on language change.
        OnPropertyChanged(nameof(Eyebrow));
        OnPropertyChanged(nameof(OnlineLabel));
        OnPropertyChanged(nameof(UsageTimelineAutomationName));
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
        bool hideSensitiveInfo,
        ProviderSortMode sortMode)
    {
        ReplaceUsageTimeline(BuildUsageTimelineSegments(
            svc.Config,
            present,
            recommendedPriorityOrder,
            hideSensitiveInfo,
            sortMode));
    }

    private void ReplaceUsageTimeline(IReadOnlyList<UsageTimelineSegmentViewModel> next)
    {
        var unchanged = UsageTimelineSegments.Count == next.Count
            && UsageTimelineSegments
                .Zip(next, (current, candidate) =>
                    current.InstanceId == candidate.InstanceId
                    && current.Weight == candidate.Weight
                    && current.AvailableText == candidate.AvailableText
                    && current.Label == candidate.Label
                    && current.ResetFrequencyText == candidate.ResetFrequencyText
                    && current.IsGrayedOut == candidate.IsGrayedOut)
                .All(same => same);
        if (unchanged)
        {
            HasUsageTimeline = UsageTimelineSegments.Count > 0;
            return;
        }

        var index = 0;
        for (; index < next.Count; index++)
        {
            if (index < UsageTimelineSegments.Count)
                UsageTimelineSegments[index] = next[index];
            else
                UsageTimelineSegments.Add(next[index]);
        }

        while (UsageTimelineSegments.Count > next.Count)
            UsageTimelineSegments.RemoveAt(UsageTimelineSegments.Count - 1);

        HasUsageTimeline = UsageTimelineSegments.Count > 0;
    }

    internal static IReadOnlyList<UsageTimelineSegmentViewModel> BuildUsageTimelineSegments(
        IConfig config,
        IReadOnlyList<(string Id, ProviderSnapshot Snap)> present,
        IReadOnlyList<ProviderSortTerm>? recommendedPriorityOrder = null,
        bool hideSensitiveInfo = false,
        ProviderSortMode sortMode = ProviderSortMode.PlanValue)
    {
        var ranked = ProviderSortPolicy.Order(
                present
                    .Where(x => string.IsNullOrEmpty(x.Snap.Error))
                    .Select(x => new ProviderPriorityCandidate(
                        x.Id,
                        x.Snap,
                        ProviderPriority.Score(x.Id, x.Snap, config)))
                    .Where(x => x.Score.Bucket == ProviderPriority.UsableSubscriptionBucket
                                || (x.Score.IsPayAsYouGo && x.Score.BalanceAmount > 0)),
                sortMode,
                x => x.Score,
                recommendedPriorityOrder)
            .Take(MaxUsageTimelineSegments)
            .Select(x => TimelineCandidate.From(x, config, hideSensitiveInfo, sortMode))
            .ToList();

        if (ranked.Count == 0)
            return Array.Empty<UsageTimelineSegmentViewModel>();

        var matching = ranked.Where(x => x.HasMatchingCadence && (x.AvailablePercent > 0.1 || x.Priority.Score.IsPayAsYouGo || sortMode == ProviderSortMode.PlanValue)).ToList();
        var displayCandidates = sortMode is ProviderSortMode.FiveHour or ProviderSortMode.Weekly or ProviderSortMode.Monthly
            ? matching
            : ranked;
        displayCandidates = displayCandidates
            .OrderByDescending(x => sortMode == ProviderSortMode.PlanValue
                ? x.Money.AmountUsd
                : x.ResetFrequencySortMinutes)
            .ThenByDescending(x => x.Priority.Score.PlanValue)
            .ThenBy(x => x.Priority.Id, StringComparer.Ordinal)
            .ToList();

        var segments = new List<UsageTimelineSegmentViewModel>();
        foreach (var candidate in displayCandidates)
        {
            var isValueMode = sortMode == ProviderSortMode.PlanValue;
            var tokensRemaining = candidate.WeeklyTokensMillions * candidate.AvailablePercent / 100.0;
            if (isValueMode)
            {
                if (candidate.Money.AmountUsd <= 0)
                    continue;
            }
            else if (!candidate.Priority.Score.IsPayAsYouGo && tokensRemaining <= 0)
            {
                continue;
            }

            bool isGrayedOut = candidate.Priority.Score.IsPayAsYouGo
                               || (matching.Count == 0 && !candidate.HasMatchingCadence);

            double weight;
            string availableText;
            string? customAvailableText;
            if (isValueMode)
            {
                availableText = Quota.FormatUsd(candidate.Money.AmountUsd);
                customAvailableText = availableText;
                weight = Math.Max(candidate.Money.AmountUsd, 0.01);
            }
            else if (candidate.Priority.Score.IsPayAsYouGo)
            {
                var bal = candidate.Priority.Snapshot.Balance;
                var total = bal?.Total > 0 ? bal.Total : (bal?.Paid ?? 0.0);
                var sym = bal?.Currency?.ToUpperInvariant() switch { "CNY" or "RMB" => "¥", _ => "$" };
                availableText = $"{sym}{total:0.##}";
                customAvailableText = availableText;
                weight = Math.Max(candidate.Priority.Score.BalanceAmount, 0.01);
            }
            else
            {
                var percent = Quota.DisplayPct(candidate.AvailablePercent);
                var reset = string.IsNullOrWhiteSpace(candidate.ResetText)
                    ? null
                    : candidate.ResetText.TrimStart('~').Trim();
                availableText = percent;
                customAvailableText = string.IsNullOrWhiteSpace(reset) ? null : $"{percent} · {reset}";
                weight = tokensRemaining;
            }

            segments.Add(new UsageTimelineSegmentViewModel(
                candidate.ProviderType,
                candidate.Label,
                weight,
                candidate.AvailablePercent,
                candidate.ResetText,
                isValueMode
                    ? AppendValueEstimate(candidate.ResetToolTip, candidate)
                    : candidate.Priority.Score.IsPayAsYouGo
                        ? $"Balance: {availableText}"
                        : AppendTokenEstimate(candidate.ResetToolTip, candidate),
                candidate.ResetFrequencyText,
                candidate.ResetFrequencySortMinutes,
                instanceId: candidate.Priority.Id,
                isGrayedOut: isGrayedOut,
                customAvailableText: customAvailableText));
        }

        return segments;
    }

    private static string? AppendValueEstimate(string? toolTip, TimelineCandidate candidate)
    {
        if (candidate.Money.AmountUsd <= 0)
            return toolTip;

        var line = candidate.Money.Kind switch
        {
            ProviderMoneyKind.Balance => $"Balance: {Quota.FormatUsd(candidate.Money.AmountUsd)}",
            ProviderMoneyKind.Estimate => $"{Quota.FormatUsd(candidate.Money.AmountUsd)}/mo {I18n.T("timeline.tokensEstimateUnknownPlan")}",
            _ => $"{Quota.FormatUsd(candidate.Money.AmountUsd)}/mo plan",
        };
        return string.IsNullOrWhiteSpace(toolTip) ? line : $"{toolTip}\n{line}";
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
            CultureInfo.CurrentCulture,
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
                CultureInfo.CurrentCulture,
                I18n.T("timeline.tokensMeasuredThroughput"),
                FormatTokensMillions(candidate.Priority.Snapshot.MeasuredWeeklyTokensMillions.Value));
        }

        return string.IsNullOrWhiteSpace(toolTip) ? line : $"{toolTip}\n{line}";
    }

    internal static string FormatTokensMillions(double millions) =>
        millions >= 1000
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.#}B", millions / 1000)
            : string.Format(CultureInfo.CurrentCulture, "{0:0.#}M", millions);

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
        PlanTokenRules.TokenEstimateKind TokenEstimateKind,
        bool HasMatchingCadence,
        ProviderMoney Money)
    {
        public static TimelineCandidate From(
            ProviderPriorityCandidate candidate,
            IConfig config,
            bool hideSensitiveInfo,
            ProviderSortMode sortMode = ProviderSortMode.PlanValue)
        {
            var providerType = Catalog.ProviderTypeForInstance(candidate.Id, config);
            var reset = TimelineReset.For(candidate.Snapshot, sortMode);
            var label = SensitiveDisplay.ProviderName(candidate.Snapshot.Name, hideSensitiveInfo);
            var weeklyTokens = PlanTokenRules.EstimateWeeklyTokensMillions(
                providerType,
                candidate.Snapshot,
                config,
                out var tokenEstimateKind,
                preferMeasured: false);

            var availablePct = sortMode switch
            {
                ProviderSortMode.FiveHour => Math.Clamp(candidate.Score.FiveHourAvailability, 0, 100),
                ProviderSortMode.Weekly => Math.Clamp(candidate.Score.WeeklyAvailability, 0, 100),
                ProviderSortMode.Monthly => Math.Clamp(candidate.Score.MonthlyAvailability, 0, 100),
                _ => Math.Clamp(candidate.Score.Availability, 0, 100),
            };

            var hasMatchingCadence = sortMode switch
            {
                ProviderSortMode.FiveHour => candidate.Score.HasFiveHour,
                ProviderSortMode.Weekly => candidate.Score.HasWeekly,
                ProviderSortMode.Monthly => candidate.Score.HasMonthly,
                _ => true,
            };

            var frequencyText = FrequencyTextFor(sortMode, hasMatchingCadence, reset.FrequencyText);

            return new TimelineCandidate(
                candidate,
                providerType,
                label,
                availablePct,
                reset.SortMinutes,
                reset.DisplayText,
                reset.ToolTip,
                frequencyText,
                reset.FrequencyMinutes,
                weeklyTokens,
                tokenEstimateKind,
                hasMatchingCadence,
                ProviderMoney.For(candidate.Id, candidate.Snapshot, candidate.Score, config));
        }

        private static string? FrequencyTextFor(
            ProviderSortMode sortMode,
            bool hasMatchingCadence,
            string? fallback)
        {
            if (!hasMatchingCadence)
                return fallback;

            return sortMode switch
            {
                ProviderSortMode.FiveHour => I18n.T("timeline.effective5h"),
                ProviderSortMode.Weekly => I18n.T("timeline.effectiveWeekly"),
                ProviderSortMode.Monthly => I18n.T("timeline.effectiveMonthly"),
                _ => fallback,
            };
        }
    }

    private sealed record TimelineReset(
        double SortMinutes,
        string? DisplayText,
        string? ToolTip,
        double FrequencyMinutes,
        string? FrequencyText,
        double AvailablePercent = 0.0)
    {
        public static TimelineReset For(ProviderSnapshot snapshot, ProviderSortMode sortMode = ProviderSortMode.PlanValue)
        {
            var candidates = ResetCandidates(snapshot)
                .Select(ResetCandidate.From)
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .ToList();

            if (candidates.Count == 0)
                return new TimelineReset(double.PositiveInfinity, null, null, double.PositiveInfinity, null, 0);

            IEnumerable<ResetCandidate> filtered = sortMode switch
            {
                ProviderSortMode.FiveHour => candidates.Where(c => c.Cadence == QuotaCadence.FiveHour),
                ProviderSortMode.Weekly => candidates.Where(c => c.Cadence == QuotaCadence.Weekly),
                ProviderSortMode.Monthly => candidates.Where(c => c.Cadence == QuotaCadence.Monthly),
                _ => candidates,
            };

            var selected = filtered
                .OrderBy(candidate => candidate.WindowSortMinutes)
                .ThenBy(candidate => candidate.MinutesUntil)
                .FirstOrDefault()
                ?? candidates
                    .OrderBy(candidate => candidate.WindowSortMinutes)
                    .ThenBy(candidate => candidate.MinutesUntil)
                    .FirstOrDefault();

            if (selected is null)
                return new TimelineReset(double.PositiveInfinity, null, null, double.PositiveInfinity, null, 0);

            return new TimelineReset(
                selected.MinutesUntil,
                selected.DisplayText,
                selected.ToolTip,
                selected.WindowSortMinutes,
                selected.FrequencyText,
                selected.AvailablePercent);
        }

        private static IEnumerable<SnapshotRateWindow> ResetCandidates(ProviderSnapshot snapshot)
        {
            if (snapshot.ModelQuotas.Count > 0)
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
        QuotaCadence Cadence,
        double WindowSortMinutes,
        double MinutesUntil,
        string? DisplayText,
        string? ToolTip,
        string? FrequencyText,
        double AvailablePercent)
    {
        public static ResetCandidate? From(SnapshotRateWindow window)
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
                var resetText = ResetFormatter.FormatDurationUntil(window.ResetsAt);
                if (!string.IsNullOrWhiteSpace(resetText))
                {
                    displayText = resetText is "now" or "< 1h"
                        ? resetText
                        : $"~{resetText}";
                    toolTip = when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                }
            }

            var cadence = QuotaCadencePolicy.For(window.Label, window.WindowMinutes, minutesUntil);
            double windowSortMinutes = window.WindowMinutes is > 0
                ? window.WindowMinutes.Value
                : QuotaCadencePolicy.DefaultWindowMinutes(cadence);
            if (windowSortMinutes <= 0)
                windowSortMinutes = double.IsFinite(minutesUntil) ? minutesUntil : double.PositiveInfinity;

            var avail = Math.Clamp(100.0 - window.UsedPercent, 0, 100);
            return new ResetCandidate(
                cadence,
                windowSortMinutes,
                minutesUntil,
                displayText,
                toolTip,
                FormatFrequency(windowSortMinutes),
                avail);
        }

        private static string? FormatFrequency(double minutes)
        {
            if (!double.IsFinite(minutes))
                return null;

            const double hour = 60;
            const double day = 24 * hour;
            const double week = 7 * day;

            if (Math.Abs(minutes - hour) < 0.1)
                return I18n.T("timeline.resetHourly");
            if (Math.Abs(minutes - day) < 0.1)
                return I18n.T("timeline.resetDaily");
            if (Math.Abs(minutes - week) < 0.1)
                return I18n.T("timeline.resetWeekly");
            if (minutes >= 28 * day)
                return I18n.T("timeline.resetMonthly");

            if (minutes >= day && Math.Abs(minutes % day) < 0.1)
                return I18n.T("timeline.resetEveryD", "n", (minutes / day).ToString("0", CultureInfo.InvariantCulture));
            if (minutes >= hour && Math.Abs(minutes % hour) < 0.1)
                return I18n.T("timeline.resetEveryH", "n", (minutes / hour).ToString("0", CultureInfo.InvariantCulture));

            return I18n.T("timeline.resetEveryM", "n", minutes.ToString("0", CultureInfo.InvariantCulture));
        }
    }
}
