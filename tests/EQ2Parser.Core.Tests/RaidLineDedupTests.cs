using EQ2Parser.Core.Raid;

namespace EQ2Parser.Core.Tests;

/// <summary>Cross-source raid-line suppression: a dual-boxer's second log
/// witnesses the same game events and must not double-feed the trackers.</summary>
public sealed class RaidLineDedupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 25, 20, 0, 0, TimeSpan.Zero);
    private static readonly object SourceA = new();
    private static readonly object SourceB = new();
    private static readonly object SourceC = new();

    private const string Loots = @"Snugglescratch loots \aITEM 1 2:Earrings\/a from the Exquisite Chest of Elenorel.";

    [Fact]
    public void Same_Line_From_A_Second_Source_Is_Swallowed()
    {
        var d = new RaidLineDedup();
        Assert.True(d.ShouldProcess(SourceA, Loots, T0));
        Assert.False(d.ShouldProcess(SourceB, Loots, T0.AddSeconds(1))); // box char's log, a beat later
    }

    [Fact]
    public void Same_Source_Repeating_Always_Passes()
    {
        // Two REAL copies looted back-to-back log the same line twice in
        // ONE file — that is two events, not an echo.
        var d = new RaidLineDedup();
        Assert.True(d.ShouldProcess(SourceA, Loots, T0));
        Assert.True(d.ShouldProcess(SourceA, Loots, T0));
    }

    [Fact]
    public void Third_Source_Is_Swallowed_Too()
    {
        var d = new RaidLineDedup();
        Assert.True(d.ShouldProcess(SourceA, Loots, T0));
        Assert.False(d.ShouldProcess(SourceB, Loots, T0));
        Assert.False(d.ShouldProcess(SourceC, Loots, T0.AddSeconds(2)));
    }

    [Fact]
    public void Outside_The_Window_Is_A_Fresh_Event()
    {
        var d = new RaidLineDedup();
        Assert.True(d.ShouldProcess(SourceA, Loots, T0));
        Assert.True(d.ShouldProcess(SourceB, Loots, T0 + RaidLineDedup.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Different_Lines_Never_Interfere()
    {
        var d = new RaidLineDedup();
        Assert.True(d.ShouldProcess(SourceA, "24 players found", T0));
        Assert.True(d.ShouldProcess(SourceB, "23 players found", T0));
    }
}
