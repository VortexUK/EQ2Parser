using EQ2Parser.Core.Analysis;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The post-fight hold for the mini parse meters: visible during combat and
/// for the hold window after it, hidden otherwise; 0 = never hide.
/// </summary>
public class OverlayFadeGateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);
    private static DateTimeOffset At(double s) => T0.AddSeconds(s);

    [Fact]
    public void Hidden_Before_The_Sessions_First_Fight()
    {
        var gate = new OverlayFadeGate();
        Assert.True(gate.ShouldHide(active: false, holdSeconds: 5, At(0)));
    }

    [Fact]
    public void Visible_During_Combat_And_Through_The_Hold_Window()
    {
        var gate = new OverlayFadeGate();
        Assert.False(gate.ShouldHide(active: true, holdSeconds: 5, At(0)));   // fighting
        Assert.False(gate.ShouldHide(active: false, holdSeconds: 5, At(3)));  // 3s after — held
        Assert.False(gate.ShouldHide(active: false, holdSeconds: 5, At(5)));  // exactly 5s — still held
        Assert.True(gate.ShouldHide(active: false, holdSeconds: 5, At(5.5))); // past the hold — fade
    }

    [Fact]
    public void A_New_Fight_Snaps_It_Back_And_Restarts_The_Hold()
    {
        var gate = new OverlayFadeGate();
        gate.ShouldHide(active: true, holdSeconds: 5, At(0));
        Assert.True(gate.ShouldHide(active: false, holdSeconds: 5, At(10)));  // faded out
        Assert.False(gate.ShouldHide(active: true, holdSeconds: 5, At(20)));  // next pull — instant
        Assert.False(gate.ShouldHide(active: false, holdSeconds: 5, At(24))); // fresh hold window
        Assert.True(gate.ShouldHide(active: false, holdSeconds: 5, At(26)));
    }

    [Fact]
    public void Zero_Means_Never_Hide()
    {
        var gate = new OverlayFadeGate();
        Assert.False(gate.ShouldHide(active: false, holdSeconds: 0, At(0)));
        gate.ShouldHide(active: true, holdSeconds: 0, At(1));
        Assert.False(gate.ShouldHide(active: false, holdSeconds: 0, At(9999)));
    }
}
