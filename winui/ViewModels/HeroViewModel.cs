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
    // Four reset brackets have to fit side by side, and each one has to keep at
    // least its best plan, so the chart needs more room than a flat top-N.
    private const int MaxUsageTimelineSegments = 8;

    // UsageCylinder drops any segment whose weight is not positive, so a provider
    // at 0% must still be worth a sliver: disappearing reads as "not configured".
    private const double MinSegmentWeight = 0.01;

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
    public string UsageTimelineCaption => I18n.T("timeline.caption");

    /// <summary>
    /// <paramref name="recommendedPriorityOrder"/> steers the "use right now" pick
    /// only. The chart below it measures effective usage and is intentionally not
    /// parameterized by the dashboard's sort mode.
    /// </summary>
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

        BuildUsageTimeline(svc, present, hideSensitiveInfo);

        // Computed i18n strings: re-notify so OneWay bindings refresh on language change.
        OnPropertyChanged(nameof(Eyebrow));
        OnPropertyChanged(nameof(OnlineLabel));
        OnPropertyChanged(nameof(UsageTimelineAutomationName));
        OnPropertyChanged(nameof(UsageTimelineCaption));
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
        bool hideSensitiveInfo)
    {
        ReplaceUsageTimeline(BuildUsageTimelineSegments(svc.Config, present, hideSensitiveInfo));
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
                    && current.IsGrayedOut == candidate.IsGrayedOut
                    && current.Group == candidate.Group
                    && current.AutomationStatusText == candidate.AutomationStatusText)
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

    /// <summary>
    /// The chart answers one question, always the same one: how many tokens can
    /// each plan supply in the next five hours? It is deliberately independent of
    /// the card sort order — switching the dashboard between 5h, weekly, monthly,
    /// and value views changes which cards lead, never what the chart measures.
    ///
    /// An exhausted subscription still exists, so it stays a candidate and simply
    /// draws a spent bar. Expired entitlements are the exception: PlanTokenRules
    /// would invent an allowance for a plan that no longer grants anything.
    /// </summary>
    internal static IReadOnlyList<UsageTimelineSegmentViewModel> BuildUsageTimelineSegments(
        IConfig config,
        IReadOnlyList<(string Id, ProviderSnapshot Snap)> present,
        bool hideSensitiveInfo = false)
    {
        var candidates = present
            .Where(x => string.IsNullOrEmpty(x.Snap.Error)
                        && x.Snap.EntitlementStatus != EntitlementStatus.Expired)
            .Select(x => new ProviderPriorityCandidate(
                x.Id,
                x.Snap,
                ProviderPriority.Score(x.Id, x.Snap, config)))
            .Where(x => x.Score.Bucket is ProviderPriority.UsableSubscriptionBucket
                            or ProviderPriority.ExhaustedSubscriptionBucket
                        || x.Score.IsPayAsYouGo)
            .Select(x => TimelineCandidate.From(x, config, hideSensitiveInfo))
            // A plan with no allowance at all has nothing to size a bar with, and a
            // metered key priced in minutes rather than tokens has no token figure.
            .Where(candidate => candidate.Effective.PoolTokensMillions > 0)
            .ToList();

        var segments = SelectCandidates(candidates)
            .Select(BuildSegment)
            .ToList();

        // The chart is a fixed part of the dashboard, not something that appears
        // only when it has good news. With nothing to draw — no providers yet, all
        // of them still connecting — it holds its place as one gray bar instead of
        // collapsing the card and reflowing the page.
        return segments.Count > 0
            ? segments
            : new List<UsageTimelineSegmentViewModel> { BuildEmptySegment() };
    }

    /// <summary>
    /// Slots are scarce, so capacity picks the survivors — but every bracket the
    /// user actually owns keeps its best plan first. Letting raw capacity fill all
    /// the slots would silently answer "you have nothing that resets weekly" when
    /// the truth is only that the weekly pool is smaller.
    /// </summary>
    private static IReadOnlyList<TimelineCandidate> SelectCandidates(IReadOnlyList<TimelineCandidate> candidates)
    {
        var byCapacity = candidates
            .OrderByDescending(candidate => candidate.Effective.TokensMillions)
            .ThenByDescending(candidate => candidate.Priority.Score.PlanValue)
            .ThenBy(candidate => candidate.Priority.Id, StringComparer.Ordinal)
            .ToList();

        var kept = new List<TimelineCandidate>();
        var keptIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in byCapacity
                     .Select(candidate => candidate.Effective.Group)
                     .Distinct()
                     .OrderBy(group => group))
        {
            var best = byCapacity.First(candidate => candidate.Effective.Group == group);
            if (keptIds.Add(best.Priority.Id))
                kept.Add(best);
        }

        foreach (var candidate in byCapacity)
        {
            if (kept.Count >= MaxUsageTimelineSegments)
                break;
            if (keptIds.Add(candidate.Priority.Id))
                kept.Add(candidate);
        }

        // Layout is applied to the survivors unchanged: brackets left to right in
        // cadence order, biggest plan first inside each bracket.
        return kept
            .Take(MaxUsageTimelineSegments)
            .OrderBy(candidate => candidate.Effective.Group)
            .ThenByDescending(candidate => candidate.Effective.TokensMillions)
            .ThenByDescending(candidate => candidate.Priority.Score.PlanValue)
            .ThenBy(candidate => candidate.Priority.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The empty bar. It stands for nothing — no provider, no plan, no number — so it
    /// carries no label and no value text, only the gray track holding the chart's
    /// place. No instance id, so it is inert: no hover highlight, no click-to-scroll,
    /// and screen readers hear the reason instead of a measurement that never existed.
    /// </summary>
    private static UsageTimelineSegmentViewModel BuildEmptySegment() =>
        new(
            providerType: "",
            label: "",
            weight: 1,
            availablePercent: 0,
            resetText: null,
            resetToolTip: null,
            resetFrequencyText: null,
            instanceId: "",
            isGrayedOut: true,
            customAvailableText: "",
            automationStatusText: I18n.T("timeline.noCapacity"));

    private static UsageTimelineSegmentViewModel BuildSegment(TimelineCandidate candidate)
    {
        var effective = candidate.Effective;
        var tokensText = FormatTokensMillions(effective.TokensMillions);
        var reset = string.IsNullOrWhiteSpace(candidate.ResetText)
            ? null
            : candidate.ResetText.TrimStart('~').Trim();

        return new UsageTimelineSegmentViewModel(
            candidate.ProviderType,
            candidate.Label,
            Math.Max(WeightForTokens(effective.TokensMillions), MinSegmentWeight),
            effective.AvailablePercent,
            candidate.ResetText,
            BuildToolTip(candidate),
            BracketTextFor(effective.Group),
            EffectiveUsage.SortMinutesFor(effective.Group),
            instanceId: candidate.Priority.Id,
            // Gray now means "nothing to spend here right now", which is what the
            // chart is asked. It no longer marks pay-as-you-go: a funded API key
            // holds real capacity and sits in its own bracket instead.
            isGrayedOut: effective.TokensMillions <= 0.0005,
            customAvailableText: reset is null ? tokensText : $"{tokensText} · {reset}",
            group: effective.Group,
            effectiveTokensMillions: effective.TokensMillions,
            compactAvailableText: tokensText);
    }

    /// <summary>
    /// Bar width is the token figure itself. Effective usage compresses the range
    /// on its own — five hours of a very large plan lands near a whole small weekly
    /// pool — so a linear axis stays readable while "twice as wide" keeps meaning
    /// "twice as many tokens". Swap this for a log curve if real data ever spreads
    /// far enough to make the small bars unreadable.
    /// </summary>
    private static double WeightForTokens(double tokensMillions) => Math.Max(0, tokensMillions);

    internal static string BracketTextFor(EffectiveUsageGroup group) => group switch
    {
        EffectiveUsageGroup.FiveHour => I18n.T("timeline.bracket5h"),
        EffectiveUsageGroup.Weekly => I18n.T("timeline.bracketWeekly"),
        EffectiveUsageGroup.Monthly => I18n.T("timeline.bracketMonthly"),
        EffectiveUsageGroup.Api => I18n.T("timeline.bracketApi"),
        _ => I18n.T("timeline.bracketUnspecified"),
    };

    private static string? BuildToolTip(TimelineCandidate candidate)
    {
        var effective = candidate.Effective;
        var toolTip = candidate.ResetToolTip;

        if (effective.Group == EffectiveUsageGroup.Api)
            return ApiToolTip(candidate, toolTip);

        if (effective.PoolTokensMillions <= 0)
            return toolTip;

        var qualifier = candidate.TokenEstimateKind switch
        {
            PlanTokenRules.TokenEstimateKind.Measured => I18n.T("timeline.tokensMeasured"),
            PlanTokenRules.TokenEstimateKind.Fallback => I18n.T("timeline.tokensEstimateUnknownPlan"),
            _ => I18n.T("timeline.tokensEstimate"),
        };
        toolTip = AppendLine(toolTip, string.Format(
            CultureInfo.CurrentCulture,
            I18n.T("timeline.effectiveTokens"),
            FormatTokensMillions(effective.TokensMillions),
            FormatTokensMillions(effective.PoolTokensMillions),
            qualifier));

        // A fresh five-hour window inside a spent weekly pool would otherwise read
        // as a bug: the percentage says full, the bar says nearly nothing.
        if (effective.IsCappedByLongerPool)
            toolTip = AppendLine(toolTip, I18n.T("timeline.cappedByLongerPool"));

        // Measured throughput (e.g. codex-lb's real token metrics) is shown as
        // context but never sizes the bar: one user's cache-heavy measurement is
        // not comparable with the normalized estimates used for other providers.
        if (candidate.Priority.Snapshot.MeasuredWeeklyTokensMillions is > 0)
        {
            toolTip = AppendLine(toolTip, string.Format(
                CultureInfo.CurrentCulture,
                I18n.T("timeline.tokensMeasuredThroughput"),
                FormatTokensMillions(candidate.Priority.Snapshot.MeasuredWeeklyTokensMillions.Value)));
        }

        return toolTip;
    }

    /// A metered key is money, so the bar says what the money buys and the tooltip
    /// shows both halves of the conversion — the balance and the rate it assumed.
    private static string? ApiToolTip(TimelineCandidate candidate, string? toolTip)
    {
        var balance = candidate.Priority.Snapshot.Balance;
        var total = Math.Max(0, balance?.Total ?? 0.0);
        var symbol = balance?.Currency?.ToUpperInvariant() switch
        {
            "CNY" or "RMB" => "¥",
            "EUR" => "€",
            _ => "$",
        };
        toolTip = AppendLine(toolTip, $"{I18n.T("timeline.balance")}: {symbol}{total:0.##}");

        var rate = ApiTokenRules.UsdPerMillionTokens(candidate.ProviderType);
        return rate is > 0
            ? AppendLine(toolTip, string.Format(
                CultureInfo.CurrentCulture,
                I18n.T("timeline.apiTokens"),
                FormatTokensMillions(candidate.Effective.TokensMillions),
                Quota.FormatUsd(rate.Value)))
            : toolTip;
    }

    private static string AppendLine(string? toolTip, string line) =>
        string.IsNullOrWhiteSpace(toolTip) ? line : $"{toolTip}\n{line}";

    internal static string FormatTokensMillions(double millions) =>
        millions >= 1000
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.#}B", millions / 1000)
            : string.Format(CultureInfo.CurrentCulture, "{0:0.#}M", millions);

    private sealed record TimelineCandidate(
        ProviderPriorityCandidate Priority,
        string ProviderType,
        string Label,
        string? ResetText,
        string? ResetToolTip,
        double WeeklyTokensMillions,
        PlanTokenRules.TokenEstimateKind TokenEstimateKind,
        EffectiveUsage Effective)
    {
        public static TimelineCandidate From(
            ProviderPriorityCandidate candidate,
            IConfig config,
            bool hideSensitiveInfo)
        {
            var providerType = Catalog.ProviderTypeForInstance(candidate.Id, config);
            var label = SensitiveDisplay.ProviderName(candidate.Snapshot.Name, hideSensitiveInfo);
            var weeklyTokens = PlanTokenRules.EstimateWeeklyTokensMillions(
                providerType,
                candidate.Snapshot,
                config,
                out var tokenEstimateKind,
                preferMeasured: false);
            var effective = EffectiveUsage.For(
                candidate.Id,
                candidate.Snapshot,
                candidate.Score,
                weeklyTokens,
                config);
            var reset = TimelineReset.For(candidate.Snapshot, effective.Group);

            return new TimelineCandidate(
                candidate,
                providerType,
                label,
                reset.DisplayText,
                reset.ToolTip,
                weeklyTokens,
                tokenEstimateKind,
                effective);
        }
    }

    private sealed record TimelineReset(
        double SortMinutes,
        string? DisplayText,
        string? ToolTip)
    {
        private static readonly TimelineReset None = new(double.PositiveInfinity, null, null);

        /// <summary>
        /// The reset shown on a bar is the one belonging to the pool the bar was
        /// sized from. Any other window would date-stamp a number it does not
        /// describe.
        /// </summary>
        public static TimelineReset For(ProviderSnapshot snapshot, EffectiveUsageGroup group)
        {
            var candidates = ResetCandidates(snapshot)
                .Select(ResetCandidate.From)
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .ToList();

            if (candidates.Count == 0)
                return None;

            var wanted = group switch
            {
                EffectiveUsageGroup.FiveHour => QuotaCadence.FiveHour,
                EffectiveUsageGroup.Weekly => QuotaCadence.Weekly,
                EffectiveUsageGroup.Monthly => QuotaCadence.Monthly,
                _ => QuotaCadence.None,
            };

            IEnumerable<ResetCandidate> filtered = wanted == QuotaCadence.None
                ? candidates
                : candidates.Where(candidate => candidate.Cadence == wanted);

            var selected = Best(filtered) ?? Best(candidates);

            return selected is null
                ? None
                : new TimelineReset(selected.MinutesUntil, selected.DisplayText, selected.ToolTip);
        }

        /// <summary>
        /// A window whose reset instant has already passed has nothing left to
        /// announce — its quota is back. Providers keep reporting such windows
        /// (codex-lb's per-model sub-quotas linger for days), and letting one win
        /// the tie stamps "now" on a bar that does not refill for another 15 hours.
        /// </summary>
        private static ResetCandidate? Best(IEnumerable<ResetCandidate> candidates) =>
            candidates
                .OrderBy(candidate => candidate.MinutesUntil <= 0 ? 1 : 0)
                .ThenBy(candidate => candidate.WindowSortMinutes)
                .ThenBy(candidate => candidate.MinutesUntil)
                .FirstOrDefault();

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
        string? ToolTip)
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

            return new ResetCandidate(cadence, windowSortMinutes, minutesUntil, displayText, toolTip);
        }
    }
}
