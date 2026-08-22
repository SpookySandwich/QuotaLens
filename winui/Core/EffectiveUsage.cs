namespace QuotaLens.Core;

/// <summary>
/// The reset bracket a plan belongs to on the effective-usage chart, ordered the
/// way the chart draws them: fastest-refilling pools on the left, then the pools
/// that only come back weekly and monthly, then metered API balances.
/// </summary>
public enum EffectiveUsageGroup
{
    FiveHour = 0,
    Weekly = 1,
    Monthly = 2,

    /// <summary>A subscription that reports no reset window at all.</summary>
    Unspecified = 3,
    Api = 4,
}

/// <summary>Which pool the five-hour figure was taken from.</summary>
public enum EffectiveUsageBasis
{
    FiveHourWindow,
    WeeklyPool,
    MonthlyPool,
    OverallPool,
    ApiBalance,
}

/// <summary>
/// Answers one question for every plan on the same axis: <em>how many tokens can
/// I spend in the next five hours?</em>
///
/// Five hours is the shortest cycle any provider resets on, so it is the only
/// horizon on which every plan can be compared. A plan that refills every five
/// hours can only give what is left of the current window. A weekly or monthly
/// plan has no short-window ceiling, so the whole remaining pool is spendable
/// right now — nothing stops the user from burning a month's allowance in an
/// afternoon.
///
/// The figure is deliberately independent of the dashboard's sort mode: switching
/// the card order must not change what the chart measures.
/// </summary>
public readonly record struct EffectiveUsage(
    EffectiveUsageGroup Group,
    EffectiveUsageBasis Basis,
    /// <summary>Tokens (millions) spendable inside the next five hours.</summary>
    double TokensMillions,
    /// <summary>The governing pool at full, for "x of y left" prose.</summary>
    double PoolTokensMillions,
    /// <summary>Availability of the governing window, 0-100.</summary>
    double AvailablePercent,
    /// <summary>
    /// True when a longer pool — not the plan's own window — is what actually
    /// limits the next five hours (a fresh 5h window inside a spent weekly pool).
    /// </summary>
    bool IsCappedByLongerPool)
{
    /// <summary>One five-hour window as a fraction of a week.</summary>
    public const double FiveHourFractionOfWeek =
        QuotaCadencePolicy.FiveHourMinutes / (double)QuotaCadencePolicy.WeeklyMinutes;

    /// <summary>A month expressed in weeks, so one weekly estimate sizes both.</summary>
    public const double MonthlyFactorOfWeek =
        QuotaCadencePolicy.MonthlyMinutes / (double)QuotaCadencePolicy.WeeklyMinutes;

    /// <summary>
    /// <paramref name="weeklyTokensMillions"/> is the plan's whole weekly allowance
    /// (see <see cref="PlanTokenRules"/>), which is the unit every plan is normalized
    /// to regardless of the cadence it actually resets on.
    /// </summary>
    public static EffectiveUsage For(
        string instanceId,
        ProviderSnapshot snapshot,
        ProviderPriorityScore score,
        double weeklyTokensMillions,
        IConfig? config = null)
    {
        var weekly = Math.Max(0, weeklyTokensMillions);

        // "API-based" is a property of how the plan is billed, not of the windows it
        // happens to report: a metered key with a rate limit is still a balance the
        // user pays per token, and belongs in its own bracket.
        if (score.IsPayAsYouGo)
        {
            var providerType = Catalog.ProviderTypeForInstance(instanceId, config);
            var tokens = ApiTokenRules.TokensMillionsForBalance(providerType, score.BalanceAmount, config);
            return new EffectiveUsage(
                EffectiveUsageGroup.Api,
                EffectiveUsageBasis.ApiBalance,
                tokens,
                tokens,
                AvailablePercent: tokens > 0 ? 100 : 0,
                IsCappedByLongerPool: false);
        }

        // The *Window* availabilities, not the ranking ones: nesting is applied below
        // in tokens, where a 90%-full five-hour window inside a 54%-full weekly pool
        // correctly stays 90% full, because 90% of 10M is nowhere near 54% of 350M.
        var fiveHour = CadenceFor(snapshot, score.HasFiveHour, score.FiveHourWindowAvailability, QuotaCadence.FiveHour);
        var weeklyWindow = CadenceFor(snapshot, score.HasWeekly, score.WeeklyWindowAvailability, QuotaCadence.Weekly);
        var monthlyWindow = CadenceFor(snapshot, score.HasMonthly, score.MonthlyAvailability, QuotaCadence.Monthly);

        // A short window can never hand out more than the longer pool it sits inside.
        var weeklyRemaining = weeklyWindow.Has
            ? weekly * weeklyWindow.Available / 100.0
            : double.PositiveInfinity;
        var monthlyRemaining = monthlyWindow.Has
            ? weekly * MonthlyFactorOfWeek * monthlyWindow.Available / 100.0
            : double.PositiveInfinity;

        if (fiveHour.Has)
        {
            var pool = weekly * FiveHourFractionOfWeek;
            var own = pool * fiveHour.Available / 100.0;
            var capped = Math.Min(own, Math.Min(weeklyRemaining, monthlyRemaining));
            return new EffectiveUsage(
                EffectiveUsageGroup.FiveHour,
                EffectiveUsageBasis.FiveHourWindow,
                capped,
                pool,
                fiveHour.Available,
                IsCappedByLongerPool: capped < own - 1e-9);
        }

        if (weeklyWindow.Has)
        {
            var capped = Math.Min(weeklyRemaining, monthlyRemaining);
            return new EffectiveUsage(
                EffectiveUsageGroup.Weekly,
                EffectiveUsageBasis.WeeklyPool,
                capped,
                weekly,
                weeklyWindow.Available,
                IsCappedByLongerPool: capped < weeklyRemaining - 1e-9);
        }

        if (monthlyWindow.Has)
        {
            var pool = weekly * MonthlyFactorOfWeek;
            return new EffectiveUsage(
                EffectiveUsageGroup.Monthly,
                EffectiveUsageBasis.MonthlyPool,
                monthlyRemaining,
                pool,
                monthlyWindow.Available,
                IsCappedByLongerPool: false);
        }

        // No cadence reported. Overall availability against the weekly estimate is
        // the only honest reading left, and the bracket says the reset is unknown.
        return new EffectiveUsage(
            EffectiveUsageGroup.Unspecified,
            EffectiveUsageBasis.OverallPool,
            weekly * Math.Clamp(score.Availability, 0, 100) / 100.0,
            weekly,
            Math.Clamp(score.Availability, 0, 100),
            IsCappedByLongerPool: false);
    }

    /// <summary>
    /// <see cref="ProviderPriority"/> reads cadence from the windows that define a
    /// provider's headline availability, which deliberately leaves out rate limits
    /// a provider reports without counting them — Kimi's "5h Rate Limit" is one.
    /// Those windows are still enforced, and the bracket the user sees has to say
    /// so, so a cadence the score does not know about is read straight off the
    /// snapshot.
    /// </summary>
    private static (bool Has, double Available) CadenceFor(
        ProviderSnapshot snapshot,
        bool scoreHasCadence,
        double scoreAvailability,
        QuotaCadence cadence)
    {
        if (scoreHasCadence)
            return (true, Math.Clamp(scoreAvailability, 0, 100));

        // Model-specific quotas already drive the score's cadence figures; reading
        // them again would let one model family's limit stand for the whole plan.
        if (snapshot.ModelQuotas.Count > 0)
            return (false, 0);

        var has = false;
        var available = 0.0;
        foreach (var window in ProviderSnapshotWindows.ResetWindows(snapshot))
        {
            // The cadence has to be stated, never inferred from time-until-reset: a
            // balance that happens to expire in four hours is not a five-hour pool.
            if (!QuotaCadencePolicy.HasCadenceHint(window.Label, window.WindowMinutes)
                || QuotaCadencePolicy.For(window.Label, window.WindowMinutes) != cadence)
            {
                continue;
            }

            has = true;
            available = Math.Max(available, Math.Clamp(100.0 - window.UsedPercent, 0, 100));
        }

        return (has, available);
    }

    /// <summary>Representative window length, used to keep brackets in cadence order.</summary>
    public static double SortMinutesFor(EffectiveUsageGroup group) => group switch
    {
        EffectiveUsageGroup.FiveHour => QuotaCadencePolicy.FiveHourMinutes,
        EffectiveUsageGroup.Weekly => QuotaCadencePolicy.WeeklyMinutes,
        EffectiveUsageGroup.Monthly => QuotaCadencePolicy.MonthlyMinutes,
        _ => double.PositiveInfinity,
    };
}
