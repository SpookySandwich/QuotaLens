using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using QuotaLens.Core;
using QuotaLens.Helpers;
using Windows.UI;

namespace QuotaLens.ViewModels;

/// <summary>
/// Top-level dashboard VM. Wraps <see cref="IProviderService"/>, exposes a sorted
/// ObservableCollection of provider cards + the hero summary, and marshals all
/// service events onto the UI thread (the window DispatcherQueue).
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    internal const string HideSensitiveInfoConfigKey = "hide_sensitive_info";

    private readonly IProviderService _svc;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, ProviderItemViewModel> _byId = new();
    private readonly ProviderSortOrderCache<ProviderItemViewModel> _sortOrderCache;
    private IReadOnlyList<ProviderSortTerm> _sortPriorityOrder;
    private bool _deprioritizeEmptyProviders;

    public DashboardViewModel(IProviderService svc, DispatcherQueue dispatcher)
    {
        _svc = svc;
        _dispatcher = dispatcher;
        _sortPriorityOrder = ProviderSortPriorityOrder.FromConfig(_svc.Config);
        _deprioritizeEmptyProviders = ProviderSortPolicy.DeprioritizeEmptyProvidersFromConfig(_svc.Config);
        // Restored before the first build so masked values never flash unmasked:
        // someone who hid emails to screen-share expects it to stay hidden.
        IsSensitiveInfoHidden = _svc.Config.GetBool(HideSensitiveInfoConfigKey);
        _sortOrderCache = new ProviderSortOrderCache<ProviderItemViewModel>(
            vm => vm.InstanceId,
            vm => vm.Priority);

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        AddProviderCommand = new RelayCommand(() => AddProviderRequested?.Invoke(this, EventArgs.Empty));
        ToggleSensitiveInfoCommand = new RelayCommand(() => IsSensitiveInfoHidden = !IsSensitiveInfoHidden);
        SetSortModeCommand = new RelayCommand<ProviderSortMode>(SetSortMode);
        EditProviderCommand = new RelayCommand<ProviderItemViewModel>(vm =>
        {
            if (vm != null) EditProviderRequested?.Invoke(this, vm);
        });

        BuildItems();
        UpdateHero();

        _svc.SnapshotUpdated += OnSnapshotUpdated;
        _svc.RefreshingChanged += OnRefreshingChanged;
        _svc.InstancesChanged += OnInstancesChanged;
    }

    public IProviderService Service => _svc;

    public HeroViewModel Hero { get; } = new();

    public ObservableCollection<ProviderItemViewModel> Providers { get; } = new();

    public IAsyncRelayCommand RefreshAllCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand AddProviderCommand { get; }
    public IRelayCommand ToggleSensitiveInfoCommand { get; }
    public IRelayCommand<ProviderSortMode> SetSortModeCommand { get; }
    public IRelayCommand<ProviderItemViewModel> EditProviderCommand { get; }

    [ObservableProperty] public partial bool IsRefreshingAll { get; set; }
    [ObservableProperty] public partial bool IsSensitiveInfoHidden { get; set; }
    [ObservableProperty] public partial bool IsProviderGridMultiColumn { get; set; }
    [ObservableProperty] public partial ProviderSortMode SortMode { get; set; } = ProviderSortMode.FiveHour;

    private Color _ambientTintColor = TransparentColor();
    private string _ambientProviderType = "";

    public Color AmbientTintColor
    {
        get => _ambientTintColor;
        private set => SetProperty(ref _ambientTintColor, value);
    }

    public string AmbientProviderType
    {
        get => _ambientProviderType;
        private set => SetProperty(ref _ambientProviderType, value);
    }

    // UI-layer hooks (the window subscribes to drive dialogs / toasts).
    public event EventHandler? SettingsRequested;
    public event EventHandler? AddProviderRequested;
    public event EventHandler<ProviderItemViewModel>? EditProviderRequested;
    public event EventHandler<ProviderItemViewModel>? DeleteProviderRequested;

    // i18n strings for the chrome.
    public string AppTitle => I18n.T("app.title");
    public string FooterQuotaBars => I18n.T("footer.quotaBars");
    public string FooterBalances => I18n.T("footer.balances");
    public string SettingsTitle => I18n.T("settings.title");
    public string RefreshAllTitle => I18n.T("common.refreshAll");
    public string SensitiveInfoTitle => IsSensitiveInfoHidden
        ? I18n.T("privacy.showSensitive")
        : I18n.T("privacy.hideSensitive");
    public string SensitiveInfoGlyph => IsSensitiveInfoHidden ? "\uED1A" : "\uE890";
    public string AddProviderTitle => I18n.T("addProvider.title");
    public string SortTitle => I18n.T("sort.title");
    public string SortBy5hTitle => I18n.T("sort.5h");
    public string SortByWeeklyTitle => I18n.T("sort.weekly");
    public string SortByMonthlyTitle => I18n.T("sort.monthly");
    public string SortByValueTitle => I18n.T("sort.value");
    public string CurrentSortTitle => SortMode switch
    {
        ProviderSortMode.Weekly => SortByWeeklyTitle,
        ProviderSortMode.Monthly => SortByMonthlyTitle,
        ProviderSortMode.PlanValue => SortByValueTitle,
        _ => SortBy5hTitle,
    };
    public bool IsFiveHourSort => SortMode == ProviderSortMode.FiveHour;
    public bool IsWeeklySort => SortMode == ProviderSortMode.Weekly;
    public bool IsMonthlySort => SortMode == ProviderSortMode.Monthly;
    public bool IsValueSort => SortMode == ProviderSortMode.PlanValue;
    public bool IsUsageTimelineVisible => UsageTimelineVisibleFor(Hero.HasUsageTimeline, IsProviderGridMultiColumn);

    public static bool UsageTimelineVisibleFor(bool hasUsageTimeline, bool isProviderGridMultiColumn) =>
        hasUsageTimeline && isProviderGridMultiColumn;

    public Task RefreshAllAsync()
    {
        IsRefreshingAll = true;
        return _svc.RefreshAllAsync().ContinueWith(_ =>
            _dispatcher.TryEnqueue(() => IsRefreshingAll = false));
    }

    private void BuildItems()
    {
        Providers.Clear();
        _byId.Clear();
        foreach (var inst in _svc.Instances)
        {
            var vm = new ProviderItemViewModel(_svc, inst);
            vm.SetSensitiveHidden(IsSensitiveInfoHidden);
            vm.EditRequested += (_, item) => EditProviderRequested?.Invoke(this, item);
            vm.DeleteRequested += (_, item) => DeleteProviderRequested?.Invoke(this, item);
            _byId[inst.Id] = vm;
            Providers.Add(vm);
        }
        RebuildSortOrderCache();
        ApplyCachedSortOrder();
    }

    private void OnSnapshotUpdated(object? sender, ProviderSnapshot snap)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_byId.TryGetValue(snap.ProviderId, out var vm))
            {
                vm.Update(snap, _svc.IsRefreshing(snap.ProviderId));
                RebuildSortOrderCache();
                ApplyCachedSortOrder();
                UpdateHero();
            }
        });
    }

    private void OnRefreshingChanged(object? sender, string id)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_byId.TryGetValue(id, out var vm))
                vm.Update(_svc.GetSnapshot(id), _svc.IsRefreshing(id));
            IsRefreshingAll = _svc.Instances.Any(i => _svc.IsRefreshing(i.Id));
            UpdateAmbientTint();
        });
    }

    private void OnInstancesChanged(object? sender, EventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            BuildItems();
            UpdateHero();
        });
    }

    /// <summary>Sort by the selected dashboard policy while preserving stable ties.</summary>
    private void RebuildSortOrderCache()
    {
        _sortOrderCache.Rebuild(Providers, _sortPriorityOrder, _deprioritizeEmptyProviders);
    }

    private void ApplyCachedSortOrder()
    {
        if (!_sortOrderCache.HasOrder(SortMode))
            RebuildSortOrderCache();

        var orderedItems = ItemsForCachedOrder(_sortOrderCache.OrderFor(SortMode));
        var orderChanged = orderedItems.Where((vm, index) => !ReferenceEquals(Providers[index], vm)).Any();
        if (!orderChanged)
        {
            UpdateAmbientTint();
            return;
        }

        for (var i = 0; i < orderedItems.Count; i++)
        {
            var current = Providers.IndexOf(orderedItems[i]);
            if (current != i)
                Providers.Move(current, i);
        }

        UpdateAmbientTint();
    }

    private IReadOnlyList<ProviderItemViewModel> ItemsForCachedOrder(IReadOnlyList<string> cachedOrder)
    {
        var orderedItems = new List<ProviderItemViewModel>(Providers.Count);
        var seen = new HashSet<string>();

        foreach (var id in cachedOrder)
        {
            if (!_byId.TryGetValue(id, out var vm) || !seen.Add(id))
                continue;

            orderedItems.Add(vm);
        }

        foreach (var vm in Providers)
        {
            if (seen.Add(vm.InstanceId))
                orderedItems.Add(vm);
        }

        return orderedItems;
    }

    public void Dispose()
    {
        _svc.SnapshotUpdated -= OnSnapshotUpdated;
        _svc.RefreshingChanged -= OnRefreshingChanged;
        _svc.InstancesChanged -= OnInstancesChanged;
    }

    public void RefreshLaunchAvailability()
    {
        foreach (var provider in Providers)
            provider.RefreshLaunchAvailability();
    }

    /// <summary>Re-evaluates every i18n-derived property after the language changes.</summary>
    public void RefreshLanguageTexts()
    {
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(FooterQuotaBars));
        OnPropertyChanged(nameof(FooterBalances));
        OnPropertyChanged(nameof(SettingsTitle));
        OnPropertyChanged(nameof(RefreshAllTitle));
        OnPropertyChanged(nameof(SensitiveInfoTitle));
        OnPropertyChanged(nameof(SensitiveInfoGlyph));
        OnPropertyChanged(nameof(AddProviderTitle));
        OnPropertyChanged(nameof(SortTitle));
        OnPropertyChanged(nameof(SortBy5hTitle));
        OnPropertyChanged(nameof(SortByWeeklyTitle));
        OnPropertyChanged(nameof(SortByMonthlyTitle));
        OnPropertyChanged(nameof(SortByValueTitle));
        OnPropertyChanged(nameof(CurrentSortTitle));
        BuildItems();
        UpdateHero();
    }

    public void RefreshSortPriority()
    {
        _sortPriorityOrder = ProviderSortPriorityOrder.FromConfig(_svc.Config);
        _deprioritizeEmptyProviders = ProviderSortPolicy.DeprioritizeEmptyProvidersFromConfig(_svc.Config);
        RebuildSortOrderCache();
        ApplyCachedSortOrder();
        UpdateHero();
    }

    public void RemoveProvider(ProviderItemViewModel item)
    {
        if (!_byId.ContainsKey(item.InstanceId))
            return;

        _svc.RemoveInstance(item.InstanceId);
    }

    private void UpdateAmbientTint()
    {
        AmbientProviderType = AmbientProviderTypeFor(
            Providers.Select(provider => provider.ProviderType),
            Hero.HasPick,
            Hero.PickProviderType);

        AmbientTintColor = string.IsNullOrEmpty(AmbientProviderType)
            ? TransparentColor()
            : Brand.Color(AmbientProviderType);
    }

    public static string AmbientProviderTypeFor(
        IEnumerable<string> sortedProviderTypes,
        bool heroHasPick,
        string heroPickProviderType) =>
        sortedProviderTypes.FirstOrDefault()
        ?? (heroHasPick ? heroPickProviderType : "");

    private void UpdateHero()
    {
        Hero.Update(_svc, _sortPriorityOrder, IsSensitiveInfoHidden, SortMode);
        OnPropertyChanged(nameof(IsUsageTimelineVisible));
        UpdateAmbientTint();
    }

    private void SetSortMode(ProviderSortMode mode)
    {
        if (SortMode == mode)
            return;

        SortMode = mode;
    }

    partial void OnSortModeChanged(ProviderSortMode value)
    {
        OnPropertyChanged(nameof(IsFiveHourSort));
        OnPropertyChanged(nameof(IsWeeklySort));
        OnPropertyChanged(nameof(IsMonthlySort));
        OnPropertyChanged(nameof(IsValueSort));
        OnPropertyChanged(nameof(CurrentSortTitle));
        ApplyCachedSortOrder();
        UpdateHero();
    }

    partial void OnIsSensitiveInfoHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(SensitiveInfoTitle));
        OnPropertyChanged(nameof(SensitiveInfoGlyph));
        foreach (var provider in Providers)
            provider.SetSensitiveHidden(value);
        UpdateHero();
        PersistSensitiveInfoPreference(value);
    }

    private void PersistSensitiveInfoPreference(bool value)
    {
        if (_svc.Config is not IConfigService config)
            return;

        config.Set(HideSensitiveInfoConfigKey, value ? "true" : "false");
        _ = config.SaveAsync();
    }

    partial void OnIsProviderGridMultiColumnChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUsageTimelineVisible));
    }

    private static Color TransparentColor() =>
        Color.FromArgb(0, 0, 0, 0);
}
