namespace EQ2Parser.Core.Analysis;

/// <summary>A plain sRGB colour — Core is UI-free, so no WPF Color here.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// WCAG 2.x contrast maths, used to keep overlay text readable on top of
/// USER-CHOSEN colours (an ACT timer's FillColor is whatever the curator
/// picked, and the flame front blends it toward white — light text on a
/// divine-yellow or focus-cream bar was unreadable).
/// </summary>
public static class Contrast
{
    /// <summary>Relative luminance per WCAG (sRGB → linear, ITU-R BT.709
    /// weights). 0 = black, 1 = white.</summary>
    public static double RelativeLuminance(Rgb c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte channel)
    {
        var s = channel / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG contrast ratio, 1:1 (identical) to 21:1 (black on
    /// white). Symmetric — order doesn't matter.</summary>
    public static double Ratio(Rgb a, Rgb b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Composite <paramref name="fore"/> at <paramref name="alpha"/>
    /// over <paramref name="back"/> — what the eye actually sees through a
    /// semi-transparent overlay layer.</summary>
    public static Rgb Over(Rgb fore, double alpha, Rgb back)
    {
        var a = Math.Clamp(alpha, 0, 1);
        return new Rgb(Mix(fore.R, back.R, a), Mix(fore.G, back.G, a), Mix(fore.B, back.B, a));
    }

    private static byte Mix(byte fore, byte back, double alpha) =>
        (byte)Math.Round(back + (fore - back) * alpha);

    /// <summary>Linear interpolation between two colours (gradient sampling).</summary>
    public static Rgb Lerp(Rgb a, Rgb b, double t) => Over(b, t, a);

    /// <summary>The candidate that reads best on <paramref name="background"/>.
    /// Candidates are ordered by preference: an earlier one wins ties within
    /// <paramref name="tolerance"/>, so a semantic colour (warning red) is
    /// kept unless another is clearly more legible.</summary>
    public static Rgb Best(Rgb background, double tolerance, params Rgb[] candidates)
    {
        if (candidates.Length == 0)
            throw new ArgumentException("at least one candidate is required", nameof(candidates));
        var best = candidates[0];
        var bestRatio = Ratio(best, background);
        foreach (var candidate in candidates.Skip(1))
        {
            var ratio = Ratio(candidate, background);
            if (ratio > bestRatio + tolerance)
            {
                best = candidate;
                bestRatio = ratio;
            }
        }
        return best;
    }

    /// <summary>True when the pair clears WCAG AA for large/bold text (3:1)
    /// — the bar's Consolas-bold countdown.</summary>
    public static bool IsReadable(Rgb foreground, Rgb background) =>
        Ratio(foreground, background) >= 3.0;
}
