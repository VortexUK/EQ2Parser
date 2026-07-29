using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Tests;

/// <summary>Pins the ACT-compatibility traps from docs/act-behavior.md §3.</summary>
public class DamageValueTests
{
    [Fact]
    public void Conversion_Clamps_Below_Death_To_Unknown()
    {
        DamageValue v = -11L;
        Assert.Equal(DamageValue.UnknownNumber, v.Number);
    }

    [Fact]
    public void Equality_Is_String_Sensitive()
    {
        // A custom-texted avoidance on the miss code is NOT a Miss.
        Assert.True(new DamageValue(-1).Equals(DamageValue.Miss));
        Assert.False(new DamageValue(-1, "Dodge").Equals(DamageValue.Miss));
        // Same for Death.
        Assert.True(new DamageValue(-10).IsDeath);
        Assert.False(new DamageValue(-10, "Slain").IsDeath);
    }

    [Fact]
    public void Addition_Ignores_Sentinels()
    {
        Assert.Equal(300, (new DamageValue(100) + new DamageValue(200)).Number);
        Assert.Equal(100, (new DamageValue(100) + DamageValue.Miss).Number);
        Assert.Equal(100, (DamageValue.Death + new DamageValue(100)).Number);
        Assert.Equal(0, (DamageValue.Miss + DamageValue.Death).Number);
        // Zero (NoDamage) is a countable value, not a sentinel: it sums.
        Assert.Equal(100, (DamageValue.NoDamage + new DamageValue(100)).Number);
    }

    [Fact]
    public void Comparison_Uses_Text_Only_For_Two_Unknowns()
    {
        Assert.True(DamageValue.Unknown("A").CompareTo(DamageValue.Unknown("B")) < 0);
        Assert.True(DamageValue.Death.CompareTo(DamageValue.Miss) < 0);
        Assert.True(new DamageValue(5).CompareTo(DamageValue.NoDamage) > 0);
    }
}
