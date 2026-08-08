using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using QuotaLens.Helpers;
using QuotaLens.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace QuotaLens.Views;

public sealed partial class UsageCylinder : UserControl
{
    private const double BarHeight = 44;
    private const double BarRadius = 4;
    private const double BracketHeight = 5;
    private const double FullLabelMinWidth = 108;
    private const double PercentOnlyMinWidth = 44;
    private INotifyCollectionChanged? _collectionChanged;
    private bool _hasRenderedWhileLoaded;
    private string _lastRenderedSignature = "";

    public UsageCylinder()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
    }

    public event EventHandler<UsageTimelineSegmentEventArgs>? SegmentPreviewed;
    public event EventHandler<UsageTimelineSegmentEventArgs>? SegmentPreviewEnded;
    public event EventHandler<UsageTimelineSegmentEventArgs>? SegmentInvoked;

    public IEnumerable? Segments
    {
        get => (IEnumerable?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(
            nameof(Segments),
            typeof(IEnumerable),
            typeof(UsageCylinder),
            new PropertyMetadata(null, OnSegmentsChanged));

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (UsageCylinder)d;
        if (control._collectionChanged is not null)
            control._collectionChanged.CollectionChanged -= control.OnSegmentsCollectionChanged;

        control._collectionChanged = e.NewValue as INotifyCollectionChanged;
        if (control._collectionChanged is not null)
            control._collectionChanged.CollectionChanged += control.OnSegmentsCollectionChanged;

        control.Render();
    }

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Render();
    }

    private void Render()
    {
        var previousSignature = _lastRenderedSignature;
        RootPanel.Children.Clear();

        var segments = Segments?
            .OfType<UsageTimelineSegmentViewModel>()
            .Where(segment => segment.Weight > 0)
            .ToList() ?? new List<UsageTimelineSegmentViewModel>();
        if (segments.Count == 0)
        {
            _lastRenderedSignature = "";
            return;
        }

        var nextSignature = RenderSignature(segments);
        var animate = IsLoaded
            && _hasRenderedWhileLoaded
            && !string.Equals(previousSignature, nextSignature, StringComparison.Ordinal);

        RootPanel.Children.Add(CreateBar(segments));
        RootPanel.Children.Add(CreateBrackets(segments));

        _lastRenderedSignature = nextSignature;
        if (IsLoaded)
            _hasRenderedWhileLoaded = true;
        if (animate)
            AnimateRender();
    }

    private UIElement CreateBar(IReadOnlyList<UsageTimelineSegmentViewModel> segments)
    {
        var outer = new Border
        {
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarRadius),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Background = RemainderBrush(),
        };

        var grid = new Grid { ColumnSpacing = 0 };
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            // Weights are token-proportional and span orders of magnitude; a small
            // floor keeps tiny plans visible and hover/clickable next to huge ones.
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(segment.Weight, GridUnitType.Star),
                MinWidth = segment.IsRemainder ? 0 : 4,
            });

            FrameworkElement child = segment.IsInteractive
                ? CreateSegmentButton(segment, index, segments.Count)
                : CreateSegmentSurface(segment, index, segments.Count);

            Grid.SetColumn(child, index);
            grid.Children.Add(child);
        }

        outer.Child = grid;
        return outer;
    }

    private FrameworkElement CreateSegmentButton(UsageTimelineSegmentViewModel segment, int index, int count)
    {
        var background = SegmentBrush(Brand.LegibleColor(segment.ProviderType));
        var button = new Button
        {
            Background = background,
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            UseSystemFocusVisuals = true,
            CornerRadius = SegmentCornerRadius(index, count),
            Content = CreateSegmentLabel(segment, out var labelParts),
        };
        ApplySegmentButtonResources(button, background);

        AutomationProperties.SetAutomationId(button, $"UsageTimelineSegment_{segment.InstanceId}");
        AutomationProperties.SetName(button, segment.AutomationName);

        button.SizeChanged += (_, _) => ApplySegmentLabelMode(button.ActualWidth, labelParts);
        button.PointerEntered += (_, _) => SegmentPreviewed?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));
        button.PointerExited += (_, _) => SegmentPreviewEnded?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));
        button.GotFocus += (_, _) => SegmentPreviewed?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));
        button.LostFocus += (_, _) => SegmentPreviewEnded?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));
        button.Click += (_, _) => SegmentInvoked?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));

        return button;
    }

    private static FrameworkElement CreateSegmentSurface(UsageTimelineSegmentViewModel segment, int index, int count)
    {
        var border = new Border
        {
            Background = segment.IsRemainder
                ? RemainderBrush()
                : SegmentBrush(Brand.LegibleColor(segment.ProviderType)),
            CornerRadius = SegmentCornerRadius(index, count),
        };
        AutomationProperties.SetName(border, segment.AutomationName);
        if (!segment.IsRemainder)
            border.Child = CreateSegmentLabel(segment, out _);
        return border;
    }

    private static FrameworkElement CreateSegmentLabel(UsageTimelineSegmentViewModel segment, out SegmentLabelParts labelParts)
    {
        var panel = new Grid
        {
            Margin = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = segment.Label,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = new SolidColorBrush(Colors.White),
            Opacity = 0.94,
            Margin = new Thickness(0, 0, 0, 2),
        };
        Grid.SetRow(label, 0);
        panel.Children.Add(label);

        var percent = new TextBlock
        {
            Text = segment.AvailableText,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Foreground = new SolidColorBrush(Colors.White),
        };
        Grid.SetRow(percent, 1);
        panel.Children.Add(percent);

        labelParts = new SegmentLabelParts(label, percent);
        return panel;
    }

    private static void ApplySegmentButtonResources(Button button, Brush background)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var white = new SolidColorBrush(Colors.White);

        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = background;
        button.Resources["ButtonBackgroundPressed"] = background;
        button.Resources["ButtonBackgroundDisabled"] = background;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        button.Resources["ButtonForeground"] = white;
        button.Resources["ButtonForegroundPointerOver"] = white;
        button.Resources["ButtonForegroundPressed"] = white;
        button.Resources["ButtonForegroundDisabled"] = white;
    }

    internal static SegmentLabelMode SegmentLabelModeForWidth(double width)
    {
        if (width >= FullLabelMinWidth)
            return SegmentLabelMode.Full;
        if (width >= PercentOnlyMinWidth)
            return SegmentLabelMode.PercentOnly;
        return SegmentLabelMode.None;
    }

    private static void ApplySegmentLabelMode(double width, SegmentLabelParts parts)
    {
        switch (SegmentLabelModeForWidth(width))
        {
            case SegmentLabelMode.Full:
                parts.Label.Visibility = Visibility.Visible;
                parts.Percent.Visibility = Visibility.Visible;
                Grid.SetRow(parts.Percent, 1);
                break;
            case SegmentLabelMode.PercentOnly:
                parts.Label.Visibility = Visibility.Collapsed;
                parts.Percent.Visibility = Visibility.Visible;
                Grid.SetRow(parts.Percent, 0);
                break;
            default:
                parts.Label.Visibility = Visibility.Collapsed;
                parts.Percent.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private sealed record SegmentLabelParts(TextBlock Label, TextBlock Percent);

    private static string RenderSignature(IEnumerable<UsageTimelineSegmentViewModel> segments) =>
        string.Join("|", segments.Select(segment =>
            string.Join(":", segment.InstanceId, segment.ProviderType, segment.Label, segment.AvailableText,
                segment.Weight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                segment.ResetFrequencyText, segment.IsRemainder)));

    internal enum SegmentLabelMode
    {
        None,
        PercentOnly,
        Full,
    }

    private static CornerRadius SegmentCornerRadius(int index, int count)
    {
        var left = index == 0 ? BarRadius : 0;
        var right = index == count - 1 ? BarRadius : 0;
        return new CornerRadius(left, right, right, left);
    }

    private static Brush SegmentBrush(Color color)
    {
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = ScaleColor(color, 1.32, 0xF8), Offset = 0 },
                new GradientStop { Color = ScaleColor(color, 1.00, 0xF2), Offset = 0.48 },
                new GradientStop { Color = ScaleColor(color, 0.72, 0xF2), Offset = 1 },
            },
        };
    }

    private static Grid CreateBrackets(IReadOnlyList<UsageTimelineSegmentViewModel> segments)
    {
        var grid = new Grid { ColumnSpacing = 0, MinHeight = 22 };
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(segment.Weight, GridUnitType.Star) });
        }

        for (var index = 0; index < segments.Count;)
        {
            var segment = segments[index];

            if (segment.IsRemainder || !segment.HasResetFrequencyText)
            {
                index++;
                continue;
            }

            var label = segment.ResetFrequencyText!;
            var span = 1;
            while (index + span < segments.Count
                   && !segments[index + span].IsRemainder
                   && string.Equals(segments[index + span].ResetFrequencyText, label, StringComparison.Ordinal))
            {
                span++;
            }

            var panel = new StackPanel { Spacing = 3, Margin = new Thickness(3, 0, 3, 0) };
            var bracket = new Border
            {
                Height = BracketHeight,
                Opacity = 0.58,
                BorderBrush = ResolveBrush("TextFillColorTertiaryBrush", Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1, 0, 1, 1),
            };
            panel.Children.Add(bracket);
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 9,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush", Colors.White),
            });
            AutomationProperties.SetName(panel, label);
            Grid.SetColumn(panel, index);
            Grid.SetColumnSpan(panel, span);
            grid.Children.Add(panel);

            index += span;
        }

        return grid;
    }

    private void AnimateRender()
    {
        var transform = new TranslateTransform { Y = 2 };
        RootPanel.RenderTransform = transform;
        RootPanel.Opacity = 0.82;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation
        {
            From = 0.82,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(170)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(opacity, RootPanel);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);

        var settle = new DoubleAnimation
        {
            From = 2,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(190)),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(settle, transform);
        Storyboard.SetTargetProperty(settle, "Y");
        storyboard.Children.Add(settle);
        storyboard.Begin();
    }

    private static Brush RemainderBrush()
    {
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0xDC, 0x81, 0x86, 0x8C), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0xC8, 0x5C, 0x61, 0x67), Offset = 0.52 },
                new GradientStop { Color = Color.FromArgb(0xC4, 0x3E, 0x43, 0x49), Offset = 1 },
            },
        };
    }

    private static Color ScaleColor(Color color, double scale, byte alpha)
    {
        static byte Channel(byte value, double scale) =>
            (byte)Math.Clamp(Math.Round(value * scale), 0, 255);

        return Color.FromArgb(
            alpha,
            Channel(color.R, scale),
            Channel(color.G, scale),
            Channel(color.B, scale));
    }

    private static Brush ResolveBrush(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

}

public sealed class UsageTimelineSegmentEventArgs : EventArgs
{
    public UsageTimelineSegmentEventArgs(string instanceId)
    {
        InstanceId = instanceId;
    }

    public string InstanceId { get; }
}
