using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private const double SegmentMinWidth = 4;
    private const double MorphDurationMs = 300.0;

    private INotifyCollectionChanged? _collectionChanged;
    private bool _hasRenderedWhileLoaded;
    private bool _renderQueued;
    private string _lastRenderedSignature = "";

    private sealed class SegmentTrack
    {
        public string InstanceId { get; set; } = "";
        public ColumnDefinition Column { get; set; } = null!;
        public FrameworkElement Element { get; set; } = null!;
        public SegmentLabelParts? LabelParts { get; set; }
        public double CurrentWeight { get; set; }
        public double StartWeight { get; set; }
        public double TargetWeight { get; set; }
        public double StartOpacity { get; set; } = 1.0;
        public UsageTimelineSegmentViewModel Segment { get; set; } = null!;
        public bool IsEntering { get; set; }
        public bool IsLeaving { get; set; }
    }

    private readonly List<SegmentTrack> _tracks = new();
    private Grid? _barGrid;
    private Border? _barBorder;
    private Grid? _bracketGrid;
    private DispatcherTimer? _morphTimer;
    private DateTimeOffset _morphStartTime;

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

        control.RequestRender();
    }

    private void OnSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RequestRender();
    }

    private void RequestRender()
    {
        if (_renderQueued)
            return;

        var queue = DispatcherQueue;
        if (queue is null)
        {
            Render();
            return;
        }

        _renderQueued = true;
        queue.TryEnqueue(() =>
        {
            _renderQueued = false;
            Render();
        });
    }

    private void Render()
    {
        var segments = Segments?
            .OfType<UsageTimelineSegmentViewModel>()
            .Where(segment => segment.Weight > 0)
            .ToList() ?? new List<UsageTimelineSegmentViewModel>();

        if (segments.Count == 0)
        {
            _morphTimer?.Stop();
            _morphTimer = null;
            _tracks.Clear();
            _barGrid = null;
            _barBorder = null;
            _bracketGrid = null;
            RootPanel.Children.Clear();
            _lastRenderedSignature = "";
            return;
        }

        var nextSignature = RenderSignature(segments);
        if (_hasRenderedWhileLoaded && string.Equals(_lastRenderedSignature, nextSignature, StringComparison.Ordinal))
            return;

        _lastRenderedSignature = nextSignature;

        if (_barGrid == null || RootPanel.Children.Count == 0 || _tracks.Count == 0)
        {
            BuildInitialBar(segments);
            _hasRenderedWhileLoaded = true;
        }
        else if (IsLoaded && _hasRenderedWhileLoaded)
        {
            UpdateTracksAndMorph(segments);
        }
        else
        {
            BuildInitialBar(segments);
            _hasRenderedWhileLoaded = true;
        }
    }

    private void BuildInitialBar(IReadOnlyList<UsageTimelineSegmentViewModel> segments)
    {
        _morphTimer?.Stop();
        _morphTimer = null;
        _tracks.Clear();
        RootPanel.Children.Clear();

        _barBorder = new Border
        {
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarRadius),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Background = RemainderBrush(),
        };

        _barGrid = new Grid { ColumnSpacing = 0 };

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var col = new ColumnDefinition
            {
                Width = new GridLength(segment.Weight, GridUnitType.Star),
                MinWidth = segment.IsRemainder ? 0 : SegmentMinWidth,
            };
            _barGrid.ColumnDefinitions.Add(col);

            FrameworkElement child;
            SegmentLabelParts? labelParts = null;
            if (segment.IsInteractive)
            {
                child = CreateSegmentButton(segment, index, segments.Count, out labelParts);
            }
            else
            {
                child = CreateSegmentSurface(segment, index, segments.Count, out labelParts);
            }

            Grid.SetColumn(child, index);
            _barGrid.Children.Add(child);

            _tracks.Add(new SegmentTrack
            {
                InstanceId = segment.InstanceId,
                Column = col,
                Element = child,
                LabelParts = labelParts,
                CurrentWeight = segment.Weight,
                StartWeight = segment.Weight,
                TargetWeight = segment.Weight,
                Segment = segment,
            });
        }

        _barBorder.Child = _barGrid;
        RootPanel.Children.Add(_barBorder);

        _bracketGrid = new Grid { ColumnSpacing = 0, Margin = new Thickness(0, 4, 0, 0) };
        RootPanel.Children.Add(_bracketGrid);

        UpdateCornerRadii();
        RebuildBrackets();
    }

    private void UpdateTracksAndMorph(IReadOnlyList<UsageTimelineSegmentViewModel> segments)
    {
        if (_barGrid == null)
            return;

        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<SegmentTrack>(segments.Count + _tracks.Count);

        foreach (var seg in segments)
        {
            activeIds.Add(seg.InstanceId);
            var existingTrack = _tracks.FirstOrDefault(t => string.Equals(t.InstanceId, seg.InstanceId, StringComparison.Ordinal));
            if (existingTrack != null)
            {
                existingTrack.StartWeight = existingTrack.CurrentWeight;
                existingTrack.TargetWeight = seg.Weight;
                existingTrack.StartOpacity = existingTrack.Element.Opacity;
                existingTrack.Segment = seg;
                existingTrack.IsEntering = false;
                existingTrack.IsLeaving = false;
                existingTrack.Element.Visibility = Visibility.Visible;
                UpdateTrackVisual(existingTrack, seg);
                ordered.Add(existingTrack);
            }
            else
            {
                var col = new ColumnDefinition
                {
                    Width = new GridLength(0.0001, GridUnitType.Star),
                    MinWidth = 0,
                };

                FrameworkElement child;
                SegmentLabelParts? labelParts;
                if (seg.IsInteractive)
                    child = CreateSegmentButton(seg, ordered.Count, ordered.Count + 1, out labelParts);
                else
                    child = CreateSegmentSurface(seg, ordered.Count, ordered.Count + 1, out labelParts);

                child.Opacity = 0.0;
                _barGrid.Children.Add(child);

                ordered.Add(new SegmentTrack
                {
                    InstanceId = seg.InstanceId,
                    Column = col,
                    Element = child,
                    LabelParts = labelParts,
                    CurrentWeight = 0.0001,
                    StartWeight = 0.0001,
                    TargetWeight = seg.Weight,
                    StartOpacity = 0.0,
                    Segment = seg,
                    IsEntering = true,
                });
            }
        }

        foreach (var track in _tracks)
        {
            if (activeIds.Contains(track.InstanceId))
                continue;

            track.StartWeight = track.CurrentWeight;
            track.TargetWeight = 0.0001;
            track.StartOpacity = track.Element.Opacity;
            track.IsEntering = false;
            track.IsLeaving = true;
            ordered.Add(track);
        }

        _tracks.Clear();
        _tracks.AddRange(ordered);
        ApplyColumnOrder();
        StartMorphAnimation();
    }

    private void ApplyColumnOrder()
    {
        if (_barGrid == null)
            return;

        while (_barGrid.ColumnDefinitions.Count < _tracks.Count)
            _barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) });
        while (_barGrid.ColumnDefinitions.Count > _tracks.Count)
            _barGrid.ColumnDefinitions.RemoveAt(_barGrid.ColumnDefinitions.Count - 1);

        for (var index = 0; index < _tracks.Count; index++)
        {
            var track = _tracks[index];
            track.Column = _barGrid.ColumnDefinitions[index];
            track.Column.Width = new GridLength(Math.Max(0.0001, track.CurrentWeight), GridUnitType.Star);
            // Columns are recycled positionally, so a track can land on one left
            // behind by a removed segment; without this it inherits that floor.
            track.Column.MinWidth = MinWidthFor(track);
            Grid.SetColumn(track.Element, index);
        }

        RebuildBrackets();
    }

    /// <summary>
    /// The row of square brackets under the bar. Consecutive segments that reset on
    /// the same cadence share one bracket, so the chart reads as "these three refill
    /// every five hours, that one only comes back next week" without the user having
    /// to know each provider's schedule.
    /// </summary>
    private void RebuildBrackets()
    {
        if (_bracketGrid == null)
            return;

        _bracketGrid.Children.Clear();
        _bracketGrid.ColumnDefinitions.Clear();

        var drawable = _tracks
            .Where(track => !track.IsLeaving && track.TargetWeight > 0.001)
            .ToList();
        if (drawable.Count == 0)
            return;

        foreach (var track in drawable)
        {
            _bracketGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.0001, track.CurrentWeight), GridUnitType.Star),
                MinWidth = MinWidthFor(track),
            });
        }

        for (var index = 0; index < drawable.Count;)
        {
            var segment = drawable[index].Segment;
            if (segment.IsRemainder || !segment.HasResetFrequencyText)
            {
                index++;
                continue;
            }

            var key = segment.GroupKey;
            var span = 1;
            while (index + span < drawable.Count
                   && !drawable[index + span].Segment.IsRemainder
                   && string.Equals(drawable[index + span].Segment.GroupKey, key, StringComparison.Ordinal))
            {
                span++;
            }

            var panel = new StackPanel { Spacing = 3, Margin = new Thickness(3, 0, 3, 0) };
            panel.Children.Add(new Border
            {
                Height = BracketHeight,
                Opacity = 0.58,
                BorderBrush = ResolveBrush("TextFillColorTertiaryBrush", Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1, 0, 1, 1),
            });
            panel.Children.Add(new TextBlock
            {
                Text = segment.ResetFrequencyText,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Foreground = ResolveBrush("TextFillColorSecondaryBrush", Colors.White),
            });
            AutomationProperties.SetName(panel, segment.ResetFrequencyText);
            Grid.SetColumn(panel, index);
            Grid.SetColumnSpan(panel, span);
            _bracketGrid.Children.Add(panel);

            index += span;
        }
    }

    /// Brackets have to breathe with the bars they sit under, or a 300ms morph
    /// leaves every caption pointing at the wrong provider.
    private void SyncBracketColumns()
    {
        if (_bracketGrid == null)
            return;

        var drawable = _tracks
            .Where(track => !track.IsLeaving && track.TargetWeight > 0.001)
            .ToList();
        if (_bracketGrid.ColumnDefinitions.Count != drawable.Count)
            return;

        for (var index = 0; index < drawable.Count; index++)
        {
            var column = _bracketGrid.ColumnDefinitions[index];
            column.Width = new GridLength(Math.Max(0.0001, drawable[index].CurrentWeight), GridUnitType.Star);
            column.MinWidth = drawable[index].Column.MinWidth;
        }
    }

    private static Brush ResolveBrush(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    /// <summary>
    /// A segment's width floor follows what the segment IS, not the star weight it
    /// happens to be interpolating through. HeroViewModel deliberately floors a spent
    /// plan to 0.01 so it stays on screen, and 0.01 can never clear a threshold
    /// expressed in star units — testing the weight collapsed those bars to a
    /// sub-pixel line on the first update after the initial render.
    /// </summary>
    private static double MinWidthFor(SegmentTrack track) =>
        track.IsLeaving || track.TargetWeight <= 0.001 || track.Segment.IsRemainder
            ? 0
            : SegmentMinWidth;

    private void StartMorphAnimation()
    {
        _morphTimer?.Stop();
        _morphStartTime = DateTimeOffset.UtcNow;
        _morphTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _morphTimer.Tick += OnMorphTimerTick;
        _morphTimer.Start();
    }

    private void OnMorphTimerTick(object? sender, object e)
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStartTime).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / MorphDurationMs, 0.0, 1.0);
        var ease = 1.0 - Math.Pow(1.0 - progress, 3.0); // Cubic ease out

        foreach (var track in _tracks)
        {
            var w = track.StartWeight + (track.TargetWeight - track.StartWeight) * ease;
            track.CurrentWeight = Math.Max(0.0001, w);
            track.Column.Width = new GridLength(track.CurrentWeight, GridUnitType.Star);

            if (track.IsLeaving || track.TargetWeight <= 0.001)
            {
                track.Element.Opacity = Math.Clamp(track.StartOpacity * (1.0 - ease), 0.0, 1.0);
                track.Column.MinWidth = 0;
                if (progress >= 1.0)
                    track.Element.Visibility = Visibility.Collapsed;
            }
            else if (track.IsEntering)
            {
                track.Element.Visibility = Visibility.Visible;
                track.Element.Opacity = Math.Clamp(ease, 0.0, 1.0);
                // Ramp in from nothing, but land on the floor rather than below it.
                track.Column.MinWidth = progress >= 1.0 || w > 0.1 ? MinWidthFor(track) : 0;
            }
            else
            {
                track.Element.Visibility = Visibility.Visible;
                track.Element.Opacity = 1.0;
                track.Column.MinWidth = MinWidthFor(track);
            }
        }

        UpdateCornerRadii();
        SyncBracketColumns();

        if (progress >= 1.0)
        {
            _morphTimer?.Stop();
            _morphTimer = null;
            foreach (var track in _tracks.ToList())
            {
                track.CurrentWeight = track.TargetWeight;
                track.Column.Width = new GridLength(Math.Max(0.0001, track.TargetWeight), GridUnitType.Star);
                if (track.IsLeaving || track.TargetWeight <= 0.001)
                {
                    _barGrid?.Children.Remove(track.Element);
                    _tracks.Remove(track);
                }
                else
                {
                    track.IsEntering = false;
                    track.Element.Opacity = 1.0;
                    track.Element.Visibility = Visibility.Visible;
                }
            }
            ApplyColumnOrder();
            UpdateCornerRadii();
        }
    }

    private void UpdateCornerRadii()
    {
        var visibleTracks = _tracks.Where(t => t.CurrentWeight > 0.005 && t.Element.Visibility == Visibility.Visible).ToList();
        for (var i = 0; i < visibleTracks.Count; i++)
        {
            var track = visibleTracks[i];
            var radius = SegmentCornerRadius(i, visibleTracks.Count);
            if (track.Element is Button btn)
                btn.CornerRadius = radius;
            else if (track.Element is Border bdr)
                bdr.CornerRadius = radius;
        }
    }

    private static void UpdateTrackVisual(SegmentTrack track, UsageTimelineSegmentViewModel seg)
    {
        if (track.LabelParts is { } parts)
        {
            parts.Label.Text = seg.Label;
            parts.FullText = seg.AvailableText;
            parts.CompactText = seg.CompactAvailableText;
            ApplySegmentLabelMode(parts.LastWidth, parts);
        }

        var background = seg.IsGrayedOut
            ? GrayedOutBrush()
            : SegmentBrush(Brand.LegibleColor(seg.ProviderType));

        if (track.Element is Button btn)
        {
            btn.Background = background;
            ApplySegmentButtonResources(btn, background);
            AutomationProperties.SetName(btn, seg.AutomationName);
        }
        else if (track.Element is Border bdr)
        {
            bdr.Background = seg.IsRemainder
                ? RemainderBrush()
                : background;
            AutomationProperties.SetName(bdr, seg.AutomationName);
        }
    }

    private FrameworkElement CreateSegmentButton(
        UsageTimelineSegmentViewModel segment,
        int index,
        int count,
        out SegmentLabelParts labelParts)
    {
        var background = segment.IsGrayedOut
            ? GrayedOutBrush()
            : SegmentBrush(Brand.LegibleColor(segment.ProviderType));

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
            Content = CreateSegmentLabel(segment, out labelParts),
        };
        ApplySegmentButtonResources(button, background);

        AutomationProperties.SetAutomationId(button, $"UsageTimelineSegment_{segment.InstanceId}");
        AutomationProperties.SetName(button, segment.AutomationName);

        var partsCapture = labelParts;
        button.SizeChanged += (_, _) => ApplySegmentLabelMode(button.ActualWidth, partsCapture);
        button.PointerEntered += (_, _) => SegmentPreviewed?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));
        button.PointerExited += (_, _) => SegmentPreviewEnded?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));
        button.GotFocus += (_, _) => SegmentPreviewed?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));
        button.LostFocus += (_, _) => SegmentPreviewEnded?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));
        button.Click += (_, _) => SegmentInvoked?.Invoke(this, new UsageTimelineSegmentEventArgs(segment.InstanceId));

        return button;
    }

    private static FrameworkElement CreateSegmentSurface(
        UsageTimelineSegmentViewModel segment,
        int index,
        int count,
        out SegmentLabelParts? labelParts)
    {
        labelParts = null;
        var border = new Border
        {
            Background = segment.IsRemainder
                ? RemainderBrush()
                : segment.IsGrayedOut
                    ? GrayedOutBrush()
                    : SegmentBrush(Brand.LegibleColor(segment.ProviderType)),
            CornerRadius = SegmentCornerRadius(index, count),
        };
        AutomationProperties.SetName(border, segment.AutomationName);
        if (!segment.IsRemainder)
            border.Child = CreateSegmentLabel(segment, out labelParts);
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

        var value = new TextBlock
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
        Grid.SetRow(value, 1);
        panel.Children.Add(value);

        labelParts = new SegmentLabelParts(label, value, segment.AvailableText, segment.CompactAvailableText);
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

    private static void ApplySegmentLabelMode(double width, SegmentLabelParts? parts)
    {
        if (parts is null)
            return;

        parts.LastWidth = width;
        switch (SegmentLabelModeForWidth(width))
        {
            case SegmentLabelMode.Full:
                parts.Label.Visibility = Visibility.Visible;
                parts.Value.Visibility = Visibility.Visible;
                parts.Value.Text = parts.FullText;
                Grid.SetRow(parts.Value, 1);
                break;
            case SegmentLabelMode.PercentOnly:
                parts.Label.Visibility = Visibility.Collapsed;
                parts.Value.Visibility = Visibility.Visible;
                // Losing the provider name leaves no room for "26.8M · 4h 2m"
                // either; keep the number and drop the reset rather than ellipsize.
                parts.Value.Text = parts.CompactText;
                Grid.SetRow(parts.Value, 0);
                break;
            default:
                parts.Label.Visibility = Visibility.Collapsed;
                parts.Value.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private sealed class SegmentLabelParts
    {
        public SegmentLabelParts(TextBlock label, TextBlock value, string fullText, string compactText)
        {
            Label = label;
            Value = value;
            FullText = fullText;
            CompactText = compactText;
        }

        public TextBlock Label { get; }
        public TextBlock Value { get; }
        public string FullText { get; set; }
        public string CompactText { get; set; }

        /// <summary>
        /// Last width the segment was measured at. A value refreshed between size
        /// changes has to be re-fitted against it, or a narrow bar quietly goes back
        /// to the full text it has no room for.
        /// </summary>
        public double LastWidth { get; set; } = double.PositiveInfinity;
    }

    private static string RenderSignature(IEnumerable<UsageTimelineSegmentViewModel> segments) =>
        string.Join("|", segments.Select(segment =>
            string.Join(":", segment.InstanceId, segment.ProviderType, segment.Label, segment.AvailableText,
                segment.Weight.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                segment.ResetFrequencyText, segment.GroupKey, segment.IsRemainder, segment.IsGrayedOut,
                segment.AutomationName)));

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

    private static Brush GrayedOutBrush()
    {
        var baseColor = Color.FromArgb(0xF2, 0x48, 0x4E, 0x58);
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = ScaleColor(baseColor, 1.25, 0xF8), Offset = 0 },
                new GradientStop { Color = ScaleColor(baseColor, 1.00, 0xF2), Offset = 0.48 },
                new GradientStop { Color = ScaleColor(baseColor, 0.75, 0xF2), Offset = 1 },
            },
        };
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
}

public sealed class UsageTimelineSegmentEventArgs : EventArgs
{
    public UsageTimelineSegmentEventArgs(string instanceId)
    {
        InstanceId = instanceId;
    }

    public string InstanceId { get; }
}
