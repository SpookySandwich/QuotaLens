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

    public EditProviderDialog(IProviderService svc, string instanceId, string providerType, string displayName, nint hwnd)
    {
        InitializeComponent();
        _svc = svc;
        _instanceId = instanceId;
        _type = providerType;
        _hwnd = hwnd;
        _selectedSourceId = _svc.Config.GetScoped(_instanceId, ProviderSourceRunner.SourceConfigKey);

        Title = displayName;
        PrimaryButtonText = I18n.T("common.done");
        CloseButtonText = I18n.T("common.cancel");
        IsPrimaryButtonEnabled = false;

        BuildFields();
        PrimaryButtonClick += OnSave;
    }

    private void BuildFields()
    {
        FieldsPanel.Children.Clear();
        _editors.Clear();
        _errorViews.Clear();
        _fetchErrorText = null;
        _revealAllErrors = false;
        IsPrimaryButtonEnabled = false;

        Catalog.Fields.TryGetValue(_type, out var fields);
        fields ??= Array.Empty<ProviderField>();

        AddSectionHeader(I18n.T("editProvider.connection"), I18n.T("editProvider.connectionHint"));

        _sources = ProviderRegistry.Create(_type).Sources;
        if (_sources.Count > 1)
            BuildSourceSelector();
        BuildConnectionActions();

        if (fields.Length == 0)
        {
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
                box.PasswordChanged += (_, _) => OnFieldChanged();
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
                box.TextChanged += (_, _) => OnFieldChanged();
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
        }

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
    private void OnFieldChanged() => RefreshValidity();

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

        IsPrimaryButtonEnabled = valid;
        return valid;
    }

    private void BuildConnectionActions()
    {
        if (ShowsSignIn())
        {
            var signIn = new Button { Content = I18n.T("editProvider.signIn") };
            AutomationProperties.SetAutomationId(signIn, $"SignIn_{_instanceId}");
            AutomationProperties.SetName(signIn, I18n.T("editProvider.signIn"));
            signIn.Click += OnSignInClick;
            FieldsPanel.Children.Add(signIn);
        }

        _fetchErrorText = new TextBlock
        {
            Foreground = InvalidBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        FieldsPanel.Children.Add(_fetchErrorText);
    }

    private string? SelectedSourceId() =>
        !string.IsNullOrWhiteSpace(_selectedSourceId)
            ? _selectedSourceId
            : _sources.FirstOrDefault()?.Id;

    private bool ShowsSignIn()
    {
        var source = SelectedSourceId();
        if (WebLoginService.IsSupported(_type))
            return _sources.Count <= 1 || string.Equals(source, "web", StringComparison.OrdinalIgnoreCase);

        return ProviderLoginLauncher.IsSupported(_type)
            && (_sources.Count <= 1 || string.Equals(source, "cli", StringComparison.OrdinalIgnoreCase));
    }

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        if (await _svc.OpenLoginAsync(_instanceId).ConfigureAwait(true))
            SetFetchError(null);
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
    /// For multi-source providers, a source dropdown (App / CLI) so the user can choose
    /// which data origin this instance uses. Stored per instance as "provider_source".
    /// </summary>
    private void BuildSourceSelector()
    {
        var segmented = new Segmented { Header = I18n.T("editProvider.source") };
        foreach (var source in _sources)
            segmented.Items.Add(new SegmentedItem { Content = SourceItemContent(source), Tag = source.Id });

        var selected = 0;
        for (var index = 0; index < _sources.Count; index++)
        {
            if (string.Equals(_sources[index].Id, _selectedSourceId, StringComparison.OrdinalIgnoreCase))
            {
                selected = index;
                break;
            }
        }
        segmented.SelectedIndex = selected;

        segmented.SelectionChanged += (_, _) =>
        {
            _selectedSourceId = (segmented.SelectedItem as SegmentedItem)?.Tag?.ToString();
            BuildSourceNote(_sources.ElementAtOrDefault(segmented.SelectedIndex));
            BuildFields();
        };

        AutomationProperties.SetAutomationId(segmented, $"Source_{_instanceId}");
        FieldsPanel.Children.Add(segmented);
        FieldsPanel.Children.Add(_sourceNote);
        BuildSourceNote(_sources.ElementAtOrDefault(selected));

        _editors.Add((
            new ProviderField("provider_source", I18n.T("editProvider.source")),
            () => (segmented.SelectedItem as SegmentedItem)?.Tag?.ToString() ?? "",
            _ => { }));
    }

    /// <summary>
    /// Tab labels stay plain text. A glyph crowded against the label was easy to miss
    /// and read as decoration; the note is shown BELOW the row instead, where it can
    /// actually be read. See <see cref="BuildSourceNote"/>.
    /// </summary>
    private static object SourceItemContent(IProviderSource source) => source.Name;

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

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 2, 0, 0),
        };
        row.Children.Add(new FontIcon
        {
            Glyph = WarningGlyph,
            FontSize = 12,
            Foreground = WarningBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = I18n.T(noteKey),
            Style = (Style)Application.Current.Resources["CaptionText"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        });

        _sourceNote.Children.Add(row);
        _sourceNote.Visibility = Visibility.Visible;
        AutomationProperties.SetName(_sourceNote, I18n.T(noteKey));
    }

    private IReadOnlySet<string> VisibleFieldKeys(ProviderField[] fields)
    {
        if (_sources.Count <= 1)
            return fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = _sources.FirstOrDefault(source =>
                string.Equals(source.Id, _selectedSourceId, StringComparison.OrdinalIgnoreCase))
            ?? _sources[0];
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
            SetFetchError(I18n.LocalizeErrorMessage(error.Message));
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void SetFetchError(string? message)
    {
        if (_fetchErrorText is null)
            return;

        _fetchErrorText.Text = message ?? "";
        _fetchErrorText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private string ReadValue(ProviderField field) =>
        field.IsGlobal
            ? _svc.Config.Get(field.Key)
            : _svc.Config.GetScoped(_instanceId, field.Key);

    private string ScopedKey(string key) => $"{_instanceId}.{key}";

    private static bool IsTruthy(string v) => v is "true" or "1" or "yes";
}
