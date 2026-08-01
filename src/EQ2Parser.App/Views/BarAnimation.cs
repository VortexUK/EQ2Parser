using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EQ2Parser.App.Views;

/// <summary>
/// Attached fraction that GLIDES: setting it eases the element's
/// ScaleTransform.ScaleX to the new value instead of snapping, so meter
/// bars slide as numbers change and timer bars drain smoothly between
/// ticks. The element's RenderTransform must be a ScaleTransform.
/// </summary>
public static class BarAnimation
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.RegisterAttached(
        "Fraction", typeof(double), typeof(BarAnimation), new PropertyMetadata(0.0, OnFractionChanged));

    public static double GetFraction(DependencyObject d) => (double)d.GetValue(FractionProperty);

    public static void SetFraction(DependencyObject d, double value) => d.SetValue(FractionProperty, value);

    private static void OnFractionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || element.RenderTransform is not ScaleTransform scale)
            return;
        // During template instantiation the declared ScaleTransform can
        // still be the template's SHARED FROZEN instance (WPF only clones
        // per element lazily) — animating that throws. Take our own copy.
        if (scale.IsFrozen)
        {
            scale = scale.Clone();
            element.RenderTransform = scale;
        }
        var animation = new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
    }
}
