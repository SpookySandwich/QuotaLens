using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using QuotaLens.Core;

namespace QuotaLens.Views;

/// <summary>A severity-colored determinate quota bar. Fill width = AvailablePercent,
/// animated smoothly on change via a ScaleTransform.</summary>
public sealed partial class QuotaBar : UserControl
{
    public QuotaBar()
    {
        InitializeComponent();
        Loaded += (_, _) => AnimateTo(AvailablePercent, animate: false);
    }

    public double AvailablePercent
    {
        get => (double)GetValue(AvailablePercentProperty);
        set => SetValue(AvailablePercentProperty, value);
    }

    public static readonly DependencyProperty AvailablePercentProperty =
        DependencyProperty.Register(nameof(AvailablePercent), typeof(double), typeof(QuotaBar),
            new PropertyMetadata(0.0, OnAvailableChanged));

    public Severity Severity
    {
        get => (Severity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public static readonly DependencyProperty SeverityProperty =
        DependencyProperty.Register(nameof(Severity), typeof(Severity), typeof(QuotaBar),
            new PropertyMetadata(Severity.Good));

    private static void OnAvailableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is QuotaBar bar && bar.IsLoaded)
            bar.AnimateTo((double)e.NewValue, animate: true);
    }

    private void AnimateTo(double percent, bool animate)
    {
        var target = Math.Clamp(percent, 0, 100) / 100.0;
        if (!animate)
        {
            FillScale.ScaleX = target;
            return;
        }
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(520),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, FillScale);
        Storyboard.SetTargetProperty(anim, "ScaleX");
        new Storyboard { Children = { anim } }.Begin();
    }
}
