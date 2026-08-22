using System.Linq;
using QuotaLens.Core;
using QuotaLens.Helpers;

namespace QuotaLens.ViewModels;

public sealed class UsageTimelineSegmentViewModel
{
    public UsageTimelineSegmentViewModel(
        string providerType,
        string label,
        double weight,
        double availablePercent,
        string? resetText,
        string? resetToolTip,
        string? resetFrequencyText = null,
        double resetFrequencySortMinutes = double.PositiveInfinity,
        bool isRemainder = false,
        string instanceId = "",
        bool isGrayedOut = false,
        string? customAvailableText = null,
        string? automationStatusText = null,
        EffectiveUsageGroup? group = null,
        double effectiveTokensMillions = 0,
        string? compactAvailableText = null)
    {
        InstanceId = instanceId;
        ProviderType = providerType;
        Label = label;
        Weight = Math.Max(0, weight);
        AvailablePercent = Math.Clamp(availablePercent, 0, 100);
        AvailableText = customAvailableText ?? QuotaLens.Core.Quota.DisplayPct(AvailablePercent);
        CompactAvailableText = compactAvailableText ?? AvailableText;
        ResetText = resetText;
        ResetToolTip = resetToolTip;
        ResetFrequencyText = resetFrequencyText;
        ResetFrequencySortMinutes = resetFrequencySortMinutes;
        IsRemainder = isRemainder;
        IsGrayedOut = isGrayedOut;
        AutomationStatusText = automationStatusText;
        Group = group;
        EffectiveTokensMillions = Math.Max(0, effectiveTokensMillions);
    }

    public string InstanceId { get; }
    public string ProviderType { get; }
    public string Label { get; }
    public double Weight { get; }
    public double AvailablePercent { get; }
    public string AvailableText { get; }

    /// <summary>
    /// What a bar too narrow for its provider name still says. The full text pairs
    /// a token figure with a reset time; dropping the reset keeps the number that
    /// answers the chart's question instead of ellipsizing it away.
    /// </summary>
    public string CompactAvailableText { get; }
    public string? ResetText { get; }
    public string? ResetToolTip { get; }

    /// <summary>The bracket caption, e.g. "resets every 5 hours".</summary>
    public string? ResetFrequencyText { get; }
    public double ResetFrequencySortMinutes { get; }
    public bool IsRemainder { get; }
    public bool IsGrayedOut { get; }

    /// <summary>
    /// The reset bracket this bar is drawn under. Null for the remainder and
    /// empty-state bars, which stand under no bracket at all.
    /// </summary>
    public EffectiveUsageGroup? Group { get; }

    /// <summary>Tokens (millions) this plan can supply in the next five hours.</summary>
    public double EffectiveTokensMillions { get; }

    /// <summary>Stable grouping key for the bracket row beneath the chart.</summary>
    public string GroupKey => Group?.ToString() ?? "";

    /// <summary>
    /// Replaces the "&lt;amount&gt; available" clause of <see cref="AutomationName"/>.
    /// The empty-state bar shows no text at all, so this is the only thing that can
    /// tell a screen-reader user why the chart is blank.
    /// </summary>
    public string? AutomationStatusText { get; }

    public bool IsInteractive => !IsRemainder && !string.IsNullOrWhiteSpace(InstanceId);
    public bool HasResetText => !string.IsNullOrWhiteSpace(ResetText);
    public bool HasResetFrequencyText => !string.IsNullOrWhiteSpace(ResetFrequencyText);
    public string AutomationName
    {
        get
        {
            if (IsRemainder)
                return I18n.T("timeline.usedCapacity");

            var status = AutomationStatusText ?? $"{AvailableText} {I18n.T("common.available")}";
            // The empty-state bar names no provider, so it must not be announced
            // with a leading comma where the name would have been.
            var parts = new[] { Label, status, ResetFrequencyText }
                .Where(part => !string.IsNullOrWhiteSpace(part));
            return string.Join(", ", parts);
        }
    }
}
