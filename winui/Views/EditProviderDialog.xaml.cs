using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using QuotaLens.Core;
using QuotaLens.Helpers;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace QuotaLens.Views;

/// <summary>
/// Provider edit dialog. Fields are built dynamically from
/// <see cref="Catalog.Fields"/> for the instance's type (TextBox / PasswordBox /
/// file-pick / ToggleSwitch), read+written with scoped to base config keys, then
/// persisted and the provider refreshed.
/// </summary>
public sealed partial class EditProviderDialog : ContentDialog
{
    // Segoe Fluent / MDL2 glyph for the "open file" / browse button.
    private const string BrowseGlyph = "";
    private const string RemoveGlyph = "";

    private readonly IProviderService _svc;
    private readonly string _instanceId;
    private readonly string _type;
    private readonly nint _hwnd;
    private readonly List<(ProviderField Field, Func<string> Read)> _editors = new();

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

        var importButton = new Button
        {
            Content = "Import from environment",
        };
        AutomationProperties.SetAutomationId(importButton, $"ImportEnv_{_instanceId}");
        AutomationProperties.SetName(importButton, "Import empty fields from environment variables");
        importButton.Click += OnImportEnvironment;
        FieldsPanel.Children.Add(importButton);

        AddSectionHeader("Connection", "Provider-specific account, CLI, or sign-in settings.");

        foreach (var field in fields)
        {
            var current = ReadValue(field.Key);
            var fieldAutomationId = $"Field_{_instanceId}_{field.Key}";

            if (field.IsToggle)
            {
                var toggle = new ToggleSwitch
                {
                    Header = field.Label,
                    IsOn = IsTruthy(current),
                };
                AutomationProperties.SetAutomationId(toggle, fieldAutomationId);
                var panel = new StackPanel { Spacing = 2 };
                panel.Children.Add(toggle);
                AddDescription(panel, field.Description);
                FieldsPanel.Children.Add(panel);
                _editors.Add((field, () => toggle.IsOn ? "true" : "false"));
            }
            else if (field.IsPassword)
            {
                var box = new PasswordBox
                {
                    Header = field.Label,
                    Password = current,
                    PlaceholderText = field.Placeholder,
                };
                AutomationProperties.SetAutomationId(box, fieldAutomationId);
                FieldsPanel.Children.Add(WrapWithDescription(box, field.Description));
                _editors.Add((field, () => box.Password));
            }
            else if (field.IsFilePath)
            {
                var box = new TextBox
                {
                    Header = field.Label,
                    Text = current,
                    PlaceholderText = field.Placeholder,
                };
                AutomationProperties.SetAutomationId(box, fieldAutomationId);
                var browse = new Button
                {
                    Content = new FontIcon { Glyph = BrowseGlyph, FontSize = 14 },
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                AutomationProperties.SetAutomationId(browse, $"Browse_{_instanceId}_{field.Key}");
                AutomationProperties.SetName(browse, $"Browse {field.Label}");
                browse.Click += async (_, _) =>
                {
                    var path = await PickFileAsync();
                    if (!string.IsNullOrEmpty(path)) box.Text = path;
                };

                var grid = new Grid { ColumnSpacing = 6 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(box, 0);
                Grid.SetColumn(browse, 1);
                grid.Children.Add(box);
                grid.Children.Add(browse);

                var wrap = new StackPanel { Spacing = 4 };
                wrap.Children.Add(grid);
                AddDescription(wrap, field.Description);
                FieldsPanel.Children.Add(wrap);
                _editors.Add((field, () => box.Text));
            }
            else
            {
                var box = new TextBox
                {
                    Header = field.Label,
                    Text = current,
                    PlaceholderText = field.Placeholder,
                };
                AutomationProperties.SetAutomationId(box, fieldAutomationId);
                FieldsPanel.Children.Add(WrapWithDescription(box, field.Description));
                _editors.Add((field, () => box.Text));
            }
        }
    }

    private void OnImportEnvironment(object sender, RoutedEventArgs e)
    {
        var imported = _svc.Config.ImportEnvironment(_instanceId);
        BuildFields();
        if (imported > 0)
            AppLog.Info($"edit: imported {imported} field(s) from environment for {_instanceId}");
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

    private static StackPanel WrapWithDescription(Control control, string? description)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(control);
        AddDescription(panel, description);
        return panel;
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
            foreach (var (field, read) in _editors)
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
