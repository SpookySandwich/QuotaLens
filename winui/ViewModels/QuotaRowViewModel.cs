using CommunityToolkit.Mvvm.ComponentModel;
using QuotaLens.Core;
using QuotaLens.Helpers;

namespace QuotaLens.ViewModels;

/// <summary>
/// A quota or informational metric row inside a provider card. Holds the
/// already-derived display state so the view does not reinterpret provider data.
/// </summary>
public sealed partial class QuotaRowViewModel : ObservableObject
{
    public QuotaRowViewModel(
        RateWindow window,
        bool prominent = false,
        bool hideSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(window);

        Label = FormatCardLabel(window.Label);
        IsQuota = window.Kind == RateWindowKind.Quota;
        var rawValueText = IsQuota
            ? ""
            : window.ValueText ?? window.DetailText ?? I18n.T("common.notAvailable");
        IsValueHidden = hideSensitive && window.Sensitivity != RateWindowSensitivity.None;
        ValueText = IsValueHidden
            ? window.Sensitivity == RateWindowSensitivity.Financial
                ? SensitiveDisplay.HiddenBalanceText
                : SensitiveDisplay.HiddenText
            : rawValueText;
        AvailablePercent = Quota.AvailablePct(window.UsedPercent);
        Severity = Quota.SeverityForAvailable(AvailablePercent);
        AvailableText = Quota.DisplayPct(AvailablePercent);
        ResetText = ResetFormatter.FormatCaption(window);
        IsProminent = prominent;
        AutomationId = $"MetricRow_{new string(Label.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray())}";
        AutomationName = IsQuota
            ? $"{Label}: {AvailableText} {AvailableSuffix}"
            : $"{Label}: {ValueText}";
    }

    public static string FormatCardLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "";

        if (I18n.Current == I18n.Lang.Zh)
        {
            return label switch
            {
                "5h Pool" or "5h Window" or "5h Rate Limit" or "5-hour" or "5h" => "5小时",
                "7d Pool" or "7d" or "Weekly included" or "Weekly Pool" or "Weekly usage" or "Weekly" => "按周",
                "Gemini 5-hour" or "Gemini 5h" => "Gemini · 5小时",
                "Gemini weekly" or "Gemini Weekly" => "Gemini · 按周",
                "Claude/GPT 5-hour" or "Claude/GPT 5h" => "Claude/GPT · 5小时",
                "Claude/GPT weekly" or "Claude/GPT Weekly" => "Claude/GPT · 按周",
                "Monthly included" or "Monthly usage" or "Monthly" => "按月",
                "Effective 5h" => "有效 5小时",
                "Effective Weekly" => "有效按周",
                "Effective Monthly" => "有效按月",
                "Total quota" => "总额度",
                _ => I18n.WindowLabel(label),
            };
        }

        return label switch
        {
            "5h Pool" or "5h Window" or "5h Rate Limit" or "5-hour" => "5h",
            "7d Pool" or "7d" or "Weekly included" or "Weekly Pool" or "Weekly usage" => "Weekly",
            "Gemini 5-hour" => "Gemini · 5h",
            "Gemini weekly" => "Gemini · Weekly",
            "Claude/GPT 5-hour" => "Claude/GPT · 5h",
            "Claude/GPT weekly" => "Claude/GPT · Weekly",
            "Monthly included" or "Monthly usage" => "Monthly",
            "Effective 5h" or "Effective Weekly" or "Effective Monthly" => label,
            "Total quota" => label,
            _ => I18n.WindowLabel(label),
        };
    }

    public QuotaRowViewModel(
        string label,
        double usedPercent,
        string? resetsAt,
        bool prominent = false)
        : this(
            new RateWindow
            {
                Label = label,
                UsedPercent = usedPercent,
                ResetsAt = resetsAt,
            },
            prominent)
    {
    }

    public string Label { get; }
    public bool IsQuota { get; }
    public bool IsInformational => !IsQuota;
    public bool IsValueHidden { get; }
    public string ValueText { get; }
    public double AvailablePercent { get; }
    public Severity Severity { get; }
    public string AvailableText { get; }
    public string? ResetText { get; }
    public bool IsProminent { get; }
    public string AutomationId { get; }
    public string AutomationName { get; }

    public string AvailableSuffix => I18n.T("common.available");
}

/// <summary>A provider-agnostic model family plus its most available quota row.</summary>
public sealed partial class FamilyGroupViewModel : ObservableObject
{
    public FamilyGroupViewModel(string family, QuotaRowViewModel best)
    {
        Family = QuotaRowViewModel.FormatCardLabel(family);
        Best = best;
    }

    public string Family { get; }
    public QuotaRowViewModel Best { get; }
}

/// <summary>A pooled-account quota breakdown.</summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    public AccountRowViewModel(AccountInfo account, int index, bool hideSensitive = false)
    {
        AutomationId = $"AccountRow_{index}";
        var accountName = account.Email ?? account.Plan ?? I18n.T("privacy.account", "n", (index + 1).ToString());
        Name = SensitiveDisplay.AccountName(accountName, index, hideSensitive);
        IsNameHidden = hideSensitive && !string.IsNullOrWhiteSpace(account.Email);
        PrivacyPlaceholderWidth = 96 + (index % 3) * 18;
        PrimaryLabel = QuotaRowViewModel.FormatCardLabel(account.PrimaryLabel ?? "5h");
        SecondaryLabel = QuotaRowViewModel.FormatCardLabel(account.SecondaryLabel ?? "Weekly");

        if (account.PrimaryUsedPercent is double primaryUsed)
        {
            PrimaryAvailableText = Quota.DisplayPct(Quota.AvailablePct(primaryUsed));
            PrimarySeverity = Quota.SeverityForAvailable(Quota.AvailablePct(primaryUsed));
            PrimaryResetText = FormatReset(account.PrimaryResetsAt);
            HasWindowBreakdown = true;
        }

        if (account.SecondaryUsedPercent is double secondaryUsed)
        {
            SecondaryAvailableText = Quota.DisplayPct(Quota.AvailablePct(secondaryUsed));
            SecondarySeverity = Quota.SeverityForAvailable(Quota.AvailablePct(secondaryUsed));
            SecondaryResetText = FormatReset(account.SecondaryResetsAt);
            HasSecondaryWindow = true;
            HasWindowBreakdown = true;
        }

        if (account.UsedPercent is double used)
        {
            var avail = Quota.AvailablePct(used);
            AvailablePercent = avail;
            Severity = Quota.SeverityForAvailable(avail);
            AvailableText = Quota.DisplayPct(avail);
            HasPercent = true;
        }

        var automationParts = new List<string> { Name };
        if (HasWindowBreakdown)
        {
            automationParts.Add($"{PrimaryLabel} {PrimaryAvailableText}");
            if (HasPrimaryResetText)
                automationParts.Add($"{I18n.T("quota.resets")} {PrimaryResetText}");
            if (HasSecondaryWindow)
            {
                automationParts.Add($"{SecondaryLabel} {SecondaryAvailableText}");
                if (HasSecondaryResetText)
                    automationParts.Add($"{I18n.T("quota.resets")} {SecondaryResetText}");
            }
        }
        else if (HasSinglePercent)
        {
            automationParts.Add($"{AvailableText} {I18n.T("common.available")}");
        }
        AutomationName = string.Join(", ", automationParts);
    }

    public string Name { get; }
    public bool IsNameHidden { get; }
    public double PrivacyPlaceholderWidth { get; }
    public bool HasPercent { get; }
    public bool HasWindowBreakdown { get; }
    public bool HasSinglePercent => HasPercent && !HasWindowBreakdown;
    public bool HasSecondaryWindow { get; }
    public string PrimaryLabel { get; }
    public string SecondaryLabel { get; }
    public string PrimaryAvailableText { get; } = "";
    public string SecondaryAvailableText { get; } = "";
    public string? PrimaryResetText { get; }
    public string? SecondaryResetText { get; }
    public string? PrimaryResetToolTip => FormatResetToolTip(PrimaryLabel, PrimaryResetText);
    public string? SecondaryResetToolTip => FormatResetToolTip(SecondaryLabel, SecondaryResetText);
    public bool HasPrimaryResetText => !string.IsNullOrWhiteSpace(PrimaryResetText);
    public bool HasSecondaryResetText => !string.IsNullOrWhiteSpace(SecondaryResetText);
    public Severity PrimarySeverity { get; } = Severity.Good;
    public Severity SecondarySeverity { get; } = Severity.Good;
    public double AvailablePercent { get; }
    public Severity Severity { get; } = Severity.Good;
    public string AvailableText { get; } = "";
    public string AutomationName { get; }
    public string AutomationId { get; }

    private static string? FormatReset(string? resetsAt)
    {
        return ResetFormatter.FormatDurationUntil(resetsAt);
    }

    private static string? FormatResetToolTip(string label, string? resetText)
    {
        return string.IsNullOrWhiteSpace(resetText)
            ? null
            : $"{label} {I18n.T("quota.resetsInCompact", "duration", resetText)}";
    }
}
