using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The pack-merge semantics the trigger AND spell-timer stores must share:
/// user forks win key collisions, and sticky enable/disable overrides
/// re-apply in both directions across syncs.
/// </summary>
public class LexiconMergeTests
{
    private sealed record Row(string Key, bool Enabled);

    private static List<Row> Plan(
        IEnumerable<Row> pack,
        IReadOnlySet<string>? custom = null,
        IReadOnlySet<string>? disabled = null,
        IReadOnlySet<string>? enabled = null) =>
        LexiconMerge.Plan(
            pack,
            custom ?? new HashSet<string>(StringComparer.Ordinal),
            disabled ?? new HashSet<string>(StringComparer.Ordinal),
            enabled ?? new HashSet<string>(StringComparer.Ordinal),
            static r => r.Key, static r => r.Enabled, static (r, e) => r with { Enabled = e });

    [Fact]
    public void Unmodified_Rows_Pass_Through_By_Reference()
    {
        var row = new Row("a", Enabled: true);
        var planned = Plan([row]);
        Assert.Same(row, Assert.Single(planned));
    }

    [Fact]
    public void User_Fork_Wins_Key_Collisions()
    {
        var planned = Plan(
            [new Row("mine", true), new Row("theirs", true)],
            custom: new HashSet<string>(StringComparer.Ordinal) { "mine" });
        Assert.Equal("theirs", Assert.Single(planned).Key);
    }

    [Fact]
    public void User_Disable_Sticks_Over_A_CuratorEnabled_Row()
    {
        var planned = Plan(
            [new Row("a", Enabled: true)],
            disabled: new HashSet<string>(StringComparer.Ordinal) { "a" });
        Assert.False(Assert.Single(planned).Enabled);
    }

    [Fact]
    public void User_Enable_Sticks_Over_A_CuratorDisabled_Row()
    {
        // The regression the both-directions contract exists for: only
        // storing disables meant re-enabling reverted on every sync.
        var planned = Plan(
            [new Row("a", Enabled: false)],
            enabled: new HashSet<string>(StringComparer.Ordinal) { "a" });
        Assert.True(Assert.Single(planned).Enabled);
    }

    [Fact]
    public void Disable_Beats_Enable_When_Both_Are_Recorded()
    {
        // SetOverride keeps the sets disjoint, but the merge must still be
        // deterministic if a hand-edited overrides file lists both.
        var planned = Plan(
            [new Row("a", Enabled: true)],
            disabled: new HashSet<string>(StringComparer.Ordinal) { "a" },
            enabled: new HashSet<string>(StringComparer.Ordinal) { "a" });
        Assert.False(Assert.Single(planned).Enabled);
    }
}

/// <summary>Trigger.WithEnabled — the Core-owned copy the stores use.</summary>
public class TriggerWithEnabledTests
{
    [Fact]
    public void Copies_Every_Field_And_Shares_The_Compiled_Pattern()
    {
        var original = new Trigger(@"^(?<who>\w+) is stunned", "Boss", "Deathtoll")
        {
            Enabled = false,
            RestrictToCategoryZone = true,
            SoundType = TriggerSound.Tts,
            SoundData = "stun on ${who}",
            StartsTimer = true,
            TimerName = "Stun",
            AudioCooldown = TimeSpan.FromSeconds(5),
            Source = "lexicon",
        };
        var enabled = original.WithEnabled(true);

        Assert.True(enabled.Enabled);
        Assert.Same(original.Pattern, enabled.Pattern);
        Assert.Equal(original.Key, enabled.Key);
        Assert.Equal(original.PrefilterLiteral, enabled.PrefilterLiteral);
        Assert.Equal(
            (original.RegexText, original.Category, original.Zone, original.RestrictToCategoryZone,
             original.SoundType, original.SoundData, original.StartsTimer, original.TimerName,
             original.AudioCooldown, original.Source),
            (enabled.RegexText, enabled.Category, enabled.Zone, enabled.RestrictToCategoryZone,
             enabled.SoundType, enabled.SoundData, enabled.StartsTimer, enabled.TimerName,
             enabled.AudioCooldown, enabled.Source));
    }
}
