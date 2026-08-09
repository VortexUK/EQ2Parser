using EQ2Parser.Core.Analysis;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// WCAG contrast maths behind the overlay's readable-text picking. The
/// reference values are the spec's own: white-on-black is exactly 21:1,
/// and mid-grey #777777 has a relative luminance of ~0.1845.
/// </summary>
public class ContrastTests
{
    private static readonly Rgb White = new(0xFF, 0xFF, 0xFF);
    private static readonly Rgb Black = new(0x00, 0x00, 0x00);

    [Fact]
    public void Luminance_Anchors_Match_The_Spec()
    {
        Assert.Equal(1.0, Contrast.RelativeLuminance(White), precision: 6);
        Assert.Equal(0.0, Contrast.RelativeLuminance(Black), precision: 6);
        Assert.Equal(0.1845, Contrast.RelativeLuminance(new Rgb(0x77, 0x77, 0x77)), precision: 4);
        // Green carries most of the luminance weight, blue almost none.
        Assert.True(Contrast.RelativeLuminance(new Rgb(0, 0xFF, 0))
            > Contrast.RelativeLuminance(new Rgb(0xFF, 0, 0)));
        Assert.True(Contrast.RelativeLuminance(new Rgb(0xFF, 0, 0))
            > Contrast.RelativeLuminance(new Rgb(0, 0, 0xFF)));
    }

    [Fact]
    public void Ratio_Is_Symmetric_And_Bounded()
    {
        Assert.Equal(21.0, Contrast.Ratio(White, Black), precision: 6);
        Assert.Equal(21.0, Contrast.Ratio(Black, White), precision: 6);
        Assert.Equal(1.0, Contrast.Ratio(White, White), precision: 6);
    }

    [Fact]
    public void Over_Composites_Toward_The_Foreground()
    {
        Assert.Equal(Black, Contrast.Over(White, 0, Black));
        Assert.Equal(White, Contrast.Over(White, 1, Black));
        Assert.Equal(new Rgb(0x80, 0x80, 0x80), Contrast.Over(White, 0.5, Black));
        // Out-of-range alpha clamps rather than overshooting.
        Assert.Equal(White, Contrast.Over(White, 5, Black));
    }

    [Fact]
    public void Best_Picks_Dark_On_Light_And_Light_On_Dark()
    {
        // The real failure case: the flame front on a "divine" timer blends
        // its yellow toward white — near-white text vanished on it.
        var divineFront = new Rgb(0xFF, 0xE4, 0xA6);
        Assert.Equal(Black, Contrast.Best(divineFront, 0, White, Black));

        var deepNavy = new Rgb(0x1A, 0x1D, 0x2C);
        Assert.Equal(White, Contrast.Best(deepNavy, 0, White, Black));
    }

    [Fact]
    public void Best_Keeps_The_Preferred_Candidate_Within_Tolerance()
    {
        // Preference order matters: a semantic colour (warning red) survives
        // unless a rival is CLEARLY more legible, so the bar doesn't flip
        // hue over a rounding-level difference.
        var background = new Rgb(0x2A, 0x2A, 0x2A);
        var warmRed = new Rgb(0xF8, 0x71, 0x71);
        var nearWhite = new Rgb(0xE2, 0xE4, 0xF0);
        // Both read fine on this dark grey; near-white is ~6.3 better.
        var gap = Contrast.Ratio(nearWhite, background) - Contrast.Ratio(warmRed, background);

        Assert.Equal(warmRed, Contrast.Best(background, gap + 0.5, warmRed, nearWhite));
        Assert.Equal(nearWhite, Contrast.Best(background, gap - 0.5, warmRed, nearWhite));
    }

    [Fact]
    public void IsReadable_Uses_The_Large_Text_Threshold()
    {
        Assert.True(Contrast.IsReadable(White, Black));
        Assert.False(Contrast.IsReadable(new Rgb(0xE2, 0xE4, 0xF0), new Rgb(0xEE, 0xE6, 0xCF)));
    }

    [Fact]
    public void Lerp_Walks_From_A_To_B()
    {
        Assert.Equal(Black, Contrast.Lerp(Black, White, 0));
        Assert.Equal(White, Contrast.Lerp(Black, White, 1));
        Assert.Equal(new Rgb(0x80, 0x80, 0x80), Contrast.Lerp(Black, White, 0.5));
    }
}
