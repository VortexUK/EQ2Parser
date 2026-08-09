using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Tests;

/// <summary>Pins the ACT-compatibility traps from docs/engine-behaviour.md §3.</summary>
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
    public void Comparison_Uses_Text_Only_For_Two_Unknowns()
    {
        Assert.True(DamageValue.Unknown("A").CompareTo(DamageValue.Unknown("B")) < 0);
        Assert.True(DamageValue.Death.CompareTo(DamageValue.Miss) < 0);
        Assert.True(new DamageValue(5).CompareTo(DamageValue.NoDamage) > 0);
    }
}
