using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.Providers;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace QuotaLens.Views;

/// <summary>
/// Provider edit dialog. Fields are built dynamically from
/// <see cref="Catalog.Fields"/> for the instance's type (TextBox / PasswordBox /
/// file-pick / ToggleSwitch), read+written with scoped config keys, then persisted
/// and the provider refreshed.
///
/// Fields that have an environment-variable mapping show a trailing import glyph; it
/// copies that ONE field's value from the environment (never overwriting an existing
/// value) — a per-field affordance instead of a single catch-all button.
/// </summary>
public sealed partial class EditProviderDialog : ContentDialog
{
    // Segoe Fluent / MDL2 glyphs.
    private const string BrowseGlyph = "\uE8E5";

    private readonly IProviderService _svc;
    private readonly string _instanceId;
    private readonly string _type;
    private readonly nint _hwnd;
    private readonly List<(ProviderField Field, Func<string> Read, Action<string> Write)> _editors = new();

    public EditProviderDialog(IProviderService svc, string instanceId, string providerType, string displayName, nint hwnd)
    {
        InitializeComponent();
        _svc = svc;
        _instanceId = instanceId;
        _type = providerType;
        _hwnd = hwnd;

        Title = displayName;
        PrimaryButtonText = I18n.T("settings.save");
        CloseButtonText = I18n.T("common.cancel");

        BuildFields();
        PrimaryButtonClick += OnSave;
    }

    private void BuildFields()
    {
        FieldsPanel.Children.Clear();
        _editors.Clear();

        if (!Catalog.Fields.TryGetValue(_type, out var fields) || fields.Length == 0) return;

        AddSectionHeader("Connection", "Provider-specific account, CLI, or sign-in settings.");

        foreach (var field in fields)
        {
            var current = ReadValue(field.Key);
            var automationId = $"Field_{_instanceId}_{field.Key}";

            if (field.IsToggle)
            {
                var toggle = new ToggleSwitch
                {
                    Header = field.Label,
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
                    Header = field.Label,
                    Password = current,
                    PlaceholderText = placeholder,
                };
                read = () => box.Password;
                write = value => box.Password = value;
                input = box;
            }
            else
            {
                var box = new TextBox
                {
                    Header = field.Label,
                    Text = current,
                    PlaceholderText = placeholder,
                };
                read = () => box.Text;
                write = value => box.Text = value;
                input = box;
            }

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
            FieldsPanel.Children.Add(wrap);
            _editors.Add((field, read, write));
        }
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

    /// <summary>
    /// Placeholder for an empty field: the effective value QuotaLens will use when
    /// the field is left empty. A set environment variable wins (its value for plain
    /// fields, "from ENV" for secrets), otherwise the static default.
    /// </summary>
    private string EffectivePlaceholder(ProviderField field)
    {
        foreach (var envKey in ProviderConfig.EnvironmentKeysFor(_type, field.Key))
        {
            var envValue = ProviderConfig.Environment(envKey);
            if (envValue is null)
                continue;
            return field.IsPassword ? $"from {envKey}" : envValue;
        }

        return field.Placeholder;
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
        var deferral = args.GetDeferral();
        try
        {
            foreach (var (field, read, _) in _editors)
                _svc.Config.Set(ScopedKey(field.Key), read());

            await _svc.Config.SaveAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private string ReadValue(string key) => _svc.Config.GetScoped(_instanceId, key);

    private string ScopedKey(string key) => $"{_instanceId}.{key}";

    private static bool IsTruthy(string v) => v is "true" or "1" or "yes";
}
