using System.Windows;
using System.Windows.Media;

namespace EQ2Parser.App.Views;

/// <summary>
/// A circular countdown: dim full ring underneath, a coloured arc for the
/// remaining fraction sweeping clockwise from 12 o'clock (red while in the
/// warning window). Pure OnRender — cheap enough for a dozen dials at 7 Hz.
/// </summary>
public sealed class RadialDial : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(RadialDial),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RadialDial),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsWarningProperty = DependencyProperty.Register(
        nameof(IsWarning), typeof(bool), typeof(RadialDial),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Pen TrackPen = new(new SolidColorBrush(Color.FromArgb(0x52, 0x1A, 0x1D, 0x2C)), 4);

    static RadialDial()
    {
        WarningBrush.Freeze();
        TrackPen.Freeze();
    }

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public bool IsWarning
    {
        get => (bool)GetValue(IsWarningProperty);
        set => SetValue(IsWarningProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 3;
        if (radius <= 0)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(null, TrackPen, center, radius, radius);

        var fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction <= 0)
            return;
        var pen = new Pen(IsWarning ? WarningBrush : Stroke, 4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        if (fraction >= 0.999)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        Point At(double angleDegrees)
        {
            var radians = (angleDegrees - 90) * Math.PI / 180;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }

        var sweep = fraction * 360;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(0), isFilled: false, isClosed: false);
            context.ArcTo(At(sweep), new Size(radius, radius), 0,
                isLargeArc: sweep > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }
}
