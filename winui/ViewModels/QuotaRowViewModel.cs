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
        string? resetPrefix = null,
        bool hideSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(window);

        Label = I18n.WindowLabel(window.Label);
        IsQuota = window.Kind == RateWindowKind.Quota;
        var rawValueText = IsQuota
            ? ""
            : window.ValueText ?? window.ResetDescription ?? I18n.T("common.notAvailable");
        IsValueHidden = hideSensitive && window.Sensitivity != RateWindowSensitivity.None;
        ValueText = IsValueHidden
            ? window.Sensitivity == RateWindowSensitivity.Financial
                ? SensitiveDisplay.HiddenBalanceText
                : SensitiveDisplay.HiddenText
            : rawValueText;
        AvailablePercent = Quota.AvailablePct(window.UsedPercent);
        Severity = Quota.SeverityForAvailable(AvailablePercent);
        AvailableText = Quota.DisplayPct(AvailablePercent);
        var reset = Quota.FmtReset(window.ResetsAt);
        ResetText = reset is not null
            ? $"{resetPrefix ?? I18n.T(IsQuota ? "card.resetsIn" : "card.periodEndsIn")} {reset}"
            : IsQuota && !string.IsNullOrWhiteSpace(window.ResetDescription)
                ? window.ResetDescription
                : null;
        IsProminent = prominent;
        AutomationId = $"MetricRow_{new string(Label.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray())}";
        AutomationName = IsQuota
            ? $"{Label}: {AvailableText} {AvailableSuffix}"
            : $"{Label}: {ValueText}";
    }

    public QuotaRowViewModel(
        string label,
        double usedPercent,
        string? resetsAt,
        bool prominent = false,
        string? resetPrefix = null)
        : this(
            new RateWindow
            {
                Label = label,
                UsedPercent = usedPercent,
                ResetsAt = resetsAt,
            },
            prominent,
            resetPrefix)
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

/// <summary>An Antigravity family group ("Claude"/"Gemini"/"Other") + its best row.</summary>
public sealed partial class FamilyGroupViewModel : ObservableObject
{
    public FamilyGroupViewModel(string family, QuotaRowViewModel best)
    {
        Family = I18n.WindowLabel(family);
        Best = best;
    }

    public string Family { get; }
    public QuotaRowViewModel Best { get; }
}

/// <summary>A pooled-account row (codex-lb / antigravity accounts breakdown).</summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    public AccountRowViewModel(AccountInfo account, int index, bool hideSensitive = false)
    {
        AutomationId = $"AccountRow_{index}";
        var accountName = account.Email ?? account.Plan ?? I18n.T("privacy.account", "n", (index + 1).ToString());
        Name = SensitiveDisplay.AccountName(accountName, index, hideSensitive);
        IsNameHidden = hideSensitive && !string.IsNullOrWhiteSpace(account.Email);
        PrivacyPlaceholderWidth = 96 + (index % 3) * 18;
        PrimaryLabel = I18n.WindowLabel(account.PrimaryLabel ?? "5h");
        SecondaryLabel = I18n.WindowLabel(account.SecondaryLabel ?? "Weekly");

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
        return Quota.FmtReset(resetsAt);
    }

    private static string? FormatResetToolTip(string label, string? resetText)
    {
        return string.IsNullOrWhiteSpace(resetText)
            ? null
            : $"{label} {I18n.T("card.resetsIn")} {resetText}";
    }
}
