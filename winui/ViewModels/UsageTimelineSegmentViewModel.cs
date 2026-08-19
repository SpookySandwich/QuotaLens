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
        string? customAvailableText = null)
    {
        InstanceId = instanceId;
        ProviderType = providerType;
        Label = label;
        Weight = Math.Max(0, weight);
        AvailablePercent = Math.Clamp(availablePercent, 0, 100);
        AvailableText = customAvailableText ?? QuotaLens.Core.Quota.DisplayPct(AvailablePercent);
        ResetText = resetText;
        ResetToolTip = resetToolTip;
        ResetFrequencyText = resetFrequencyText;
        ResetFrequencySortMinutes = resetFrequencySortMinutes;
        IsRemainder = isRemainder;
        IsGrayedOut = isGrayedOut;
    }

    public string InstanceId { get; }
    public string ProviderType { get; }
    public string Label { get; }
    public double Weight { get; }
    public double AvailablePercent { get; }
    public string AvailableText { get; }
    public string? ResetText { get; }
    public string? ResetToolTip { get; }
    public string? ResetFrequencyText { get; }
    public double ResetFrequencySortMinutes { get; }
    public bool IsRemainder { get; }
    public bool IsGrayedOut { get; }
    public bool IsInteractive => !IsRemainder && !string.IsNullOrWhiteSpace(InstanceId);
    public bool HasResetText => !string.IsNullOrWhiteSpace(ResetText);
    public bool HasResetFrequencyText => !string.IsNullOrWhiteSpace(ResetFrequencyText);
    public string AutomationName => IsRemainder
        ? I18n.T("timeline.usedCapacity")
        : string.IsNullOrWhiteSpace(ResetFrequencyText)
            ? $"{Label}, {AvailableText} {I18n.T("common.available")}"
            : $"{Label}, {AvailableText} {I18n.T("common.available")}, {ResetFrequencyText}";
}
