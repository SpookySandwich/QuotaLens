using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.ViewModels;
using Windows.System;

namespace QuotaLens.Views;

/// <summary>Add-provider dialog: pick a provider type; the window owns instance creation.</summary>
public sealed partial class AddProviderDialog : ContentDialog
{
    private const double MinCellWidth = 156;
    private const int MaxColumns = 3;
    private const double RowHeight = 48;

    // The results area gets a FIXED height so the dialog does not resize while the
    // user types. These bound it against short screens and tall windows alike.
    private const double ResultsChromeAllowance = 240;
    private const double MinResultsHeight = 208;
    private const double MaxResultsHeight = 424;

    // A ContentDialog sizes to its content, and a grid of fixed-width cells asks for
    // nothing in particular — so without this the dialog sits at its minimum width and
    // silently renders two columns. Width is claimed from the real window instead.
    private const double DialogChromeAllowance = 96;
    private const double MinContentWidth = 352;
    private const double MaxContentWidth = 528;

    private readonly IReadOnlyList<ProviderAddOption> _options;
    private readonly CollectionViewSource _groupedOptions = new()
    {
        IsSourceGrouped = true,
        ItemsPath = new PropertyPath("Items"),
    };

    private IReadOnlyList<ProviderAddOption> _visible = Array.Empty<ProviderAddOption>();

    public AddProviderDialog(IReadOnlyList<ProviderInstance>? existingInstances = null)
    {
        InitializeComponent();

        Title = I18n.T("addProvider.title");
        CloseButtonText = I18n.T("common.cancel");
        SearchBox.PlaceholderText = I18n.T("addProvider.searchPlaceholder");
        NoResultsText.Text = I18n.T("addProvider.noMatches");
        NoResultsHint.Text = I18n.T("addProvider.noMatchesHint");
        ClearSearchLink.Content = I18n.T("addProvider.clearSearch");
        ToolTipService.SetToolTip(ClearSearchButton, I18n.T("addProvider.clearSearch"));
        AutomationProperties.SetName(ClearSearchButton, I18n.T("addProvider.clearSearch"));

        _options = ProviderAddOptions.Build(Catalog.AddableTypes, existingInstances);
        Refresh();
    }

    public ProviderType? SelectedProviderType { get; private set; }

    private void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // Sizing waits for Opened: XamlRoot is not live before the dialog is shown.
        if (XamlRoot is not null)
        {
            PickerRoot.Width = Math.Clamp(
                XamlRoot.Size.Width - DialogChromeAllowance,
                MinContentWidth,
                MaxContentWidth);

            ResultsHost.Height = Math.Clamp(
                XamlRoot.Size.Height - ResultsChromeAllowance,
                MinResultsHeight,
                MaxResultsHeight);
        }

        // Typing is the primary way to navigate 49 providers.
        SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Fills the track exactly with whole columns. A fixed ItemWidth silently drops
    /// to two columns when the dialog is a pixel narrower than assumed, which is
    /// what makes a picker like this feel cramped for no visible reason.
    /// </summary>
    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TypeGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
            return;

        var track = TypeGrid.ActualWidth - TypeGrid.Padding.Left - TypeGrid.Padding.Right;
        if (track <= 0)
            return;

        var columns = Math.Clamp((int)(track / MinCellWidth), 1, MaxColumns);
        panel.ItemWidth = Math.Floor(track / columns);
        panel.ItemHeight = RowHeight;
    }

    private void OnTypeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProviderAddOption option)
            Commit(option);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                FocusFirstItem();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                // Enter is only armed while filtering; see Refresh().
                if (TypeGrid.SelectedItem is ProviderAddOption selected)
                    Commit(selected);
                e.Handled = true;
                break;

            case VirtualKey.Escape when SearchBox.Text.Length > 0:
                // Clear the query first; a second Escape closes the dialog. Never both.
                SearchBox.Text = "";
                e.Handled = true;
                break;
        }
    }

    private void OnGridKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            SearchBox.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (e.Key != VirtualKey.Up)
            return;

        // Only the first row hands focus back to the search field; deeper rows keep
        // the grid's native 2-D navigation.
        if (TypeGrid.SelectedIndex >= 0 && TypeGrid.SelectedIndex < ColumnCount())
        {
            SearchBox.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    /// <summary>Typing anywhere in the grid continues the query instead of stranding the user.</summary>
    private void OnGridCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (char.IsControl(args.Character))
            return;

        SearchBox.Text += args.Character;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectionStart = SearchBox.Text.Length;
        args.Handled = true;
    }

    private void Commit(ProviderAddOption option)
    {
        SelectedProviderType = option.Type;
        Hide();
    }

    private void Refresh()
    {
        var query = SearchBox.Text ?? "";
        var hasQuery = !string.IsNullOrWhiteSpace(query);

        _visible = hasQuery
            ? ProviderAddOptions.FilterRanked(_options, query)
            : _options;

        var groups = hasQuery
            ? SingleResultsGroup(_visible)
            : IdleGroups(_options);

        // Setting Source mints a new View, so re-assign ItemsSource each time.
        _groupedOptions.Source = groups;
        TypeGrid.ItemsSource = _groupedOptions.View;

        // Arm Enter only while filtering: pressing it straight after opening must not
        // commit an arbitrary provider.
        TypeGrid.SelectedIndex = hasQuery && _visible.Count > 0 ? 0 : -1;
        if (TypeGrid.SelectedIndex == 0)
            TypeGrid.ScrollIntoView(_visible[0], ScrollIntoViewAlignment.Leading);

        var empty = _visible.Count == 0;
        NoResultsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TypeGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IReadOnlyList<ProviderAddGroup> SingleResultsGroup(
        IReadOnlyList<ProviderAddOption> matches) =>
        matches.Count == 0
            ? Array.Empty<ProviderAddGroup>()
            : new[]
            {
                new ProviderAddGroup(
                    "Results",
                    I18n.T("addProvider.group.results"),
                    "",
                    matches),
            };

    private static IReadOnlyList<ProviderAddGroup> IdleGroups(IReadOnlyList<ProviderAddOption> options)
    {
        var groups = new List<ProviderAddGroup>();
        if (ProviderAddOptions.SuggestedGroup(options) is { } suggested)
            groups.Add(suggested);

        // One flat, alphabetical list — no source/setup categorization. Users care about
        // their data, not how a provider authenticates.
        groups.Add(new ProviderAddGroup(
            "All",
            I18n.T("addProvider.group.all"),
            "",
            options.OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToArray()));
        return groups;
    }

    private int ColumnCount() =>
        TypeGrid.ItemsPanelRoot is ItemsWrapGrid { ItemWidth: > 0 } panel
            ? Math.Max(1, (int)(TypeGrid.ActualWidth / panel.ItemWidth))
            : 1;

    private void FocusFirstItem()
    {
        TypeGrid.UpdateLayout();
        if (TypeGrid.ContainerFromIndex(0) is GridViewItem item)
            item.Focus(FocusState.Keyboard);
    }
}
