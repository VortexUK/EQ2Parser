using EQ2Parser.Core.Logs;

namespace EQ2Parser.Core.Tests;

public class LogLineTests
{
    [Fact]
    public void Parses_A_Real_Line()
    {
        var raw = "(1753738000)[Mon Jul 28 22:26:40 2026] You hit a training dummy for 100 points of crushing damage.";
        Assert.True(LogLine.TryParse(raw, out var line));
        Assert.Equal(1753738000, line.Epoch);
        Assert.Equal("You hit a training dummy for 100 points of crushing damage.", line.Message);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1753738000), line.Timestamp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a log line")]
    [InlineData("(abc)[stamp] message")] // non-numeric epoch
    [InlineData("(123 no close paren")]
    [InlineData("(123)no bracket after")]
    [InlineData("(123)[unclosed stamp")]
    public void Rejects_Garbage_Without_Throwing(string raw)
    {
        Assert.False(LogLine.TryParse(raw, out _));
    }

    [Fact]
    public void Rejects_Partial_Tail_Read()
    {
        // A live tail can hand us the front half of a line mid-write.
        Assert.False(LogLine.TryParse("(17537380", out _));
    }
}
