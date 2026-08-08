using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using QuotaLens.ViewModels;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace QuotaLens.Views;

/// <summary>
/// Renders a single provider as a Fluent card. The actual layout is chosen by a
/// <see cref="CardTemplateSelector"/> keyed on the VM's <see cref="CardKind"/>; we
/// re-run the selector whenever Kind changes (a ContentControl only re-selects on
/// a Content reference change, not on a property change of the same object).
/// </summary>
public sealed partial class ProviderCard : UserControl
{
    private const double CardCornerRadius = 8;
    private const double TimelinePreviewOffsetY = -2;
    private const double TimelinePulseOffsetY = -6;
    private const double TimelinePreviewDepth = 12;
    private const double TimelinePulseDepth = 32;
    private const double TimelinePreviewContourOpacity = 0.42;
    private const double TimelinePulseContourOpacity = 0.78;
    private const double TimelineAttentionFrameMilliseconds = 16;
    private static readonly TimeSpan MinimumShimmerVisibleDuration = TimeSpan.FromSeconds(1);

    private DateTimeOffset? _shimmerShownAt;
    private DateTimeOffset _timelineAttentionStartedAt;
    private int _shimmerTransitionVersion;
    private int _timelineHighlightVersion;
    private DispatcherQueueTimer? _timelineAttentionTimer;
    private ThemeShadow? _timelineAttentionShadow;
    private Vector3 _timelineAttentionStartTranslation;
    private Vector3 _timelineAttentionTargetTranslation;
    private double _timelineAttentionStartContourOpacity;
    private double _timelineAttentionTargetContourOpacity;
    private double _timelineAttentionDurationMilliseconds;

    public ProviderCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        ShimmerFadeOutStoryboard.Completed += OnShimmerFadeOutCompleted;
    }

    public ProviderItemViewModel? ViewModel
    {
        get => (ProviderItemViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(ProviderItemViewModel), typeof(ProviderCard),
            new PropertyMetadata(null, OnViewModelChanged));

    public void PulseTimelineAttention()
    {
        PulseTimelineHighlight();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ProviderCard)d;
        if (e.OldValue is ProviderItemViewModel oldVm)
            oldVm.PropertyChanged -= card.OnVmPropertyChanged;
        if (e.NewValue is ProviderItemViewModel newVm)
            newVm.PropertyChanged += card.OnVmPropertyChanged;

        card.SetShimmerActive(card.ViewModel?.IsShimmerLoadingActive == true, animate: false);
        card.SetTimelineHighlightActive(card.ViewModel?.IsTimelineHighlighted == true, animate: false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProviderItemViewModel.Kind))
        {
            // Force the ContentControl to re-evaluate the template selector.
            var vm = CardHost.Content;
            CardHost.Content = null;
            CardHost.Content = vm;
        }
        else if (e.PropertyName == nameof(ProviderItemViewModel.IsShimmerLoadingActive))
        {
            SetShimmerActive(ViewModel?.IsShimmerLoadingActive == true, animate: true);
        }
        else if (e.PropertyName == nameof(ProviderItemViewModel.IsTimelineHighlighted))
        {
            SetTimelineHighlightActive(ViewModel?.IsTimelineHighlighted == true);
        }
        else if (e.PropertyName == nameof(ProviderItemViewModel.TimelineHighlightPulseVersion))
        {
            PulseTimelineHighlight();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureTimelineAttentionShadow();
        UpdateClip();
        SetShimmerActive(ViewModel?.IsShimmerLoadingActive == true, animate: false);
        SetTimelineHighlightActive(ViewModel?.IsTimelineHighlighted == true, animate: false);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateClip();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _shimmerTransitionVersion++;
        ShimmerStoryboard.Stop();
        ShimmerFadeInStoryboard.Stop();
        ShimmerFadeOutStoryboard.Stop();
        _timelineHighlightVersion++;
        StopTimelineAttentionAnimation(reset: true);
        ShimmerOverlay.Opacity = 0;
        ShimmerOverlay.Visibility = Visibility.Collapsed;
        TimelineAttentionContour.Opacity = 0;
        TimelineAttentionContour.Visibility = Visibility.Collapsed;
        ShimmerSurface.Data = null;
        _shimmerShownAt = null;
    }

    private void SetTimelineHighlightActive(bool isActive, bool animate = true)
    {
        _timelineHighlightVersion++;
        AnimateTimelineAttention(
            isActive ? TimelinePreviewOffsetY : 0,
            isActive ? TimelinePreviewDepth : 0,
            isActive ? TimelinePreviewContourOpacity : 0,
            TimeSpan.FromMilliseconds(animate ? isActive ? 140 : 220 : 0));
    }

    private async void PulseTimelineHighlight()
    {
        var version = ++_timelineHighlightVersion;
        AnimateTimelineAttention(
            TimelinePulseOffsetY,
            TimelinePulseDepth,
            TimelinePulseContourOpacity,
            TimeSpan.FromMilliseconds(120));

        await Task.Delay(700);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _timelineHighlightVersion)
                return;

            var isHighlighted = ViewModel?.IsTimelineHighlighted == true;
            AnimateTimelineAttention(
                isHighlighted ? TimelinePreviewOffsetY : 0,
                isHighlighted ? TimelinePreviewDepth : 0,
                isHighlighted ? TimelinePreviewContourOpacity : 0,
                TimeSpan.FromMilliseconds(700));
        });
    }

    private void EnsureTimelineAttentionShadow()
    {
        if (_timelineAttentionShadow is not null)
            return;

        _timelineAttentionShadow = new ThemeShadow();
        _timelineAttentionShadow.Receivers.Add(CardShadowReceiver);
        AttentionSurface.Shadow = _timelineAttentionShadow;
    }

    private void AnimateTimelineAttention(double offsetY, double depth, double contourOpacity, TimeSpan duration)
    {
        EnsureTimelineAttentionShadow();

        var targetTranslation = new Vector3(0, (float)offsetY, (float)depth);
        TimelineAttentionContour.Visibility = Visibility.Visible;

        _timelineAttentionTimer?.Stop();
        _timelineAttentionStartTranslation = new Vector3(
            0,
            (float)AttentionSurfaceLiftTransform.Y,
            AttentionSurface.Translation.Z);
        _timelineAttentionTargetTranslation = targetTranslation;
        _timelineAttentionStartContourOpacity = TimelineAttentionContour.Opacity;
        _timelineAttentionTargetContourOpacity = contourOpacity;
        _timelineAttentionDurationMilliseconds = duration.TotalMilliseconds;
        _timelineAttentionStartedAt = DateTimeOffset.Now;

        if (duration <= TimeSpan.Zero)
        {
            ApplyTimelineAttention(targetTranslation, contourOpacity);
            return;
        }

        _timelineAttentionTimer ??= CreateTimelineAttentionTimer();
        _timelineAttentionTimer.Start();
    }

    private DispatcherQueueTimer CreateTimelineAttentionTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(TimelineAttentionFrameMilliseconds);
        timer.IsRepeating = true;
        timer.Tick += OnTimelineAttentionTick;
        return timer;
    }

    private void OnTimelineAttentionTick(DispatcherQueueTimer sender, object args)
    {
        var elapsed = (DateTimeOffset.Now - _timelineAttentionStartedAt).TotalMilliseconds;
        var progress = _timelineAttentionDurationMilliseconds <= 0
            ? 1
            : Math.Clamp(elapsed / _timelineAttentionDurationMilliseconds, 0, 1);
        var easedProgress = EaseOutCubic(progress);

        ApplyTimelineAttention(
            LerpVector(_timelineAttentionStartTranslation, _timelineAttentionTargetTranslation, easedProgress),
            Lerp(_timelineAttentionStartContourOpacity, _timelineAttentionTargetContourOpacity, easedProgress));

        if (progress < 1)
            return;

        sender.Stop();
        ApplyTimelineAttention(_timelineAttentionTargetTranslation, _timelineAttentionTargetContourOpacity);
    }

    private void ApplyTimelineAttention(Vector3 translation, double contourOpacity)
    {
        AttentionSurfaceLiftTransform.Y = translation.Y;
        AttentionSurface.Translation = new Vector3(0, 0, translation.Z);
        TimelineAttentionContour.Opacity = contourOpacity;
        if (contourOpacity > 0)
        {
            TimelineAttentionContour.Visibility = Visibility.Visible;
            return;
        }

        if (ViewModel?.IsTimelineHighlighted != true)
            TimelineAttentionContour.Visibility = Visibility.Collapsed;
    }

    private void StopTimelineAttentionAnimation(bool reset)
    {
        _timelineAttentionTimer?.Stop();
        if (!reset)
            return;

        AttentionSurfaceLiftTransform.Y = 0;
        AttentionSurface.Translation = Vector3.Zero;
        TimelineAttentionContour.Opacity = 0;
        TimelineAttentionContour.Visibility = Visibility.Collapsed;
    }

    private static double EaseOutCubic(double progress) =>
        1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);

    private static double Lerp(double from, double to, double progress) =>
        from + ((to - from) * progress);

    private static Vector3 LerpVector(Vector3 from, Vector3 to, double progress) =>
        new(
            (float)Lerp(from.X, to.X, progress),
            (float)Lerp(from.Y, to.Y, progress),
            (float)Lerp(from.Z, to.Z, progress));

    private void UpdateClip()
    {
        SetRoundedRectanglePath(ShimmerSurface, CardRoot.ActualWidth, CardRoot.ActualHeight, CardCornerRadius);
    }

    private void SetShimmerActive(bool isActive, bool animate)
    {
        _shimmerTransitionVersion++;

        if (isActive)
        {
            UpdateClip();
            var wasVisible = ShimmerOverlay.Visibility == Visibility.Visible;
            ShimmerOverlay.Visibility = Visibility.Visible;
            _shimmerShownAt ??= DateTimeOffset.Now;
            ShimmerStoryboard.Begin();

            if (animate && !wasVisible)
            {
                ShimmerFadeOutStoryboard.Stop();
                ShimmerOverlay.Opacity = 0;
                ShimmerFadeInStoryboard.Begin();
            }
            else
            {
                HoldShimmerAtFullOpacity();
            }

            return;
        }

        if (ShimmerOverlay.Visibility != Visibility.Visible)
        {
            HideShimmerImmediately();
            return;
        }

        if (!animate)
        {
            HideShimmerImmediately();
            return;
        }

        var shownAt = _shimmerShownAt ?? DateTimeOffset.Now;
        var remaining = MinimumShimmerVisibleDuration - (DateTimeOffset.Now - shownAt);
        if (remaining > TimeSpan.Zero)
        {
            var version = _shimmerTransitionVersion;
            _ = FadeOutShimmerAfterDelayAsync(version, remaining);
            return;
        }

        BeginShimmerFadeOut();
    }

    private async Task FadeOutShimmerAfterDelayAsync(int version, TimeSpan delay)
    {
        await Task.Delay(delay);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _shimmerTransitionVersion && ViewModel?.IsShimmerLoadingActive != true)
                BeginShimmerFadeOut();
        });
    }

    private void OnShimmerFadeOutCompleted(object? sender, object e)
    {
        if (ViewModel?.IsShimmerLoadingActive == true)
            return;

        HideShimmerImmediately();
    }

    private void HideShimmerImmediately()
    {
        ShimmerOverlay.Opacity = 0;
        ShimmerFadeInStoryboard.Stop();
        ShimmerFadeOutStoryboard.Stop();
        ShimmerStoryboard.Stop();
        ShimmerOverlay.Opacity = 0;
        ShimmerOverlay.Visibility = Visibility.Collapsed;
        _shimmerShownAt = null;
    }

    private void BeginShimmerFadeOut()
    {
        if (ViewModel?.IsShimmerLoadingActive == true)
            return;

        HoldShimmerAtFullOpacity();
        ShimmerFadeOutStoryboard.Begin();
    }

    private void HoldShimmerAtFullOpacity()
    {
        ShimmerOverlay.Opacity = 1;
        ShimmerFadeInStoryboard.Stop();
        ShimmerFadeOutStoryboard.Stop();
    }

    private static void SetRoundedRectanglePath(XamlPath path, double width, double height, double radius)
    {
        if (width <= 0 || height <= 0)
            return;

        path.Width = width;
        path.Height = height;

        var r = Math.Min(radius, Math.Min(width, height) / 2);
        var figure = new PathFigure
        {
            StartPoint = new Point(r, 0),
            IsClosed = true,
        };

        figure.Segments.Add(new LineSegment { Point = new Point(width - r, 0) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(width, r),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        });
        figure.Segments.Add(new LineSegment { Point = new Point(width, height - r) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(width - r, height),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        });
        figure.Segments.Add(new LineSegment { Point = new Point(r, height) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(0, height - r),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        });
        figure.Segments.Add(new LineSegment { Point = new Point(0, r) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(r, 0),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        path.Data = geometry;
    }
}
