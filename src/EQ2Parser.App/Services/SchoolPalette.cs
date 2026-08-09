using System.Windows;
using System.Windows.Media;

namespace EQ2Parser.App.Services;

/// <summary>
/// Damage-school colours for the timer overlay's flame styling — the
/// dominant (first-listed) school of a timer's DamageType tag drives the
/// glow and the leading-edge burn. Untagged timers burn in their own bar
/// colour, so every bar gets the effect and the school only sets the hue.
/// </summary>
public static class SchoolPalette
{
    public static Color For(string damageTypes, Color fallback) =>
        damageTypes.Split(',')[0].Trim().ToLowerInvariant() switch
        {
            "heat" => Color.FromRgb(0xFF, 0x6B, 0x2B),
            "cold" => Color.FromRgb(0x4F, 0xC3, 0xF7),
            "magic" => Color.FromRgb(0xA7, 0x8B, 0xFA),
            "mental" => Color.FromRgb(0xF0, 0x6B, 0xD8),
            "divine" => Color.FromRgb(0xFF, 0xD7, 0x5E),
            "disease" => Color.FromRgb(0xA8, 0xC0, 0x3A),
            "poison" => Color.FromRgb(0x34, 0xD3, 0x99),
            "crushing" or "slashing" or "piercing" => Color.FromRgb(0xC8, 0xCE, 0xDC),
            "focus" => Color.FromRgb(0xEE, 0xE6, 0xCF),
            _ => fallback,
        };

    public static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    public static Color Scale(Color c, double f) => Color.FromRgb(
        (byte)Math.Clamp(c.R * f, 0, 255),
        (byte)Math.Clamp(c.G * f, 0, 255),
        (byte)Math.Clamp(c.B * f, 0, 255));

    // The flame gradient's stops, shared by the brush and the sampler so
    // the readability maths can never drift from what's drawn.
    private static (double Offset, Color Color)[] FlameStops(Color fill, Color glow) =>
    [
        (0.0, Scale(fill, 0.5)),
        (0.7, fill),
        (0.88, Blend(fill, glow, 0.6)),
        (1.0, Blend(glow, Colors.White, 0.35)),
    ];

    /// <summary>The bar fill: dark base burning brighter along the bar and
    /// tipping into the school colour at the leading edge — the flame
    /// front. The fill scales with the countdown, so the gradient (and the
    /// hot tip) ride the edge for free.</summary>
    public static Brush FlameFill(Color fill, Color glow)
    {
        LinearGradientBrush brush = new()
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
        };
        foreach (var (offset, color) in FlameStops(fill, glow))
            brush.GradientStops.Add(new GradientStop(color, offset));
        brush.Freeze();
        return brush;
    }

}
