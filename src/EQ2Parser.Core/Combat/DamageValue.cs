namespace EQ2Parser.Core.Combat;

/// <summary>
/// A combat amount with ACT-compatible negative sentinel codes
/// (docs/act-behavior.md §3, "Dnum"). Positive = a real amount; the negative
/// codes carry miss/avoid/death semantics. Compatibility notes we deliberately
/// preserve:
///   * converting a number below -10 CLAMPS to Unknown (-9),
///   * equality compares the custom text too — a (-1, "Dodge") is NOT equal
///     to <see cref="Miss"/>,
///   * addition ignores negative operands (sentinels never contaminate sums).
/// </summary>
public readonly struct DamageValue : IEquatable<DamageValue>, IComparable<DamageValue>
{
    public const long NoDamageNumber = 0;
    public const long MissNumber = -1;
    public const long ResistNumber = -2;
    public const long ParryNumber = -3;
    public const long RiposteNumber = -4;
    public const long BlockNumber = -5;
    public const long UnknownNumber = -9;
    public const long DeathNumber = -10;

    public long Number { get; }
    private readonly string? _text;

    public DamageValue(long number, string? text = null)
    {
        // ACT clamps anything below the death code to Unknown.
        Number = number < DeathNumber ? UnknownNumber : number;
        _text = string.IsNullOrEmpty(text) ? null : text;
    }

    public static readonly DamageValue NoDamage = new(NoDamageNumber);
    public static readonly DamageValue Miss = new(MissNumber);
    public static readonly DamageValue Death = new(DeathNumber);
    public static DamageValue Unknown(string text) => new(UnknownNumber, text);

    public static implicit operator DamageValue(long number) => new(number);

    public bool IsDeath => Number == DeathNumber && TextOrDefault == "Death";
    public bool IsMiss => Equals(Miss);

    /// <summary>The display text; falls back to the canonical name per code.
    /// Invariant formatting on purpose: this string participates in
    /// equality/hashing, so it must not vary with the user's locale.</summary>
    public string TextOrDefault => _text ?? Number switch
    {
        > 0 => Number.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
        NoDamageNumber => "No Damage",
        MissNumber => "Miss",
        ResistNumber => "Resist",
        ParryNumber => "Parry",
        RiposteNumber => "Riposte",
        BlockNumber => "Block",
        DeathNumber => "Death",
        _ => "Unknown",
    };


    /// <summary>ACT-compatible equality: number AND display text must match.</summary>
    public bool Equals(DamageValue other) =>
        Number == other.Number && TextOrDefault == other.TextOrDefault;

    public override bool Equals(object? obj) => obj is DamageValue d && Equals(d);
    public override int GetHashCode() => HashCode.Combine(Number, TextOrDefault);

    /// <summary>Both Unknown → compare text; otherwise compare number.</summary>
    public int CompareTo(DamageValue other) =>
        Number == UnknownNumber && other.Number == UnknownNumber
            ? string.CompareOrdinal(TextOrDefault, other.TextOrDefault)
            : Number.CompareTo(other.Number);

    public static bool operator ==(DamageValue left, DamageValue right) => left.Equals(right);
    public static bool operator !=(DamageValue left, DamageValue right) => !left.Equals(right);
    public static bool operator <(DamageValue left, DamageValue right) => left.CompareTo(right) < 0;
    public static bool operator <=(DamageValue left, DamageValue right) => left.CompareTo(right) <= 0;
    public static bool operator >(DamageValue left, DamageValue right) => left.CompareTo(right) > 0;
    public static bool operator >=(DamageValue left, DamageValue right) => left.CompareTo(right) >= 0;

    public override string ToString() => TextOrDefault;
}
