using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.Providers;
using QuotaLens.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace QuotaLens.Views;

/// <summary>
/// Provider edit dialog. Fields are built dynamically from
/// <see cref="Catalog.Fields"/> for the instance's type (TextBox / PasswordBox /
/// file-pick / ToggleSwitch), read+written with scoped config keys (global for
/// <see cref="ProviderField.IsGlobal"/> app-path fields). Done is enabled when
/// the visible fields are valid, then a live fetch must succeed before the
/// dialog closes. Sign-in lives here, not on the card. ContentDialog chrome
/// buttons always dismiss unless cancelled, so in-dialog actions must not live
/// on Primary/Secondary.
/// </summary>
public sealed partial class EditProviderDialog : ContentDialog
{
    // Segoe Fluent / MDL2 glyphs.
    private const string BrowseGlyph = "\uE8E5";
    private const string WarningGlyph = "\uE7BA";

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WarningBrush =
        new(Windows.UI.Color.FromArgb(255, 232, 163, 61));

    /// <summary>Caveat for the selected source, shown under the tabs rather than on them.</summary>
    private readonly StackPanel _sourceNote = new() { Visibility = Visibility.Collapsed };

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush InvalidBrush =
        new(Windows.UI.Color.FromArgb(255, 239, 68, 68));

    private readonly IProviderService _svc;
    private readonly string _instanceId;
    private readonly string _type;
    private readonly nint _hwnd;
    private readonly List<(ProviderField Field, Func<string> Read, Action<string> Write)> _editors = new();
    private readonly List<(Func<bool> IsValid, Func<string?> Error, TextBlock ErrorText)> _errorViews = new();
    private IReadOnlyList<IProviderSource> _sources = Array.Empty<IProviderSource>();
    private string? _selectedSourceId;
    private bool _revealAllErrors;
    private TextBlock? _fetchErrorText;
    private Button? _connectionButton;
    private ProgressRing? _connectionProgress;
    private TextBlock? _connectionProgressText;
    private Func<bool>? _connectionPlacementIsValid;
    private IProviderConnectionAction? _connectionAction;
    private readonly HashSet<string> _verifiedSourceIds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _connectionCts;
    private bool _connecting;
    private ProviderErrorKind? _currentErrorKind;

    public EditProviderDialog(IProviderService svc, string instanceId, string providerType, string displayName, nint hwnd)
    {
        InitializeComponent();
        _svc = svc;
        _instanceId = instanceId;
        _type = providerType;
        _hwnd = hwnd;

        Title = displayName;
        PrimaryButtonText = I18n.T("common.done");
        CloseButtonText = I18n.T("common.cancel");
        IsPrimaryButtonEnabled = false;

        BuildFields();
        PrimaryButtonClick += OnSave;
        Closed += (_, _) =>
        {
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
            _connectionCts = null;
        };
    }

    private void BuildFields()
    {
        FieldsPanel.Children.Clear();
        _editors.Clear();
        _errorViews.Clear();
        _fetchErrorText = null;
        _connectionButton = null;
        _connectionProgress = null;
        _connectionProgressText = null;
        _connectionPlacementIsValid = null;
        _connectionAction = null;
        _currentErrorKind = null;
        _revealAllErrors = false;
        IsPrimaryButtonEnabled = false;

        Catalog.Fields.TryGetValue(_type, out var fields);
        fields ??= Array.Empty<ProviderField>();

        AddSectionHeader(I18n.T("editProvider.connection"), I18n.T("editProvider.connectionHint"));

        var provider = ProviderRegistry.Create(_type);
        _sources = ProviderRegistry.ConnectionSourcesFor(provider);
        if (string.IsNullOrWhiteSpace(_selectedSourceId))
        {
            _selectedSourceId = ProviderRegistry
                .ConfiguredOrDefaultSourceFor(_sources, _instanceId, _svc.Config)
                ?.Mode.ConfigValue();
        }
        if (_sources.Count > 1)
            BuildSourceSelector();
        _connectionAction = SelectedSource()?.ConnectionAction;

        if (fields.Length == 0)
        {
            BuildConnectionAction(() => true);
            BuildFetchErrorView();
            RefreshValidity();
            return;
        }

        var visibleKeys = VisibleFieldKeys(fields);
        foreach (var field in fields)
        {
            if (!visibleKeys.Contains(field.Key))
                continue;

            var current = ReadValue(field);
            var automationId = $"Field_{_instanceId}_{field.Key}";

            if (field.IsToggle)
            {
                var toggle = new ToggleSwitch
                {
                    Header = I18n.FieldLabel(field.Label),
                    IsOn = IsTruthy(current),
                };
                AutomationProperties.SetAutomationId(toggle, automationId);
                toggle.Toggled += (_, _) => OnFieldChanged(field);
                var panel = new StackPanel { Spacing = 2 };
                panel.Children.Add(toggle);
                AddDescription(panel, field.Description);
                FieldsPanel.Children.Add(panel);
                _editors.Add((field, () => toggle.IsOn ? "true" : "false", value => toggle.IsOn = IsTruthy(value)));
                continue;
            }

            // Text-like field: input on the left, a browse button on the right for
            // file paths. The placeholder shows the effective value when the field is
            // left empty: the mapped environment variable (or "from ENV" for secrets),
            // otherwise the static default.
            var grid = new Grid { ColumnSpacing = 6 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var placeholder = EffectivePlaceholder(field);
            Func<string> read;
            Action<string> write;
            Control input;
            if (field.IsPassword)
            {
                var box = new PasswordBox
                {
                    Header = I18n.FieldLabel(field.Label),
                    Password = current,
                    PlaceholderText = placeholder,
                };
                box.PasswordChanged += (_, _) => OnFieldChanged(field);
                read = () => box.Password;
                write = value => box.Password = value;
                input = box;
            }
            else
            {
                var box = new TextBox
                {
                    Header = I18n.FieldLabel(field.Label),
                    Text = current,
                    PlaceholderText = placeholder,
                };
                box.TextChanged += (_, _) => OnFieldChanged(field);
                read = () => box.Text;
                write = value => box.Text = value;
                input = box;
            }

            var isValid = ValidationFor(field, read);

            AutomationProperties.SetAutomationId(input, automationId);
            Grid.SetColumn(input, 0);
            grid.Children.Add(input);

            var trailing = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            if (field.IsFilePath)
                trailing.Children.Add(BuildBrowseButton(field, write));

            if (trailing.Children.Count > 0)
            {
                Grid.SetColumn(trailing, 1);
                grid.Children.Add(trailing);
            }

            var wrap = new StackPanel { Spacing = 4 };
            wrap.Children.Add(grid);
            AddDescription(wrap, field.Description);

            if (field.IsRequired || field.IsFilePath)
            {
                var errorText = new TextBlock
                {
                    Foreground = InvalidBrush,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Visibility = Visibility.Collapsed,
                };
                wrap.Children.Add(errorText);
                _errorViews.Add((isValid, ErrorFor(field, read), errorText));
            }

            FieldsPanel.Children.Add(wrap);
            _editors.Add((field, read, write));

            if (IsConnectionPlacementField(field))
                BuildConnectionAction(isValid);
        }

        // A source may not need a path/URL field. It still participates in the same
        // setup flow, placed safely after its visible fields.
        if (_connectionAction is not null && _connectionButton is null)
            BuildConnectionAction(() => true);

        BuildFetchErrorView();
        RefreshValidity();
    }

    private Button BuildBrowseButton(ProviderField field, Action<string> write)
    {
        var browse = new Button
        {
            Content = new FontIcon { Glyph = BrowseGlyph, FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Bottom,
            Style = (Style)Application.Current.Resources["CardIconButton"],
        };
        var browseName = I18n.T("settings.browse");
        ToolTipService.SetToolTip(browse, browseName);
        AutomationProperties.SetAutomationId(browse, $"Browse_{_instanceId}_{field.Key}");
        AutomationProperties.SetName(browse, browseName);
        browse.Click += async (_, _) =>
        {
            var path = await PickFileAsync();
            if (!string.IsNullOrEmpty(path))
                write(path);
        };
        return browse;
    }

    private static Func<bool> ValidationFor(ProviderField field, Func<string> read)
    {
        if (field.IsRequired)
            return () => !string.IsNullOrWhiteSpace(read());

        if (field.IsFilePath)
            return () => string.IsNullOrWhiteSpace(read()) || File.Exists(read());

        return () => true;
    }

    /// <summary>Per-field error message, or null when the value is acceptable.</summary>
    private static Func<string?> ErrorFor(ProviderField field, Func<string> read)
    {
        if (field.IsRequired)
        {
            if (field.IsFilePath)
            {
                return () => string.IsNullOrWhiteSpace(read())
                    ? I18n.T("editProvider.required")
                    : (!File.Exists(read()) ? I18n.T("editProvider.fileNotFound") : null);
            }

            return () => string.IsNullOrWhiteSpace(read()) ? I18n.T("editProvider.required") : null;
        }

        if (field.IsFilePath)
            return () => string.IsNullOrWhiteSpace(read()) || File.Exists(read())
                ? null
                : I18n.T("editProvider.fileNotFound");

        return () => null;
    }

    /// <summary>
    /// Live-gates Done. Empty required fields stay quiet until a save attempt;
    /// a typed path that does not exist is shown immediately so the disabled
    /// button has a reason.
    /// </summary>
    private async void OnFieldChanged(ProviderField field)
    {
        if (_connectionAction?.RequiresVerifiedData == true
            && _connectionAction.VerificationFieldKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase)
            && SelectedSource() is { } source)
        {
            _verifiedSourceIds.Remove(source.Mode.ConfigValue());
        }
        RefreshValidity();

        var selected = SelectedSource();
        var draftConfig = DraftConfig();
        if (selected?.ConnectionAction?.ShouldConnectAfterConfigChange(
                field.Key,
                _instanceId,
                draftConfig) == true)
        {
            await ConnectAndVerifyAsync(selected, draftConfig);
        }
    }

    private bool RefreshValidity()
    {
        var fileNotFound = I18n.T("editProvider.fileNotFound");
        var valid = true;
        foreach (var (isValid, error, errorText) in _errorViews)
        {
            if (isValid())
            {
                errorText.Text = "";
                errorText.Visibility = Visibility.Collapsed;
                continue;
            }

            valid = false;
            var message = error();
            var show = _revealAllErrors || string.Equals(message, fileNotFound, StringComparison.Ordinal);
            errorText.Text = show ? (message ?? "") : "";
            errorText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        var connectionState = CurrentConnectionState();
        IsPrimaryButtonEnabled = ProviderConnectionStates.CanFinish(
            valid,
            _connectionAction,
            connectionState,
            connectionInProgress: _connecting);
        RefreshConnectionAction(connectionState);
        return valid;
    }

    private void BuildFetchErrorView()
    {
        _fetchErrorText = new TextBlock
        {
            Foreground = InvalidBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        FieldsPanel.Children.Add(_fetchErrorText);
    }

    private bool IsConnectionPlacementField(ProviderField field) =>
        _connectionAction is not null
        && string.Equals(
            field.Key,
            _connectionAction.PlacementFieldKey,
            StringComparison.OrdinalIgnoreCase);

    private void BuildConnectionAction(Func<bool> placementIsValid)
    {
        if (_connectionAction is null || _connectionButton is not null)
            return;

        _connectionPlacementIsValid = placementIsValid;
        var label = I18n.T(_connectionAction.LabelKey);
        _connectionButton = new Button { Content = label };
        AutomationProperties.SetAutomationId(_connectionButton, $"ConnectionAction_{_instanceId}");
        AutomationProperties.SetName(_connectionButton, label);
        _connectionButton.Click += OnConnectionClick;

        var progressLabel = I18n.T(_connectionAction.ProgressLabelKey);
        _connectionProgress = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_connectionProgress, $"ConnectionProgress_{_instanceId}");
        AutomationProperties.SetName(_connectionProgress, progressLabel);

        _connectionProgressText = new TextBlock
        {
            Text = progressLabel,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(
            _connectionProgressText,
            $"ConnectionProgressText_{_instanceId}");

        var progress = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        progress.Children.Add(_connectionProgress);
        progress.Children.Add(_connectionProgressText);

        var row = new StackPanel { Spacing = 8 };
        row.Children.Add(_connectionButton);
        row.Children.Add(progress);
        FieldsPanel.Children.Add(row);
    }

    private IProviderSource? SelectedSource() =>
        _sources.Find(_selectedSourceId) ?? _sources.FirstOrDefault();

    private ProviderConnectionState CurrentConnectionState()
    {
        var source = SelectedSource();
        if (source is null)
            return new ProviderConnectionState(false, false, false, false);

        return ProviderConnectionStates.Evaluate(
            source,
            _instanceId,
            DraftConfig(),
            _svc.Config.GetScoped(_instanceId, ProviderSourceRunner.SourceConfigKey),
            _svc.GetSnapshot(_instanceId),
            _currentErrorKind,
            verifiedInDialog: _verifiedSourceIds.Contains(source.Mode.ConfigValue()),
            singleSource: _sources.Count <= 1);
    }

    private void RefreshConnectionAction(ProviderConnectionState? state = null)
    {
        if (_connectionButton is null || _connectionAction is null)
            return;

        var show = _connectionAction.ShouldOffer(state ?? CurrentConnectionState());
        _connectionButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _connectionButton.IsEnabled = show
            && !_connecting
            && (_connectionPlacementIsValid?.Invoke() ?? true);
        if (_connectionProgress is not null)
        {
            _connectionProgress.IsActive = _connecting;
            _connectionProgress.Visibility = _connecting
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (_connectionProgressText is not null)
        {
            _connectionProgressText.Visibility = _connecting
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private async void OnConnectionClick(object sender, RoutedEventArgs e)
    {
        var source = SelectedSource();
        if (source?.ConnectionAction is null || _connecting)
            return;

        await ConnectAndVerifyAsync(source, DraftConfig());
    }

    private async Task ConnectAndVerifyAsync(IProviderSource source, IConfig draftConfig)
    {
        if (source.ConnectionAction is null || _connecting)
            return;

        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _connecting = true;
        RefreshConnectionAction();
        SetFetchError(null);

        try
        {
            var result = await ProviderConnectionCoordinator.ConnectAndVerifyAsync(
                    source,
                    _instanceId,
                    draftConfig,
                    _connectionCts.Token)
                .ConfigureAwait(true);
            if (result.Verified)
            {
                _verifiedSourceIds.Add(source.Mode.ConfigValue());
                SetFetchError(null);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancel/close is not a connection error.
        }
        catch (Exception error)
        {
            SetFetchError(
                I18n.LocalizeErrorMessage(error.Message),
                error is ProviderException providerError
                    ? providerError.Kind
                    : ProviderErrorKind.Unknown);
        }
        finally
        {
            _connecting = false;
            RefreshValidity();
        }
    }

    /// <summary>
    /// Placeholder for an empty field: the effective value QuotaLens will use when
    /// the field is left empty. A set environment variable wins (its value for plain
    /// fields, "from ENV" for secrets), otherwise the static default.
    /// </summary>
    private string EffectivePlaceholder(ProviderField field)
    {
        // A global app-path field is symbolic: it shows the auto-detected executable
        // as its placeholder, so the user sees what will launch without entering a path.
        if (field.IsGlobal && field.IsFilePath)
        {
            var target = Catalog.LaunchTargetFor(_type, _svc.Config);
            if (target is not null &&
                string.Equals(target.ConfigKey, field.Key, StringComparison.OrdinalIgnoreCase))
            {
                if (IdeLauncher.TryResolveLaunchPath(_type, target, null, out var detected))
                    return detected;
                if (target.DefaultPaths.Length > 0)
                    return target.DefaultPaths[0];
            }
        }

        foreach (var envKey in ProviderConfig.EnvironmentKeysFor(_type, field.Key))
        {
            var envValue = ProviderConfig.Environment(envKey);
            if (envValue is null)
                continue;
            return field.IsPassword ? $"from {envKey}" : envValue;
        }

        return field.Placeholder;
    }

    /// <summary>
    /// For multi-source providers, an App / CLI / Web selector so the user can choose
    /// which data origin this instance uses. Stored per instance as "provider_source".
    /// </summary>
    private void BuildSourceSelector()
    {
        var selector = new Segmented { Header = I18n.T("editProvider.source") };
        foreach (var source in _sources)
        {
            var item = new SegmentedItem
            {
                Content = source.Mode.DisplayName(),
                Tag = source.Mode.ConfigValue(),
            };
            AutomationProperties.SetAutomationId(
                item,
                $"Source_{_instanceId}_{source.Mode.ConfigValue()}");
            AutomationProperties.SetName(item, source.Mode.DisplayName());
            selector.Items.Add(item);
        }

        var selected = 0;
        for (var index = 0; index < _sources.Count; index++)
        {
            if (_sources[index].MatchesConfigValue(_selectedSourceId))
            {
                selected = index;
                break;
            }
        }
        selector.SelectedIndex = selected;

        selector.SelectionChanged += (_, _) =>
        {
            _selectedSourceId = (selector.SelectedItem as SegmentedItem)?.Tag?.ToString();
            BuildSourceNote(_sources.ElementAtOrDefault(selector.SelectedIndex));
            BuildFields();
        };

        AutomationProperties.SetAutomationId(selector, $"Source_{_instanceId}");
        AutomationProperties.SetName(selector, I18n.T("editProvider.source"));
        FieldsPanel.Children.Add(selector);
        FieldsPanel.Children.Add(_sourceNote);
        BuildSourceNote(_sources.ElementAtOrDefault(selected));

        _editors.Add((
            new ProviderField("provider_source", I18n.T("editProvider.source")),
            () => (selector.SelectedItem as SegmentedItem)?.Tag?.ToString() ?? "",
            _ => { }));
    }

    /// <summary>
    /// The selected source's caveat (e.g. "tokens only renew while the app is in use"),
    /// rendered under the tabs as readable text rather than as an icon on the tab.
    /// </summary>
    private void BuildSourceNote(IProviderSource? source)
    {
        _sourceNote.Children.Clear();
        if (source?.AttentionNote is not { } noteKey)
        {
            _sourceNote.Visibility = Visibility.Collapsed;
            return;
        }

        var row = new Grid
        {
            ColumnSpacing = 6,
            Margin = new Thickness(0, 2, 0, 0),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon
        {
            Glyph = WarningGlyph,
            FontSize = 12,
            Foreground = WarningBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var text = new TextBlock
        {
            Text = I18n.T(noteKey),
            Style = (Style)Application.Current.Resources["CaptionText"],
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        _sourceNote.Children.Add(row);
        _sourceNote.Visibility = Visibility.Visible;
        AutomationProperties.SetAutomationId(_sourceNote, $"SourceNote_{_instanceId}");
        AutomationProperties.SetName(_sourceNote, I18n.T(noteKey));
    }

    private IReadOnlySet<string> VisibleFieldKeys(ProviderField[] fields)
    {
        if (_sources.Count <= 1)
            return fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = SelectedSource() ?? _sources[0];
        return selected.ConfigFieldKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void AddSectionHeader(string title, string description)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 2) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["CaptionText"],
            TextWrapping = TextWrapping.Wrap,
        });
        FieldsPanel.Children.Add(panel);
    }

    private static void AddDescription(Panel panel, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return;

        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.72,
        });
    }

    private async Task<string?> PickFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _revealAllErrors = true;
        if (!RefreshValidity())
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            foreach (var (field, read, _) in _editors)
            {
                if (field.IsGlobal)
                    _svc.Config.Set(field.Key, read());
                else
                    _svc.Config.Set(ScopedKey(field.Key), read());
            }

            await _svc.Config.SaveAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ProviderRegistry.Create(_type)
                .FetchAsync(_instanceId, _svc.Config, timeout.Token)
                .ConfigureAwait(true);
            SetFetchError(null);
        }
        catch (Exception error)
        {
            SetFetchError(
                I18n.LocalizeErrorMessage(error.Message),
                error is ProviderException providerError
                    ? providerError.Kind
                    : ProviderErrorKind.Unknown);
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void SetFetchError(string? message, ProviderErrorKind? errorKind = null)
    {
        _currentErrorKind = string.IsNullOrWhiteSpace(message) ? null : errorKind;
        if (_fetchErrorText is null)
            return;

        _fetchErrorText.Text = message ?? "";
        _fetchErrorText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        RefreshConnectionAction();
    }

    private IConfig DraftConfig()
    {
        var globalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scopedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, read, _) in _editors)
        {
            if (field.IsGlobal)
                globalValues[field.Key] = read();
            else
                scopedValues[field.Key] = read();
        }

        return new OverlayConfig(_svc.Config, _instanceId, globalValues, scopedValues);
    }

    private string ReadValue(ProviderField field) =>
        field.IsGlobal
            ? _svc.Config.Get(field.Key)
            : _svc.Config.GetScoped(_instanceId, field.Key);

    private string ScopedKey(string key) => $"{_instanceId}.{key}";

    private static bool IsTruthy(string v) => v is "true" or "1" or "yes";
}
