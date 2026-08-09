using System.Windows.Media;
using EQ2Parser.App.ViewModels;

namespace EQ2Parser.App.Tests;

/// <summary>
/// The countdown digits must stay readable on a USER-CHOSEN bar colour.
/// The reported bug: a near-white fixed foreground disappeared into the
/// flame front of light timers (divine yellow, focus cream) while the bar
/// was still nearly full.
/// </summary>
public class TimerSecondsContrastTests
{
    private const double BarWidth = 280;

    /// <summary>Divine: bright yellow, whose flame front blends toward white.</summary>
    private static TimerBarRow BrightRow(double fraction, bool warning = false)
    {
        var row = new TimerBarRow { Fraction = fraction, IsWarning = warning };
        row.FillColor = Color.FromRgb(0xFF, 0xD7, 0x5E);
        row.GlowColor = Color.FromRgb(0xFF, 0xD7, 0x5E);
        return row;
    }

    private static Color Chosen(TimerBarRow row)
    {
        TimersViewModel.ApplyReadableSeconds(row, BarWidth);
        return ((SolidColorBrush)row.SecondsBrush).Color;
    }

    private static double Luminance(Color c) =>
        EQ2Parser.Core.Analysis.Contrast.RelativeLuminance(new(c.R, c.G, c.B));

    [Fact]
    public void Full_Bright_Bar_Gets_Dark_Digits_With_A_Light_Halo()
    {
        var row = BrightRow(fraction: 1.0);
        var color = Chosen(row);

        Assert.True(Luminance(color) < 0.1, $"expected dark digits over the flame front, got {color}");
        Assert.Equal(Colors.White, row.SecondsShadowColor);
    }

    [Fact]
    public void Shrunken_Bar_Gets_Light_Digits_On_The_Dark_Glass()
    {
        // The fill has receded well past the digits: the backdrop is the
        // card, so the established near-white reads best again.
        var row = BrightRow(fraction: 0.2);
        var color = Chosen(row);

        Assert.True(Luminance(color) > 0.5, $"expected light digits over the card, got {color}");
        Assert.Equal(Colors.Black, row.SecondsShadowColor);
    }

    [Fact]
    public void A_Dark_Bar_Keeps_Light_Digits_Even_When_Full()
    {
        // Deep blue timer: the flame front never gets bright enough to
        // beat the near-white, so nothing changes for these bars.
        var row = new TimerBarRow { Fraction = 1.0 };
        row.FillColor = Color.FromRgb(0x1E, 0x2A, 0x78);
        row.GlowColor = Color.FromRgb(0x1E, 0x2A, 0x78);

        Assert.True(Luminance(Chosen(row)) > 0.5);
    }

    [Fact]
    public void Warning_Stays_Red_But_Darkens_On_A_Bright_Bar()
    {
        var onGlass = Chosen(BrightRow(fraction: 0.2, warning: true));
        var onFlame = Chosen(BrightRow(fraction: 1.0, warning: true));

        // Both keep the warning hue (red dominant), but the one over the
        // flame front is the dark variant.
        Assert.True(onGlass.R > onGlass.G && onGlass.R > onGlass.B);
        Assert.True(onFlame.R > onFlame.G && onFlame.R > onFlame.B);
        Assert.True(Luminance(onFlame) < Luminance(onGlass));
    }

    [Fact]
    public void The_Decision_Flips_Once_As_The_Fill_Passes_The_Digits()
    {
        // A countdown crosses the digits exactly once, so the colour change
        // is a single transition rather than a per-tick flicker.
        var flips = 0;
        var row = BrightRow(fraction: 1.0);
        var previous = Chosen(row);
        for (var f = 100; f >= 0; f--)
        {
            row.Fraction = f / 100.0;
            var current = Chosen(row);
            if (current != previous)
                flips++;
            previous = current;
        }
        Assert.Equal(1, flips);
    }

    [Fact]
    public void Brush_Is_Reused_While_The_Decision_Holds()
    {
        // Guards the per-tick allocation: 4 Hz × every bar adds up.
        var row = BrightRow(fraction: 0.2);
        TimersViewModel.ApplyReadableSeconds(row, BarWidth);
        var first = row.SecondsBrush;
        row.Fraction = 0.19;
        TimersViewModel.ApplyReadableSeconds(row, BarWidth);

        Assert.Same(first, row.SecondsBrush);
    }
}
