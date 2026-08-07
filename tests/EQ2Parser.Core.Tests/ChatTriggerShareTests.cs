using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

/// <summary>Chat trigger-share detection: another player pastes the ACT
/// share snippet into group/raid/tells and the app offers it.</summary>
public class ChatTriggerShareTests
{
    private const string Snippet =
        """<Trigger R="a dangerous device appears" SD="Move out" ST="3" CR="F" C="General" T="F" TN="" Ta="F" />""";

    [Fact]
    public void Extracts_A_Share_From_Player_Chat()
    {
        var message = $"""\aPC -1 Martyn:Martyn\/a says to the raid party, "{Snippet}" """;
        var share = ChatTriggerShare.TryExtract(message);
        Assert.NotNull(share);
        Assert.Equal("Martyn", share.Sharer);
        Assert.False(share.Self);
        Assert.Equal("a dangerous device appears", share.Trigger.RegexText);
        Assert.Equal("Move out", share.Trigger.SoundData);
        Assert.Equal(TriggerSound.Tts, share.Trigger.SoundType);
    }

    [Fact]
    public void Own_Paste_Is_Flagged_Self()
    {
        var share = ChatTriggerShare.TryExtract($"""You say to the group, "{Snippet}" """);
        Assert.NotNull(share);
        Assert.True(share.Self);
    }

    [Theory]
    // NPC "chat" never counts.
    [InlineData("""\aNPC 123 Impostor:Impostor\/a says, "<Trigger R="x" SD="y" ST="3" CR="F" C="G" T="F" TN="" Ta="F" />" """)]
    // Malformed / truncated snippet.
    [InlineData("""\aPC -1 M:M\/a says to the group, "<Trigger R="broken" """)]
    // Ordinary chat mentioning the word.
    [InlineData("""\aPC -1 M:M\/a says to the group, "use the Trigger page" """)]
    // Combat line.
    [InlineData("Mayong Mistmoore hits Sofja for 5,000 divine damage.")]
    public void Non_Shares_Are_Ignored(string message) =>
        Assert.Null(ChatTriggerShare.TryExtract(message));

    private static string Raw(long epoch, string message) =>
        $"({epoch})[Sat Aug 1 20:44:48 2026] {message}";

    [Fact]
    public void Processor_Raises_For_Live_Foreign_Shares_Only()
    {
        var engine = new ParserEngine("log", "Sofja");
        var processor = new LogLineProcessor(engine);
        List<SharedTrigger> seen = [];
        processor.TriggerShared += seen.Add;

        var foreign = Raw(100, $"""\aPC -1 Martyn:Martyn\/a says to the raid party, "{Snippet}" """.TrimEnd());
        var own = Raw(101, $"""You say to the group, "{Snippet}" """.TrimEnd());

        // Live (no ObservedAt = live per the freshness rule).
        Assert.True(LogLine.TryParse(foreign, out var liveLine));
        processor.Process(liveLine);
        // Own paste: skipped.
        Assert.True(LogLine.TryParse(own, out var ownLine));
        processor.Process(ownLine);
        // Replayed history (ObservedAt far after log time): skipped.
        Assert.True(LogLine.TryParse(foreign, out var stale));
        stale = stale with { ObservedAt = DateTimeOffset.FromUnixTimeSeconds(100).AddHours(2) };
        processor.Process(stale);

        var share = Assert.Single(seen);
        Assert.Equal("Martyn", share.Sharer);
    }
}
