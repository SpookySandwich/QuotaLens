using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.Services;

namespace QuotaLens.ViewModels;

public enum CardKind
{
    Skeleton,      // snapshot == null
    NotConfigured, // error AND required-fields blank
    Error,         // error (generic)
    Balance,       // planSortOrder < 0
    Rate,          // standard rate-limit card
}

/// <summary>
/// One provider card. Recomputed from the latest snapshot + config; exposes a
/// CardKind plus the derived display fields and per-card commands. The view uses
/// a DataTemplateSelector keyed on <see cref="Kind"/>.
/// </summary>
public sealed partial class ProviderItemViewModel : ObservableObject
{
    private readonly IProviderService _svc;
    private ProviderSnapshot? _lastSnapshot;
    private bool _lastRefreshing;
    private bool _isSensitiveHidden;

    public ProviderItemViewModel(IProviderService svc, ProviderInstance instance)
    {
        _svc = svc;
        InstanceId = instance.Id;
        ProviderType = instance.Type;
        DefaultName = string.IsNullOrWhiteSpace(instance.Name)
            ? I18n.ProviderName(instance.Type, Catalog.ProviderName(instance.Type))
            : instance.Name;
        Name = DefaultName;

        HasSettings = Catalog.IsAddableProviderType(ProviderType)
            || (Catalog.Fields.TryGetValue(ProviderType, out var fields) && fields.Length > 0);
        RefreshLaunchAvailability();

        RefreshCommand = new AsyncRelayCommand(() => _svc.RefreshAsync(InstanceId));
        LaunchCommand = new RelayCommand(() => _svc.LaunchIde(InstanceId));
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(this, this));
        EditCommand = new RelayCommand(
            () => EditRequested?.Invoke(this, this),
            () => HasSettings);

        Update(_svc.GetSnapshot(InstanceId), _svc.IsRefreshing(InstanceId));
    }

    /// Raised when the user taps Edit / Add credentials on this card.
    public event EventHandler<ProviderItemViewModel>? EditRequested;
    public event EventHandler<ProviderItemViewModel>? DeleteRequested;

    public string InstanceId { get; }
    public string ProviderType { get; }
    public string DefaultName { get; }

    public bool HasSettings { get; }
    public string CardAutomationId => $"ProviderCard_{InstanceId}";
    public string LaunchAutomationId => $"Launch_{InstanceId}";
    public string EditAutomationId => $"Edit_{InstanceId}";
    public string DeleteAutomationId => $"Delete_{InstanceId}";
    public string RefreshAutomationId => $"Refresh_{InstanceId}";
    public string LaunchText => I18n.T("ide.launch");
    public string LaunchAutomationName => I18n.T("ide.launchTitle", "name", IdeName);
    public string EditToolTip => I18n.T("settings.edit");
    public string RefreshToolTip => I18n.T("common.refresh");
    public string EditAutomationName => $"{I18n.T("settings.edit")} {Name}";
    public string DeleteAutomationName => I18n.T("provider.removeAutomationName", "name", Name);
    public string RefreshAutomationName => $"{I18n.T("common.refresh")} {Name}";

    // Brand identity (color dot).
    public Microsoft.UI.Xaml.Media.Brush BrandBrush => Brand.Brush(ProviderType);

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand LaunchCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand EditCommand { get; }

    /// Legacy numeric sort value retained for diagnostics; dashboard sorting uses Priority.
    public double SortValue { get; private set; } = -1;

    public ProviderPriorityScore Priority { get; private set; }

    [ObservableProperty] public partial CardKind Kind { get; set; } = CardKind.Skeleton;
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial bool IsRefreshing { get; set; }
    [ObservableProperty] public partial bool IsTimelineHighlighted { get; set; }

    // Headline availability (for the colored % + bar on rate cards).
    [ObservableProperty] public partial double AvailablePercent { get; set; }
    [ObservableProperty] public partial Severity Severity { get; set; } = Severity.Good;
    [ObservableProperty] public partial string AvailableText { get; set; } = "";

    // Card footer.
    [ObservableProperty] public partial string FooterTime { get; set; } = "";
    [ObservableProperty] public partial bool IsStale { get; set; }
    [ObservableProperty] public partial string? FooterReset { get; set; }

    [ObservableProperty] public partial string? ErrorText { get; set; }

    // Balance card.
    [ObservableProperty] public partial string BalanceAmount { get; set; } = "";
    [ObservableProperty] public partial string BalanceCurrency { get; set; } = "USD";
    [ObservableProperty] public partial string? BalancePaid { get; set; }

    // Inline balances on rate cards (CNY / credits).
    [ObservableProperty] public partial string? InlineBalance { get; set; }
    [ObservableProperty] public partial string? InlineBalanceDetail { get; set; }

    public ObservableCollection<QuotaRowViewModel> Rows { get; } = new();
    public ObservableCollection<FamilyGroupViewModel> Families { get; } = new();
    public ObservableCollection<AccountRowViewModel> Accounts { get; } = new();

    private bool _canLaunch;
    private string _ideName = "";
    private string? _launchIconPath;
    private Uri? _launchIconUri;
    private bool _hasNamePrivacyPlaceholder;

    public bool CanLaunch
    {
        get => _canLaunch;
        private set => SetProperty(ref _canLaunch, value);
    }

    public string IdeName
    {
        get => _ideName;
        private set
        {
            if (SetProperty(ref _ideName, value))
                OnPropertyChanged(nameof(LaunchAutomationName));
        }
    }

    public Uri? LaunchIconUri
    {
        get => _launchIconUri;
        private set
        {
            if (SetProperty(ref _launchIconUri, value))
                OnPropertyChanged(nameof(HasLaunchIcon));
        }
    }

    public bool HasLaunchIcon => LaunchIconUri != null;
    public bool IsSensitiveHidden
    {
        get => _isSensitiveHidden;
        private set => SetProperty(ref _isSensitiveHidden, value);
    }

    public bool HasNamePrivacyPlaceholder
    {
        get => _hasNamePrivacyPlaceholder;
        private set => SetProperty(ref _hasNamePrivacyPlaceholder, value);
    }

    public bool HasFamilies => Families.Count > 0;
    public bool HasAccounts => Accounts.Count > 0;
    public bool HasRows => Rows.Count > 0;

    // i18n strings exposed for x:Bind.
    public string ConnectingText => I18n.T("card.connecting");
    public string PendingText => I18n.T("summary.pending");
    public string NotConfiguredTitle => I18n.T("card.notConfigured");
    public string NotConfiguredDetail => I18n.T("card.notConfiguredDetail");
    public string AddCredentialsText => I18n.T("card.addCredentials");
    public string NeedsAttentionText => I18n.T("card.needsAttention");
    public string StaleText => I18n.T("card.stale");
    public string StaleHint => I18n.T("card.staleHint");

    public bool HasError => Kind is CardKind.Error;
    public bool IsShimmerLoadingActive => IsRefreshing;
    public int TimelineHighlightPulseVersion { get; private set; }

    /// "Updated" normally, "Last attempt" on an error card.
    public string UpdatedLabel => Kind is CardKind.Error ? I18n.T("card.lastAttempt") : I18n.T("card.updated");

    public void PulseTimelineHighlight()
    {
        TimelineHighlightPulseVersion++;
        OnPropertyChanged(nameof(TimelineHighlightPulseVersion));
    }

    public void RefreshLaunchAvailability()
    {
        var launchTarget = Catalog.LaunchTargetFor(ProviderType, _svc.Config);
        var configuredPath = launchTarget?.ConfigKey is null
            ? null
            : _svc.Config.Get(launchTarget.ConfigKey);
        var canResolveLaunchPath = launchTarget != null
            && IdeLauncher.TryResolveLaunchPath(ProviderType, launchTarget, configuredPath, out _);

        CanLaunch = canResolveLaunchPath;
        IdeName = !canResolveLaunchPath || launchTarget == null
            ? ""
            : launchTarget.ConfigKey == Catalog.DefaultLaunchEditorPathKey
                ? I18n.T("ide.defaultEditor")
                : launchTarget.DisplayName;

        var iconPath = !canResolveLaunchPath || launchTarget == null
            ? null
            : LaunchIconService.GetOrCreateIconPath(ProviderType, launchTarget, configuredPath);
        SetLaunchIconPath(iconPath);
    }

    public void SetSensitiveHidden(bool hidden)
    {
        if (IsSensitiveHidden == hidden)
            return;

        IsSensitiveHidden = hidden;
        Update(_lastSnapshot, _lastRefreshing);
    }

    private void SetLaunchIconPath(string? iconPath)
    {
        if (_launchIconPath == iconPath)
            return;

        _launchIconPath = iconPath;
        LaunchIconUri = string.IsNullOrWhiteSpace(iconPath)
            ? null
            : new Uri(Path.GetFullPath(iconPath));
    }

    private void SetDisplayName(string rawName)
    {
        HasNamePrivacyPlaceholder = IsSensitiveHidden && SensitiveDisplay.ContainsSensitiveText(rawName);
        Name = SensitiveDisplay.ProviderName(rawName, IsSensitiveHidden);
    }

    /// <summary>Recompute everything from a fresh snapshot. Call on the UI thread.</summary>
    public void Update(ProviderSnapshot? snap, bool refreshing)
    {
        _lastSnapshot = snap;
        _lastRefreshing = refreshing;

        RefreshLaunchAvailability();
        IsRefreshing = refreshing;

        if (snap == null)
        {
            Kind = CardKind.Skeleton;
            SetDisplayName(DefaultName);
            SortValue = -1;
            Priority = ProviderPriority.Score(InstanceId, null, _svc.Config);
            ClearCollections();
            return;
        }

        SetDisplayName(DisplayNameFor(snap));

        var hasError = !string.IsNullOrEmpty(snap.Error);
        Priority = ProviderPriority.Score(InstanceId, snap, _svc.Config);

        // Footer time + stale.
        FooterTime = snap.UpdatedAt.ToLocalTime().ToString("HH:mm");
        IsStale = !hasError && Quota.IsStale(snap.UpdatedAt, _svc.Config.RefreshMs);

        if (hasError)
        {
            // Distinguish "not configured" from a real error.
            if (Catalog.IsProviderUnconfigured(InstanceId, _svc.Config))
            {
                Kind = CardKind.NotConfigured;
                SetDisplayName(DefaultName);
                SortValue = -1;
                Priority = ProviderPriority.Score(InstanceId, null, _svc.Config);
                ClearCollections();
                return;
            }

            Kind = CardKind.Error;
            SortValue = -1;
            Priority = ProviderPriority.Score(InstanceId, null, _svc.Config);
            var err = _isSensitiveHidden
                ? SensitiveDisplay.MaskEmails(snap.Error!)
                : snap.Error!;
            var localizedError = I18n.LocalizeErrorMessage(err);
            ErrorText = localizedError.Length > 80 ? localizedError[..77] + "..." : localizedError;
            FooterReset = null;
            ClearCollections();
            return;
        }

        // Healthy snapshot → compute sort + headline availability.
        var availability = Priority.Availability;
        SortValue = Priority.PlanValue * 1000 + availability;

        ClearCollections();

        if (Priority.IsPayAsYouGo && !HasRateContent(snap))
        {
            // Balance-only card (pay-as-you-go providers without quota windows).
            Kind = CardKind.Balance;
            var bal = snap.Balance;
            var symbol = bal?.Currency == "CNY" ? "¥" : "$";
            BalanceAmount = SensitiveDisplay.BalanceAmount(bal != null ? $"{symbol}{bal.Total:0.00}" : "--", _isSensitiveHidden);
            BalanceCurrency = bal?.Currency ?? "USD";
            BalancePaid = SensitiveDisplay.BalanceDetail(bal != null ? $"Paid {symbol}{bal.Paid:0.00}" : null, _isSensitiveHidden);
            FooterReset = null;
            return;
        }

        // Rate-limit card.
        Kind = CardKind.Rate;
        AvailablePercent = availability;
        Severity = Quota.SeverityForAvailable(availability);
        AvailableText = Quota.DisplayPct(availability);

        BuildRateContent(snap);
    }

    private string DisplayNameFor(ProviderSnapshot snapshot) =>
        ProviderSnapshotIdentity.ComposeTitle(ProviderType, DefaultName, snapshot);

    private void BuildRateContent(ProviderSnapshot snap)
    {
        // Antigravity family grouping (when it has model quotas).
        if (ProviderType == "antigravity"
            && snap.ModelQuotas.Count > 0
            && snap.AdditionalWindows.Count == 0)
        {
            var showOther = GetScopedBool("show_antigravity_other_quotas");
            var groups = snap.ModelQuotas
                .Where(q => showOther || q.Family != "Other")
                .GroupBy(q => q.Family)
                .Select(g =>
                {
                    var best = g.Aggregate((a, b) => a.RemainingPercent > b.RemainingPercent ? a : b);
                    var label = StripParen(best.Model);
                    return new FamilyGroupViewModel(
                        g.Key,
                        new QuotaRowViewModel(new RateWindow
                        {
                            Label = label,
                            UsedPercent = best.UsedPercent,
                            ResetsAt = best.ResetsAt,
                        }, hideSensitive: _isSensitiveHidden));
                })
                .ToList();

            if (groups.Count > 0)
            {
                foreach (var grp in groups) Families.Add(grp);
                OnPropertyChanged(nameof(HasFamilies));
                FooterReset = null; // families render their own resets
                BuildAccountsAndInlineBalance(snap);
                return;
            }
        }

        // Standard primary / secondary / tertiary rows.
        var primaryResetPrefix = ProviderType == "codex-lb" && !string.IsNullOrWhiteSpace(snap.Primary.ResetDescription)
            ? snap.Primary.ResetDescription
            : null;
        Rows.Add(new QuotaRowViewModel(
            snap.Primary,
            prominent: true,
            resetPrefix: primaryResetPrefix,
            hideSensitive: _isSensitiveHidden));
        if (snap.Secondary != null)
            Rows.Add(new QuotaRowViewModel(snap.Secondary, hideSensitive: _isSensitiveHidden));
        if (snap.Tertiary != null)
            Rows.Add(new QuotaRowViewModel(snap.Tertiary, hideSensitive: _isSensitiveHidden));
        foreach (var window in snap.AdditionalWindows)
            Rows.Add(new QuotaRowViewModel(window, hideSensitive: _isSensitiveHidden));
        OnPropertyChanged(nameof(HasRows));

        var reset = snap.Primary.Kind == RateWindowKind.Quota
            ? Quota.FmtReset(snap.Primary.ResetsAt)
            : null;
        FooterReset = ProviderType == "codex-lb"
            ? null
            : reset == null ? null : $"{I18n.T("card.reset")} {reset}";

        BuildAccountsAndInlineBalance(snap);
    }

    private static bool HasRateContent(ProviderSnapshot snap) =>
        snap.Primary.Kind == RateWindowKind.Informational
        || snap.Secondary is not null
        || snap.Tertiary is not null
        || snap.AdditionalWindows.Count > 0
        || snap.ModelQuotas.Count > 0
        || snap.Accounts.Count > 0
        || snap.Primary.WindowMinutes is not null
        || !string.IsNullOrWhiteSpace(snap.Primary.ResetsAt);

    private void BuildAccountsAndInlineBalance(ProviderSnapshot snap)
    {
        // Per-account details remain useful for a single pooled/local account too.
        if (snap.Accounts.Count >= 1)
        {
            for (var i = 0; i < snap.Accounts.Count; i++)
                Accounts.Add(new AccountRowViewModel(snap.Accounts[i], i, _isSensitiveHidden));
            OnPropertyChanged(nameof(HasAccounts));
        }

        // Inline balance lines on rate cards (CNY / credits). A provider that
        // already emits an informational balance row gets component rows instead
        // of repeating the same total below the card.
        var bal = snap.Balance;
        if (bal is null)
            return;

        if (HasInformationalBalanceWindow(snap))
        {
            AddBalanceComponentRows(bal);
            return;
        }

        if (bal.Currency == "CNY")
            InlineBalance = SensitiveDisplay.InlineBalance($"¥{bal.Total:0.00} balance", _isSensitiveHidden);
        else if (bal.Currency == "credits")
        {
            InlineBalance = SensitiveDisplay.InlineBalance($"{bal.Total:#,0} credits remaining", _isSensitiveHidden);
            InlineBalanceDetail = SensitiveDisplay.InlineBalanceDetail($"of {bal.Granted:#,0} total", _isSensitiveHidden);
        }
        else
        {
            var symbol = CurrencySymbol(bal.Currency);
            InlineBalance = SensitiveDisplay.InlineBalance($"{symbol}{bal.Total:0.00} balance", _isSensitiveHidden);
            InlineBalanceDetail = SensitiveDisplay.InlineBalanceDetail(
                BalanceComponentDetail(bal, symbol)
                ?? (bal.Granted > bal.Total ? $"of {symbol}{bal.Granted:0.00} total" : null),
                _isSensitiveHidden);
        }
    }

    private void AddBalanceComponentRows(BalanceInfo balance)
    {
        var symbol = CurrencySymbol(balance.Currency);
        if (!string.IsNullOrWhiteSpace(balance.PaidLabelKey))
        {
            Rows.Add(new QuotaRowViewModel(new RateWindow
            {
                Label = I18n.T(balance.PaidLabelKey),
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Financial,
                ValueText = $"{symbol}{balance.Paid:0.00}",
            }, hideSensitive: _isSensitiveHidden));
        }

        if (!string.IsNullOrWhiteSpace(balance.GrantedLabelKey))
        {
            Rows.Add(new QuotaRowViewModel(new RateWindow
            {
                Label = I18n.T(balance.GrantedLabelKey),
                Kind = RateWindowKind.Informational,
                Sensitivity = RateWindowSensitivity.Financial,
                ValueText = $"{symbol}{balance.Granted:0.00}",
            }, hideSensitive: _isSensitiveHidden));
        }

        OnPropertyChanged(nameof(HasRows));
    }

    private static bool HasInformationalBalanceWindow(ProviderSnapshot snapshot) =>
        BalanceWindows(snapshot).Any(window =>
            window.Kind == RateWindowKind.Informational
            && window.Label.Contains("balance", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<RateWindow> BalanceWindows(ProviderSnapshot snapshot)
    {
        yield return snapshot.Primary;
        if (snapshot.Secondary is not null)
            yield return snapshot.Secondary;
        if (snapshot.Tertiary is not null)
            yield return snapshot.Tertiary;
        foreach (var window in snapshot.AdditionalWindows)
            yield return window;
    }

    private static string? BalanceComponentDetail(BalanceInfo balance, string symbol)
    {
        var components = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(balance.PaidLabelKey))
            components.Add($"{I18n.T(balance.PaidLabelKey)} {symbol}{balance.Paid:0.00}");
        if (!string.IsNullOrWhiteSpace(balance.GrantedLabelKey))
            components.Add($"{I18n.T(balance.GrantedLabelKey)} {symbol}{balance.Granted:0.00}");
        return components.Count == 0 ? null : string.Join(" · ", components);
    }

    private void ClearCollections()
    {
        Rows.Clear();
        Families.Clear();
        Accounts.Clear();
        InlineBalance = null;
        InlineBalanceDetail = null;
        OnPropertyChanged(nameof(HasFamilies));
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>Strip a trailing " (...)" suffix, mirroring model.replace(/\s*\(.*/, "").</summary>
    private static string StripParen(string s)
    {
        var i = s.IndexOf('(');
        if (i < 0) return s;
        return s[..i].TrimEnd();
    }

    private static string CurrencySymbol(string currency) => currency switch
    {
        "USD" => "$",
        "CNY" => "¥",
        "EUR" => "€",
        "GBP" => "£",
        _ => string.IsNullOrWhiteSpace(currency) ? "" : currency + " ",
    };

    partial void OnKindChanged(CardKind value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(UpdatedLabel));
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(EditAutomationName));
        OnPropertyChanged(nameof(DeleteAutomationName));
        OnPropertyChanged(nameof(RefreshAutomationName));
    }

    partial void OnIsRefreshingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsShimmerLoadingActive));
    }

    private bool GetScopedBool(string key, bool fallback = false)
    {
        var value = _svc.Config.GetScoped(InstanceId, key);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            _ => false,
        };
    }

}
