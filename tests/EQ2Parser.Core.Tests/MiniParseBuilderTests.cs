using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The mini parse maths — sorting, bar fractions (share of the TOP row),
/// per-second rates, row capping, and the metric switch. This fed the
/// overlays untested while it lived in the App project.
/// </summary>
public class MiniParseBuilderTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    /// <summary>A combatant with the given outgoing damage (one swing).</summary>
    private static Combatant Dealer(string name, long damage, string victim = "a gnoll")
    {
        var combatant = new Combatant(name);
        combatant.AddOutgoing(new Swing(
            SwingCategory.Melee, false, "None", name, "Strike", damage, T0, 1, victim, "crushing"));
        return combatant;
    }

    private static (List<(string, Combatant)> Members, HashSet<string> Allies) Raid(params Combatant[] allies)
    {
        List<(string, Combatant)> members = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var ally in allies)
        {
            members.Add((ally.Key, ally));
            keys.Add(ally.Key);
        }
        // An enemy is always present but never an ally — must not appear.
        members.Add(("A GNOLL", Dealer("a gnoll", 999_999, victim: "Alice")));
        return (members, keys);
    }

    private static readonly Dictionary<string, string?> NoClasses = new(StringComparer.Ordinal);

    [Fact]
    public void Rows_Sort_By_Total_And_Carry_Share_Of_Top()
    {
        var (members, allies) = Raid(Dealer("Alice", 1000), Dealer("Bea", 250), Dealer("Cara", 500));
        var data = MiniParseBuilder.Build("Boss", TimeSpan.FromSeconds(10), "DPS", 10, members, allies, NoClasses);

        Assert.Equal(["Alice", "Cara", "Bea"], data.Rows.Select(r => r.Name));
        Assert.Equal([1, 2, 3], data.Rows.Select(r => r.Rank));
        // Bar fill = share of the TOP row, not of the raid total.
        Assert.Equal(1.0, data.Rows[0].Fraction);
        Assert.Equal(0.5, data.Rows[1].Fraction);
        Assert.Equal(0.25, data.Rows[2].Fraction);
        // Per-second over the fight duration; raid rate covers every ally.
        Assert.Equal(100, data.Rows[0].Value);
        Assert.Equal(175, data.RaidValue);
        // The enemy's damage never leaks into the meter.
        Assert.DoesNotContain(data.Rows, r => r.Name == "a gnoll");
    }

    [Fact]
    public void MaxRows_Caps_The_Visible_List_But_Not_The_Raid_Rate()
    {
        var (members, allies) = Raid(Dealer("Alice", 400), Dealer("Bea", 300), Dealer("Cara", 200), Dealer("Dee", 100));
        var data = MiniParseBuilder.Build("Boss", TimeSpan.FromSeconds(10), "DPS", 2, members, allies, NoClasses);

        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(100, data.RaidValue); // all 1000 damage / 10s, hidden rows included
    }

    [Fact]
    public void Zero_Duration_Clamps_To_One_Second()
    {
        var (members, allies) = Raid(Dealer("Alice", 500));
        var data = MiniParseBuilder.Build("Boss", TimeSpan.Zero, "DPS", 10, members, allies, NoClasses);

        Assert.Equal(500, data.Rows[0].Value);
        Assert.Equal("0:00", data.DurationLabel);
    }

    [Fact]
    public void Metric_Switch_Selects_The_Right_Stat_And_Drops_Zero_Rows()
    {
        var healer = new Combatant("Sofja");
        healer.AddOutgoing(new Swing(
            SwingCategory.Healing, false, "None", "Sofja", "Mend", 300, T0, 2, "Alice", "heal"));
        var (members, allies) = Raid(Dealer("Alice", 1000), healer);

        var dps = MiniParseBuilder.Build("Boss", TimeSpan.FromSeconds(10), "DPS", 10, members, allies, NoClasses);
        var hps = MiniParseBuilder.Build("Boss", TimeSpan.FromSeconds(10), "HPS", 10, members, allies, NoClasses);

        // Zero-total rows vanish per metric: the healer from DPS, the dealer from HPS.
        Assert.Equal(["Alice"], dps.Rows.Select(r => r.Name));
        Assert.Equal(["Sofja"], hps.Rows.Select(r => r.Name));
        Assert.Equal(30, hps.Rows[0].Value);
    }

    [Fact]
    public void Class_Names_Attach_By_Display_Name()
    {
        var (members, allies) = Raid(Dealer("Alice", 100));
        var classes = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Alice"] = "Wizard" };
        var data = MiniParseBuilder.Build("Boss", TimeSpan.FromSeconds(10), "DPS", 10, members, allies, classes);

        Assert.Equal("Wizard", data.Rows[0].ClassName);
    }
}
