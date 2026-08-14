using System.Diagnostics;
using System.Globalization;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.Services;
using QuotaLens.ViewModels;
using QuotaLens.Views;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace QuotaLens;

public sealed partial class MainWindow : Window
{
    private const double AmbientTintTransitionMilliseconds = 900;
    private const double AmbientTintFrameMilliseconds = 16;
    private const double ProviderReorderAnimationMilliseconds = 360;
    private const double WideLayoutBreakpoint = 672;
    private const double DashboardSingleColumnMaxWidth = 720;
    private const double WideContentMaxWidth = 1120;
    private const double DashboardHorizontalMargin = 32;
    private const double PlanValueRuleSectionMinWidth = 300;
    private const double PlanValueRuleSectionColumnSpacing = 12;
    private const int PlanValueRuleSectionMaxColumns = 3;
    private const int TimelineBringIntoViewPulseDelayMilliseconds = 220;

    private readonly IProviderService _svc;
    private readonly StartupLaunchService _startupLaunch;
    private readonly nint _hwnd;
    private DispatcherQueueTimer? _ambientTintTimer;
    private LinearGradientBrush? _ambientTintBrush;
    private DispatcherQueueTimer? _ditherRebuildTimer;
    private int _ditherWidth;
    private int _ditherHeight;
    private readonly List<Storyboard> _providerReorderAnimations = new();
    private readonly List<ProviderSortTerm> _sortPriorityTerms = new();
    private readonly Dictionary<string, List<PlanRuleEditor>> _planRuleEditorsByProvider = new();
    private readonly List<(string ConfigKey, TextBox Box)> _launchPathEditors = new();
    private long _ambientTintStartTimestamp;
    private Windows.UI.Color _ambientTintStartColor;
    private Windows.UI.Color _ambientTintTargetColor;
    private Windows.UI.Color _ambientTintDisplayedColor;
    private int _planValueRulesColumnCount;
    private bool _dialogOpen;
    private bool _isUpdatingSortSelection;
    private bool _sortSelectionCanAnimate;
    private int _providerReorderAnimationVersion;

    /// <summary>Integration constructs the window with the live provider service.</summary>
    public MainWindow(IProviderService svc)
    {
        _svc = svc;
        _startupLaunch = new StartupLaunchService();

        // Build the VM before InitializeComponent so x:Bind OneTime bindings resolve.
        // The DispatcherQueue is used by the VM to marshal service events onto the UI thread.
        Vm = new DashboardViewModel(svc, DispatcherQueue.GetForCurrentThread());

        InitializeComponent();

        RootGrid.DataContext = Vm;
        _ambientTintDisplayedColor = Vm.AmbientTintColor;
        _ambientTintTargetColor = _ambientTintDisplayedColor;
        _ambientTintBrush = CreateAmbientTintBrush(_ambientTintDisplayedColor);
        AmbientTintCurrent.Background = _ambientTintBrush;
        UpdateSortSelection();
        _sortSelectionCanAnimate = true;

        _hwnd = WindowNative.GetWindowHandle(this);

        // ===== Authentic Fluent chrome =====
        // Mica backdrop (theme + system accent come automatically from WinUI).
        SystemBackdrop = new MicaBackdrop();

        // Extend into the title bar; keep the NATIVE caption buttons, just supply our region.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.PreferredMinimumWidth = 520;

        // Size the window once the content is loaded, when the real DPI/rasterization
        // scale is known (querying DPI before the window is shown returns 96 → wrong size).
        RootGrid.Loaded += (_, _) => { SizeAndCenterByScale(); ApplyCaptionInset(); QueueAdaptiveLayoutUpdate(); QueueDitherRebuild(); };
        RootGrid.SizeChanged += (_, _) => { QueueAdaptiveLayoutUpdate(); QueueDitherRebuild(); };
        DashboardRoot.SizeChanged += (_, _) => QueueAdaptiveLayoutUpdate();
        SettingsRoot.SizeChanged += (_, _) => QueueAdaptiveLayoutUpdate();
        AppTitleBar.SizeChanged += (_, _) => ApplyCaptionInset();
        PlanValueRulesPanel.SizeChanged += OnPlanValueRulesPanelSizeChanged;
        Activated += OnFirstActivated;
        SizeChanged += (_, _) => QueueAdaptiveLayoutUpdate();
        AppWindow.Changed += OnAppWindowChanged;

        RenderStaticTexts();
        RenderLaunchPathRows();
        BuildLanguageOptions();

        // ===== Dialog + toast wiring =====
        Vm.SettingsRequested += OnSettingsRequested;
        Vm.AddProviderRequested += OnAddProviderRequested;
        Vm.EditProviderRequested += async (_, item) =>
        {
            var result = await ShowDialogAsync(new EditProviderDialog(_svc, item.InstanceId, item.ProviderType, item.Name, _hwnd));
            if (result == ContentDialogResult.Primary)
                _ = _svc.RefreshAsync(item.InstanceId);
        };
        Vm.DeleteProviderRequested += OnDeleteProviderRequested;
        Vm.PropertyChanged += OnViewModelPropertyChanged;
        Vm.Providers.CollectionChanged += OnProvidersCollectionChanged;

        Closed += (_, _) =>
        {
            Vm.SettingsRequested -= OnSettingsRequested;
            Vm.AddProviderRequested -= OnAddProviderRequested;
            Vm.DeleteProviderRequested -= OnDeleteProviderRequested;
            Vm.PropertyChanged -= OnViewModelPropertyChanged;
            Vm.Providers.CollectionChanged -= OnProvidersCollectionChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            _ambientTintTimer?.Stop();
            StopProviderReorderAnimations(resetTransforms: true);
            Vm.Dispose();
        };
    }

    public DashboardViewModel Vm { get; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        SizeAndCenterByScale();
        QueueAdaptiveLayoutUpdate();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
            QueueAdaptiveLayoutUpdate();
    }

    /// <summary>Resize to the default logical dashboard size (× DPI scale) and center.</summary>
    private void SizeAndCenterByScale()
    {
        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi <= 0 ? (RootGrid.XamlRoot?.RasterizationScale ?? 1.0) : dpi / 96.0;
        if (scale <= 0) scale = 1.0;
        var w = (int)Math.Round(Services.WindowHelper.DefaultWidth * scale);
        var h = (int)Math.Round(Services.WindowHelper.DefaultHeight * scale);
        AppWindow.Resize(new SizeInt32(w, h));

        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + (work.Width - w) / 2,
            work.Y + (work.Height - h) / 2));
    }

    /// <summary>Keep the custom title bar clear of the native caption buttons.</summary>
    private void ApplyCaptionInset()
    {
        try
        {
            // TitleBar.RightInset is in physical pixels; convert to DIPs for the XAML grid.
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;
            var dips = AppWindow.TitleBar.RightInset / scale;
            RightInset.Width = new GridLength(dips > 0 ? dips : 138);
        }
        catch
        {
            RightInset.Width = new GridLength(138);
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(Microsoft.UI.Xaml.Controls.ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = Content.XamlRoot;
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async void OnAddProviderRequested(object? sender, EventArgs e)
    {
        // Pass the configured instances so the picker can badge what is already tracked.
        var dialog = new AddProviderDialog(_svc.Config.Instances);
        await ShowDialogAsync(dialog);

        var type = dialog.SelectedProviderType;
        if (type is null)
            return;

        await ProviderAddFlow.AddAsync(
            _svc,
            type,
            async instance =>
                await ShowDialogAsync(new EditProviderDialog(_svc, instance.Id, instance.Type, instance.Name, _hwnd))
                == ContentDialogResult.Primary);
    }

    private async void OnDeleteProviderRequested(object? sender, ProviderItemViewModel item)
    {
        var dialog = new ContentDialog
        {
            Title = I18n.T("provider.removeTitle"),
            Content = new TextBlock
            {
                Text = I18n.T("provider.removeMessage", "name", item.Name),
                TextWrapping = TextWrapping.WrapWholeWords,
            },
            PrimaryButtonText = I18n.T("common.remove"),
            CloseButtonText = I18n.T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
            BorderThickness = new Thickness(1),
        };

        if (Application.Current.Resources.TryGetValue("OverlayCornerRadius", out var radius) && radius is CornerRadius cornerRadius)
            dialog.CornerRadius = cornerRadius;
        if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var brush) && brush is Brush borderBrush)
            dialog.BorderBrush = borderBrush;

        dialog.Background = new AcrylicBrush
        {
            TintColor = Windows.UI.Color.FromArgb(0xFF, 0x2B, 0x27, 0x25),
            TintOpacity = 0.86,
            TintLuminosityOpacity = 0.72,
            FallbackColor = Windows.UI.Color.FromArgb(0xFF, 0x24, 0x21, 0x20),
        };

        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
            Vm.RemoveProvider(item);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.AmbientTintColor))
            AnimateAmbientTintChange();
        else if (e.PropertyName == nameof(DashboardViewModel.SortMode))
            UpdateSortSelection();
    }

    private void OnProvidersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        QueueAdaptiveLayoutUpdate();
    }

    private void OnUsageTimelineSegmentPreviewed(object? sender, UsageTimelineSegmentEventArgs e)
    {
        SetProviderTimelineHighlight(e.InstanceId, true);
    }

    private void OnUsageTimelineSegmentPreviewEnded(object? sender, UsageTimelineSegmentEventArgs e)
    {
        SetProviderTimelineHighlight(e.InstanceId, false);
    }

    private async void OnUsageTimelineSegmentInvoked(object? sender, UsageTimelineSegmentEventArgs e)
    {
        var provider = Vm.Providers.FirstOrDefault(item => item.InstanceId == e.InstanceId);
        if (provider is null)
            return;

        var delayedPulse = false;
        if (ProviderCardsControl.ContainerFromItem(provider) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 0.18,
            });
            delayedPulse = true;
        }

        if (delayedPulse)
            await Task.Delay(TimelineBringIntoViewPulseDelayMilliseconds);

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (Vm.Providers.Contains(provider))
            {
                provider.PulseTimelineHighlight();
                if (ProviderCardsControl.ContainerFromItem(provider) is DependencyObject pulseContainer)
                    FindDescendant<ProviderCard>(pulseContainer)?.PulseTimelineAttention();
            }
        });
    }

    private void SetProviderTimelineHighlight(string instanceId, bool isHighlighted)
    {
        var provider = Vm.Providers.FirstOrDefault(item => item.InstanceId == instanceId);
        if (provider is not null)
            provider.IsTimelineHighlighted = isHighlighted;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    private void AnimateAmbientTintChange()
    {
        var nextColor = Vm.AmbientTintColor;
        if (ColorsEqual(_ambientTintTargetColor, nextColor))
            return;

        _ambientTintStartColor = _ambientTintDisplayedColor;
        _ambientTintTargetColor = nextColor;
        _ambientTintStartTimestamp = Stopwatch.GetTimestamp();

        _ambientTintTimer ??= CreateAmbientTintTimer();
        _ambientTintTimer.Stop();
        _ambientTintTimer.Start();
    }

    private DispatcherQueueTimer CreateAmbientTintTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(AmbientTintFrameMilliseconds);
        timer.IsRepeating = true;
        timer.Tick += OnAmbientTintTick;
        return timer;
    }

    private void OnAmbientTintTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = Stopwatch.GetElapsedTime(_ambientTintStartTimestamp).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / AmbientTintTransitionMilliseconds, 0, 1);
        var easedProgress = EaseInOutCubic(progress);

        _ambientTintDisplayedColor = LerpColor(_ambientTintStartColor, _ambientTintTargetColor, easedProgress);
        ApplyAmbientTintColor(_ambientTintDisplayedColor);

        if (progress < 1)
            return;

        sender.Stop();
        _ambientTintDisplayedColor = _ambientTintTargetColor;
        ApplyAmbientTintColor(_ambientTintDisplayedColor);
    }

    // ===== Gradient dither =====
    // XAML composits at 8 bits/channel with no higher-precision path, so the
    // full-window tint gradient quantizes into visible bands. A static layer of
    // per-pixel ±1 LSB noise at ~2% opacity (the same recipe as Windows acrylic)
    // breaks the bands up. The noise must be generated at DEVICE pixel size and
    // shown 1:1 — stretching a fixed bitmap blurs the noise into visible grain.

    private const byte DitherAlpha = 5;

    private void QueueDitherRebuild()
    {
        if (_ditherRebuildTimer is null)
        {
            _ditherRebuildTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _ditherRebuildTimer.Interval = TimeSpan.FromMilliseconds(150);
            _ditherRebuildTimer.IsRepeating = false;
            _ditherRebuildTimer.Tick += (_, _) => _ = RebuildDitherAsync();
        }

        _ditherRebuildTimer.Stop();
        _ditherRebuildTimer.Start();
    }

    private async Task RebuildDitherAsync()
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)Math.Ceiling(RootGrid.ActualWidth * scale);
        var height = (int)Math.Ceiling(RootGrid.ActualHeight * scale);
        if (width <= 0 || height <= 0 || (width == _ditherWidth && height == _ditherHeight))
            return;

        var pixels = await Task.Run(() => CreateDitherPixels(width, height, DitherAlpha));

        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
            stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();

        AmbientDitherLayer.Source = bitmap;
        _ditherWidth = width;
        _ditherHeight = height;
    }

    private static byte[] CreateDitherPixels(int width, int height, byte alpha)
    {
        // Premultiplied BGRA: white noise pixels carry channel == alpha, black
        // pixels 0 — bipolar noise that nudges the underlying ramp ±1 code value.
        // Fixed seed: the pattern must be static; animating it reads as film grain.
        var buffer = new byte[width * height * 4];
        var rng = new Random(0x5EED);
        for (var i = 0; i < buffer.Length; i += 4)
        {
            var premultiplied = rng.Next(2) == 0 ? (byte)0 : alpha;
            buffer[i] = premultiplied;
            buffer[i + 1] = premultiplied;
            buffer[i + 2] = premultiplied;
            buffer[i + 3] = alpha;
        }

        return buffer;
    }

    private LinearGradientBrush CreateAmbientTintBrush(Windows.UI.Color color)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        foreach (var stop in Brand.AmbientTintStops(color))
            brush.GradientStops.Add(new GradientStop { Offset = stop.Offset, Color = stop.Color });
        return brush;
    }

    private void ApplyAmbientTintColor(Windows.UI.Color color)
    {
        _ambientTintBrush ??= CreateAmbientTintBrush(color);
        AmbientTintCurrent.Background = _ambientTintBrush;
        var stops = Brand.AmbientTintStops(color);
        for (var index = 0; index < stops.Count; index++)
            _ambientTintBrush.GradientStops[index].Color = stops[index].Color;
    }

    private static double EaseInOutCubic(double progress) =>
        progress < 0.5
            ? 4 * progress * progress * progress
            : 1 - Math.Pow(-2 * progress + 2, 3) / 2;

    private static Windows.UI.Color LerpColor(Windows.UI.Color from, Windows.UI.Color to, double progress) =>
        Windows.UI.Color.FromArgb(
            LerpByte(from.A, to.A, progress),
            LerpByte(from.R, to.R, progress),
            LerpByte(from.G, to.G, progress),
            LerpByte(from.B, to.B, progress));

    private static byte LerpByte(byte from, byte to, double progress) =>
        (byte)Math.Clamp(Math.Round(from + ((to - from) * progress)), byte.MinValue, byte.MaxValue);

    private static bool ColorsEqual(Windows.UI.Color left, Windows.UI.Color right)
        => left.A == right.A && left.R == right.R && left.G == right.G && left.B == right.B;

    // ===== Settings page navigation =====

    private void OnSettingsRequested(object? sender, EventArgs e) => OpenSettingsPage();

    private async void OnSortSelectorSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var nextMode = sender.SelectedItem switch
        {
            _ when sender.SelectedItem == SortByResetFrequencyItem => ProviderSortMode.ResetFrequency,
            _ when sender.SelectedItem == SortByNextResetItem => ProviderSortMode.NextReset,
            _ => ProviderSortMode.PlanValue,
        };

        var shouldAnimate = !_isUpdatingSortSelection && _sortSelectionCanAnimate && Vm.SortMode != nextMode;
        IReadOnlyDictionary<string, Windows.Foundation.Point>? oldPositions = null;
        var animationVersion = _providerReorderAnimationVersion;
        if (shouldAnimate)
        {
            StopProviderReorderAnimations(resetTransforms: true);
            oldPositions = CaptureProviderCardPositions();
            animationVersion = ++_providerReorderAnimationVersion;
        }

        Vm.SetSortModeCommand.Execute(nextMode);

        if (oldPositions is { Count: > 0 })
            await AnimateProviderReorderAsync(oldPositions, animationVersion);
    }

    private void UpdateSortSelection()
    {
        if (SortSelector == null || SortByPlanValueItem == null || SortByResetFrequencyItem == null || SortByNextResetItem == null)
            return;

        _isUpdatingSortSelection = true;
        try
        {
            SortSelector.SelectedItem = Vm.SortMode switch
            {
                ProviderSortMode.ResetFrequency => SortByResetFrequencyItem,
                ProviderSortMode.NextReset => SortByNextResetItem,
                _ => SortByPlanValueItem,
            };
        }
        finally
        {
            _isUpdatingSortSelection = false;
        }
    }

    private void UpdateAdaptiveLayout()
    {
        UpdateDashboardAdaptiveWidth();
        UpdateSettingsAdaptiveLayout();
    }

    private void UpdateDashboardAdaptiveWidth()
    {
        if (DashboardContent == null || DashboardViewport == null)
            return;

        var viewportWidth = DashboardRoot.ActualWidth;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            viewportWidth = RootGrid.ActualWidth;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            return;

        DashboardViewport.Width = viewportWidth;

        var available = Math.Max(0, viewportWidth - DashboardHorizontalMargin);
        var maxWidth = viewportWidth >= WideLayoutBreakpoint
            ? WideContentMaxWidth
            : DashboardSingleColumnMaxWidth;
        var contentWidth = Math.Max(0, Math.Min(available, maxWidth));
        DashboardContent.Width = contentWidth;
        ProviderCardsControl.Width = contentWidth;
        Vm.IsProviderGridMultiColumn = AdaptiveProviderCardsLayout.GetColumnCount(
            contentWidth,
            AdaptiveProviderCardsLayout.DefaultMinColumnWidth,
            AdaptiveProviderCardsLayout.DefaultColumnSpacing,
            Vm.Providers.Count) > 1;
        ProviderCardsControl.InvalidateMeasure();
        DashboardContent.InvalidateMeasure();

    }

    private void UpdateSettingsAdaptiveLayout()
    {
        if (SettingsContent == null || SettingsRoot == null)
            return;

        var viewportWidth = SettingsRoot.ActualWidth;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            viewportWidth = RootGrid.ActualWidth;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            return;

        SettingsContent.Width = Math.Min(viewportWidth, WideContentMaxWidth);
        var layoutWidth = RootGrid.ActualWidth;
        if (!double.IsFinite(layoutWidth) || layoutWidth <= 0)
            layoutWidth = viewportWidth;
        var useStackedRows = layoutWidth < WideLayoutBreakpoint;
        ArrangeSettingsRow(RefreshIntervalSettingsRow, IntervalBox, useStackedRows);
        ArrangeSettingsRow(EmptyThresholdSettingsRow, ThresholdBox, useStackedRows);
        ArrangeSettingsRow(DeprioritizeEmptySettingsRow, DeprioritizeEmptyToggle, useStackedRows);
        ArrangeSettingsRow(LaunchAtStartupSettingsRow, LaunchAtStartupToggle, useStackedRows);
        ArrangeSettingsRow(StartHiddenAtStartupSettingsRow, StartHiddenAtStartupToggle, useStackedRows);
        ArrangeSettingsRow(SortPrioritySettingsRow, SortPriorityPanel, useStackedRows);
        ArrangeSettingsRow(DefaultLaunchEditorSettingsRow, DefaultLaunchEditorControls, useStackedRows);
    }

    private static void ArrangeSettingsRow(Grid row, FrameworkElement trailingControl, bool useStackedLayout)
    {
        if (row.RowDefinitions.Count == 0)
        {
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        row.RowSpacing = useStackedLayout ? 12 : 0;
        trailingControl.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(trailingControl, useStackedLayout ? 1 : 0);
        Grid.SetColumn(trailingControl, useStackedLayout ? 1 : 2);
        Grid.SetColumnSpan(trailingControl, useStackedLayout ? 2 : 1);
    }

    private void QueueAdaptiveLayoutUpdate()
    {
        _ = DispatcherQueue.TryEnqueue(UpdateAdaptiveLayout);
    }

    private Dictionary<string, Windows.Foundation.Point> CaptureProviderCardPositions()
    {
        var positions = new Dictionary<string, Windows.Foundation.Point>();
        foreach (var provider in Vm.Providers)
        {
            if (ProviderCardsControl.ContainerFromItem(provider) is not FrameworkElement container)
                continue;

            var point = container.TransformToVisual(ProviderCardsControl)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            positions[provider.InstanceId] = point;
        }

        return positions;
    }

    private async Task AnimateProviderReorderAsync(IReadOnlyDictionary<string, Windows.Foundation.Point> oldPositions, int animationVersion)
    {
        await WaitForProviderCardsLayoutAsync();
        if (animationVersion != _providerReorderAnimationVersion)
            return;

        foreach (var provider in Vm.Providers)
        {
            if (!oldPositions.TryGetValue(provider.InstanceId, out var oldPoint)
                || ProviderCardsControl.ContainerFromItem(provider) is not FrameworkElement container)
            {
                continue;
            }

            var point = container.TransformToVisual(ProviderCardsControl)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var offsetX = oldPoint.X - point.X;
            var offsetY = oldPoint.Y - point.Y;
            if (Math.Abs(offsetX) < 0.5 && Math.Abs(offsetY) < 0.5)
                continue;

            AnimateProviderContainer(container, offsetX, offsetY, animationVersion);
        }
    }

    private async Task WaitForProviderCardsLayoutAsync()
    {
        var layoutTask = new TaskCompletionSource<object?>();

        void OnLayoutUpdated(object? sender, object e)
        {
            layoutTask.TrySetResult(null);
        }

        ProviderCardsControl.LayoutUpdated += OnLayoutUpdated;
        try
        {
            await Task.WhenAny(layoutTask.Task, Task.Delay(120));
        }
        finally
        {
            ProviderCardsControl.LayoutUpdated -= OnLayoutUpdated;
        }
    }

    private void AnimateProviderContainer(FrameworkElement container, double offsetX, double offsetY, int animationVersion)
    {
        var transform = new TranslateTransform { X = offsetX, Y = offsetY };
        container.RenderTransform = transform;

        var yAnimation = new DoubleAnimation
        {
            From = offsetY,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(ProviderReorderAnimationMilliseconds)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(yAnimation, transform);
        Storyboard.SetTargetProperty(yAnimation, nameof(TranslateTransform.Y));

        var storyboard = new Storyboard();
        storyboard.Children.Add(yAnimation);

        if (Math.Abs(offsetX) >= 0.5)
        {
            var xAnimation = new DoubleAnimation
            {
                From = offsetX,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(ProviderReorderAnimationMilliseconds)),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(xAnimation, transform);
            Storyboard.SetTargetProperty(xAnimation, nameof(TranslateTransform.X));
            storyboard.Children.Add(xAnimation);
        }
        storyboard.Completed += (_, _) =>
        {
            _providerReorderAnimations.Remove(storyboard);
            if (animationVersion == _providerReorderAnimationVersion && ReferenceEquals(container.RenderTransform, transform))
                container.RenderTransform = null;
        };

        _providerReorderAnimations.Add(storyboard);
        storyboard.Begin();
    }

    private void StopProviderReorderAnimations(bool resetTransforms)
    {
        foreach (var storyboard in _providerReorderAnimations.ToArray())
            storyboard.Stop();
        _providerReorderAnimations.Clear();

        if (!resetTransforms)
            return;

        foreach (var provider in Vm.Providers)
        {
            if (ProviderCardsControl.ContainerFromItem(provider) is FrameworkElement container)
                container.RenderTransform = null;
        }
    }

    /// <summary>
    /// All chrome strings that are set from code (not x:Bind): the window title, the
    /// title-bar label and every settings-page row. Re-run after a language change.
    /// </summary>
    private void RenderStaticTexts()
    {
        Title = I18n.T("app.title");
        TitleText.Text = Vm.AppTitle;
        SettingsHeading.Text = I18n.T("settings.global");
        SettingsRowTitle.Text = I18n.T("settings.refreshInterval");
        SettingsHint.Text = I18n.T("settings.refreshIntervalHint");
        ThresholdRowTitle.Text = I18n.T("settings.emptyThreshold");
        ThresholdHint.Text = I18n.T("settings.emptyThresholdHint");
        DeprioritizeEmptyRowTitle.Text = I18n.T("settings.deprioritizeEmptyProviders");
        DeprioritizeEmptyHint.Text = I18n.T("settings.deprioritizeEmptyProvidersHint");
        LaunchAtStartupRowTitle.Text = I18n.T("settings.launchAtStartup");
        LaunchAtStartupHint.Text = I18n.T("settings.launchAtStartupHint");
        StartHiddenAtStartupRowTitle.Text = I18n.T("settings.startHiddenAtStartup");
        StartHiddenAtStartupHint.Text = I18n.T("settings.startHiddenAtStartupHint");
        SortPriorityRowTitle.Text = I18n.T("settings.sortPriority");
        SortPriorityHint.Text = I18n.T("settings.sortPriorityHint");
        PlanValueRulesRowTitle.Text = I18n.T("settings.planValueRules");
        PlanValueRulesHint.Text = I18n.T("settings.planValueRulesHint");
        DefaultLaunchEditorRowTitle.Text = I18n.T("settings.defaultLaunchEditor");
        DefaultLaunchEditorHint.Text = I18n.T("settings.defaultLaunchEditorHint");
        DefaultLaunchEditorBox.PlaceholderText = I18n.T("settings.defaultLaunchEditorPlaceholder");
        AutomationProperties.SetName(BackButton, I18n.T("settings.close"));
        BackButtonToolTipText.Text = I18n.T("settings.close");
        AutomationProperties.SetName(BrowseDefaultLaunchEditorButton, I18n.T("settings.browseDefaultLaunchEditor"));
        BrowseDefaultLaunchEditorToolTipText.Text = I18n.T("settings.browse");
        LaunchPathsTitle.Text = I18n.T("settings.launchPaths");
        LaunchPathsHint.Text = I18n.T("settings.launchPathsHint");
        LanguageRowTitle.Text = I18n.T("settings.language");
        LanguageHint.Text = I18n.T("settings.languageHint");
        SaveSettingsButton.Content = I18n.T("settings.apply");
        CancelSettingsButton.Content = I18n.T("common.cancel");
    }

    /// <summary>
    /// Re-renders every visible string after the language preference changes, so the
    /// switch is immediate instead of requiring a restart.
    /// </summary>
    private void RefreshUiLanguage()
    {
        RenderStaticTexts();
        BuildLanguageOptions();
        SelectLanguageOption(_svc.Config.Get("language"));
        RenderLaunchPathRows();
        LoadSortPrioritySettings();
        LoadPlanValueRulesSettings();
        Vm.RefreshLanguageTexts();
        TitleText.Text = SettingsRoot.Visibility == Visibility.Visible ? Vm.SettingsTitle : Vm.AppTitle;
    }

    private void BuildLanguageOptions()
    {
        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(new ComboBoxItem { Content = I18n.T("settings.language.system"), Tag = "" });
        LanguageBox.Items.Add(new ComboBoxItem { Content = I18n.T("settings.language.en"), Tag = "en" });
        LanguageBox.Items.Add(new ComboBoxItem { Content = I18n.T("settings.language.zh"), Tag = "zh" });
    }

    private void SelectLanguageOption(string value)
    {
        for (var i = 0; i < LanguageBox.Items.Count; i++)
        {
            if (string.Equals((LanguageBox.Items[i] as ComboBoxItem)?.Tag?.ToString(),
                    value, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedIndex = i;
                return;
            }
        }

        LanguageBox.SelectedIndex = 0; // Follow system
    }

    private void OpenSettingsPage()
    {
        var current = _svc.Config.Get("min_refresh_interval_secs", "1800");
        IntervalBox.Value = double.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs) ? secs : 1800;

        var thresholdRaw = _svc.Config.Get("empty_threshold_pct", "5");
        ThresholdBox.Value = double.TryParse(thresholdRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var tVal) ? tVal : 5.0;

        DeprioritizeEmptyToggle.IsOn = ProviderSortPolicy.DeprioritizeEmptyProvidersFromConfig(_svc.Config);
        LaunchAtStartupToggle.IsOn = _startupLaunch.IsEnabled();
        StartHiddenAtStartupToggle.IsOn = _startupLaunch.IsStartHiddenEnabled();
        UpdateStartHiddenAtStartupAvailability();
        DefaultLaunchEditorBox.Text = _svc.Config.Get(Catalog.DefaultLaunchEditorPathKey);
        SelectLanguageOption(_svc.Config.Get("language"));
        LoadLaunchPaths();
        LoadSortPrioritySettings();
        LoadPlanValueRulesSettings();

        DashboardRoot.Visibility = Visibility.Collapsed;
        SettingsRoot.Visibility = Visibility.Visible;
        TitleIcon.Visibility = Visibility.Collapsed;
        TitleCommandBar.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        TitleText.Text = Vm.SettingsTitle;
        QueueAdaptiveLayoutUpdate();
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e)
    {
        SettingsRoot.Visibility = Visibility.Collapsed;
        DashboardRoot.Visibility = Visibility.Visible;
        TitleIcon.Visibility = Visibility.Visible;
        TitleCommandBar.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        TitleText.Text = Vm.AppTitle;
        QueueAdaptiveLayoutUpdate();
    }

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var secs = double.IsNaN(IntervalBox.Value) ? 1800 : (int)Math.Max(30, IntervalBox.Value);
        _svc.Config.Set("min_refresh_interval_secs", secs.ToString(CultureInfo.InvariantCulture));

        var threshold = double.IsNaN(ThresholdBox.Value) ? 5.0 : Math.Clamp(ThresholdBox.Value, 0.0, 100.0);
        _svc.Config.Set("empty_threshold_pct", threshold.ToString(CultureInfo.InvariantCulture));
        _svc.Config.Set(ProviderSortPolicy.DeprioritizeEmptyProvidersConfigKey, DeprioritizeEmptyToggle.IsOn ? "true" : "false");
        _startupLaunch.SetEnabled(LaunchAtStartupToggle.IsOn, StartHiddenAtStartupToggle.IsOn);
        _svc.Config.Set(Catalog.DefaultLaunchEditorPathKey, DefaultLaunchEditorBox.Text);
        var previousLanguage = _svc.Config.Get("language");
        var selectedLanguage = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        _svc.Config.Set("language", selectedLanguage);
        SaveLaunchPaths();
        _svc.Config.Set(ProviderSortPriorityOrder.ConfigKey, ProviderSortPriorityOrder.Serialize(_sortPriorityTerms));
        SavePlanValueRulesSettings();

        await _svc.Config.SaveAsync();
        Vm.RefreshLaunchAvailability();
        Vm.RefreshSortPriority();
        _ = _svc.RefreshAllAsync();

        // Switch language immediately: re-render every visible string in place.
        if (!string.Equals(previousLanguage, selectedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            I18n.SetLanguage(selectedLanguage);
            RefreshUiLanguage();
        }

        OnCloseSettings(sender, e);
    }

    private void OnLaunchAtStartupToggled(object sender, RoutedEventArgs e) =>
        UpdateStartHiddenAtStartupAvailability();

    private void UpdateStartHiddenAtStartupAvailability() =>
        StartHiddenAtStartupToggle.IsEnabled = LaunchAtStartupToggle.IsOn;

    private async void OnBrowseDefaultLaunchEditor(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".bat");
        picker.FileTypeFilter.Add(".cmd");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file != null)
            DefaultLaunchEditorBox.Text = file.Path;
    }

    // ---- Desktop-app launch paths (global, per provider type) ----

    private void RenderLaunchPathRows()
    {
        LaunchPathsPanel.Children.Clear();
        _launchPathEditors.Clear();

        foreach (var (providerType, target) in Catalog.LaunchTargets)
        {
            if (target.ConfigKey is null)
                continue;

            var box = new TextBox
            {
                PlaceholderText = I18n.T("settings.autoDetect"),
                MinWidth = 220,
            };
            AutomationProperties.SetName(box, target.DisplayName + " app path");

            var browse = new Button
            {
                Content = new FontIcon { Glyph = "\uE8E5", FontSize = 14 },
                Style = (Style)Application.Current.Resources["CardIconButton"],
            };
            var browseName = I18n.T("settings.browse");
            ToolTipService.SetToolTip(browse, browseName);
            AutomationProperties.SetName(browse, browseName);
            browse.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, _hwnd);
                picker.FileTypeFilter.Add(".exe");
                picker.FileTypeFilter.Add("*");
                var file = await picker.PickSingleFileAsync();
                if (file is not null)
                    box.Text = file.Path;
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = target.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            Grid.SetColumn(browse, 2);
            grid.Children.Add(browse);

            LaunchPathsPanel.Children.Add(grid);
            _launchPathEditors.Add((target.ConfigKey, box));
        }
    }

    private void LoadLaunchPaths()
    {
        foreach (var (configKey, box) in _launchPathEditors)
            box.Text = _svc.Config.Get(configKey);
    }

    private void SaveLaunchPaths()
    {
        foreach (var (configKey, box) in _launchPathEditors)
            _svc.Config.Set(configKey, box.Text);
    }

    private void LoadSortPrioritySettings()
    {
        _sortPriorityTerms.Clear();
        _sortPriorityTerms.AddRange(ProviderSortPriorityOrder.FromConfig(_svc.Config));
        RenderSortPriorityRows();
    }

    private void RenderSortPriorityRows()
    {
        SortPriorityPanel.Children.Clear();
        for (var index = 0; index < _sortPriorityTerms.Count; index++)
            SortPriorityPanel.Children.Add(CreateSortPriorityRow(index));
    }

    private UIElement CreateSortPriorityRow(int index)
    {
        var term = _sortPriorityTerms[index];
        var termName = I18n.T(ProviderSortPriorityOrder.I18nKey(term));
        var termDescription = I18n.T(ProviderSortPriorityOrder.DescriptionI18nKey(term));
        var grid = new Grid
        {
            MinHeight = 32,
            ColumnSpacing = 6,
        };
        ToolTipService.SetToolTip(grid, CreateSortPriorityToolTip(termDescription));
        AutomationProperties.SetName(grid, termName);
        AutomationProperties.SetHelpText(grid, termDescription);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var position = new TextBlock
        {
            Text = (index + 1).ToString(CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(position, 0);
        grid.Children.Add(position);

        var label = new TextBlock
        {
            Text = termName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(label, CreateSortPriorityToolTip(termDescription));
        AutomationProperties.SetHelpText(label, termDescription);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var upButton = CreatePriorityMoveButton(index, moveUp: true);
        Grid.SetColumn(upButton, 2);
        grid.Children.Add(upButton);

        var downButton = CreatePriorityMoveButton(index, moveUp: false);
        Grid.SetColumn(downButton, 3);
        grid.Children.Add(downButton);

        return grid;
    }

    private static ToolTip CreateSortPriorityToolTip(string text) =>
        new()
        {
            Content = new TextBlock
            {
                Text = text,
                MaxWidth = 320,
                TextWrapping = TextWrapping.Wrap,
            },
        };

    private Button CreatePriorityMoveButton(int index, bool moveUp)
    {
        var term = _sortPriorityTerms[index];
        var button = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            IsEnabled = moveUp ? index > 0 : index < _sortPriorityTerms.Count - 1,
            Content = new FontIcon
            {
                FontSize = 12,
                Glyph = moveUp ? "\uE70E" : "\uE70D",
            },
        };

        if (Application.Current.Resources.TryGetValue("CardIconButton", out var style) && style is Style buttonStyle)
            button.Style = buttonStyle;

        var directionName = I18n.T(moveUp ? "settings.movePriorityUp" : "settings.movePriorityDown");
        var termName = I18n.T(ProviderSortPriorityOrder.I18nKey(term));
        AutomationProperties.SetAutomationId(
            button,
            $"SortPriority{(moveUp ? "MoveUp" : "MoveDown")}{term}");
        AutomationProperties.SetName(button, $"{directionName}: {termName}");
        ToolTipService.SetToolTip(button, $"{directionName}: {termName}");
        button.Click += (_, _) => MoveSortPriority(index, moveUp ? -1 : 1);
        return button;
    }

    private void MoveSortPriority(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _sortPriorityTerms.Count)
            return;

        (_sortPriorityTerms[index], _sortPriorityTerms[target]) = (_sortPriorityTerms[target], _sortPriorityTerms[index]);
        RenderSortPriorityRows();
    }

    private void LoadPlanValueRulesSettings()
    {
        _planRuleEditorsByProvider.Clear();
        PlanValueRulesPanel.Children.Clear();
        PlanValueRulesPanel.RowDefinitions.Clear();
        PlanValueRulesPanel.ColumnDefinitions.Clear();
        _planValueRulesColumnCount = 0;

        foreach (var provider in Catalog.AddableTypes.Where(type => Catalog.SubscriptionProviderTypes.Contains(type.Id)))
            PlanValueRulesPanel.Children.Add(CreatePlanValueProviderSection(provider));

        ArrangePlanValueRuleSections();
    }

    private void OnPlanValueRulesPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ArrangePlanValueRuleSections();
    }

    private void ArrangePlanValueRuleSections()
    {
        var childCount = PlanValueRulesPanel.Children.Count;
        var columns = PlanValueRuleColumnCount(PlanValueRulesPanel.ActualWidth);
        var rows = childCount == 0 ? 0 : (int)Math.Ceiling(childCount / (double)columns);

        if (PlanValueRulesPanel.ColumnDefinitions.Count != columns)
        {
            PlanValueRulesPanel.ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++)
                PlanValueRulesPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        if (PlanValueRulesPanel.RowDefinitions.Count != rows)
        {
            PlanValueRulesPanel.RowDefinitions.Clear();
            for (var i = 0; i < rows; i++)
                PlanValueRulesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        if (_planValueRulesColumnCount == columns && childCount == 0)
            return;

        _planValueRulesColumnCount = columns;
        for (var i = 0; i < childCount; i++)
        {
            var child = (FrameworkElement)PlanValueRulesPanel.Children[i];
            Grid.SetColumn(child, i % columns);
            Grid.SetRow(child, i / columns);
        }
    }

    private static int PlanValueRuleColumnCount(double availableWidth)
    {
        if (availableWidth <= 0)
            return 1;

        var columns = (int)Math.Floor(
            (availableWidth + PlanValueRuleSectionColumnSpacing) /
            (PlanValueRuleSectionMinWidth + PlanValueRuleSectionColumnSpacing));
        return Math.Clamp(columns, 1, PlanValueRuleSectionMaxColumns);
    }

    private UIElement CreatePlanValueProviderSection(ProviderType provider)
    {
        var header = new StackPanel { Spacing = 1 };
        header.Children.Add(new TextBlock
        {
            Text = provider.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = PlanValueRuleSummary(provider.Id),
            Style = (Style)Application.Current.Resources["CaptionText"],
        });

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = I18n.T("settings.planValueRulesProviderHint"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionText"],
        });

        var captions = new Grid { ColumnSpacing = 6 };
        captions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        captions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        captions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        captions.Children.Add(new TextBlock { Text = I18n.T("settings.planValueKeyword"), Style = (Style)Application.Current.Resources["CaptionText"] });
        var valueCaption = new TextBlock { Text = I18n.T("settings.planValueUsdMonth"), Style = (Style)Application.Current.Resources["CaptionText"] };
        Grid.SetColumn(valueCaption, 1);
        captions.Children.Add(valueCaption);
        body.Children.Add(captions);

        var rowsPanel = new StackPanel { Spacing = 6 };
        body.Children.Add(rowsPanel);
        _planRuleEditorsByProvider[provider.Id] = new List<PlanRuleEditor>();

        foreach (var rule in PlanValueRules.ForProvider(provider.Id, _svc.Config))
            AddPlanRuleRow(provider.Id, rowsPanel, rule.Keyword, rule.Value);
        if (_planRuleEditorsByProvider[provider.Id].Count == 0)
            AddPlanRuleRow(provider.Id, rowsPanel, "", 0);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addButton = new Button { Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        addButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = "\uE710", FontSize = 12 },
                new TextBlock { Text = I18n.T("settings.addPlanValueRule") },
            },
        };
        addButton.Click += (_, _) => AddPlanRuleRow(provider.Id, rowsPanel, "", 0);
        actions.Children.Add(addButton);

        var resetButton = new Button
        {
            Content = I18n.T("settings.restorePlanValueDefaults"),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        };
        resetButton.Click += (_, _) => ResetPlanRulesToDefaults(provider.Id, rowsPanel);
        actions.Children.Add(resetButton);
        body.Children.Add(actions);

        return new Expander
        {
            Header = header,
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            IsExpanded = false,
        };
    }

    private string PlanValueRuleSummary(string providerType)
    {
        var rules = PlanValueRules.ForProvider(providerType, _svc.Config);
        if (rules.Count == 0)
            return I18n.T("settings.planValueNoRules");
        return I18n.T("settings.planValueRuleCount", "count", rules.Count.ToString(CultureInfo.InvariantCulture));
    }

    private void AddPlanRuleRow(string providerType, StackPanel rowsPanel, string keyword, double value)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keywordBox = new TextBox
        {
            Text = keyword,
            PlaceholderText = "pro",
        };
        AutomationProperties.SetName(keywordBox, I18n.T("settings.planValueKeyword"));

        var valueBox = new NumberBox
        {
            Value = value,
            Minimum = 0,
            SmallChange = 1,
            LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        AutomationProperties.SetName(valueBox, I18n.T("settings.planValueUsdMonth"));

        var removeButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            Style = (Style)Application.Current.Resources["CardIconButton"],
        };
        AutomationProperties.SetName(removeButton, I18n.T("settings.removePlanValueRule"));

        var editor = new PlanRuleEditor(row, keywordBox, valueBox);
        removeButton.Click += (_, _) =>
        {
            _planRuleEditorsByProvider[providerType].Remove(editor);
            rowsPanel.Children.Remove(row);
        };

        Grid.SetColumn(keywordBox, 0);
        Grid.SetColumn(valueBox, 1);
        Grid.SetColumn(removeButton, 2);
        row.Children.Add(keywordBox);
        row.Children.Add(valueBox);
        row.Children.Add(removeButton);

        _planRuleEditorsByProvider[providerType].Add(editor);
        rowsPanel.Children.Add(row);
    }

    private void ResetPlanRulesToDefaults(string providerType, StackPanel rowsPanel)
    {
        rowsPanel.Children.Clear();
        _planRuleEditorsByProvider[providerType].Clear();

        if (Catalog.DefaultPlanValueRules.TryGetValue(providerType, out var defaults) && defaults.Length > 0)
        {
            foreach (var rule in defaults)
                AddPlanRuleRow(providerType, rowsPanel, rule.Keyword, rule.Value);
        }
        else
        {
            AddPlanRuleRow(providerType, rowsPanel, "", 0);
        }
    }

    private void SavePlanValueRulesSettings()
    {
        foreach (var providerType in Catalog.PayAsYouGoProviderTypes)
            _svc.Config.Remove(PlanValueRules.ConfigKey(providerType));

        foreach (var (providerType, editors) in _planRuleEditorsByProvider)
        {
            var rules = editors
                .Select(editor => new ProviderPlanValueRule(
                    editor.KeywordBox.Text.Trim(),
                    double.IsNaN(editor.ValueBox.Value) ? -1 : editor.ValueBox.Value))
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Keyword) && rule.Value >= 0)
                .ToList();

            var key = PlanValueRules.ConfigKey(providerType);
            if (PlanValueRules.AreEquivalentToDefaults(providerType, rules))
                _svc.Config.Remove(key);
            else
                _svc.Config.Set(key, PlanValueRules.Serialize(rules));
        }
    }

    private sealed record PlanRuleEditor(Grid Row, TextBox KeywordBox, NumberBox ValueBox);
}
