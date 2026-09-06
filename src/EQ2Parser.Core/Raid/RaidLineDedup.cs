namespace EQ2Parser.Core.Raid;

/// <summary>
/// Cross-source suppression for the raid line hook. A dual-boxing officer
/// runs two (or more) live logs, and both characters witness the same game
/// events — chest blocks, "X loots …" lines, officer chat echoes, roster
/// deltas — so feeding every source into the raid trackers delivers each
/// event once PER LOG: duplicate loot rows, double throttle counts, and a
/// doubled announcement echo that could confirm two identical charges for
/// one macro press.
///
/// The rule: an identical message from a DIFFERENT source within
/// <see cref="Window"/> (log timestamps — both clients stamp the same event
/// within a second) is the same game event and is swallowed. Identical
/// messages from the SAME source always pass: two real copies of an item
/// looted back-to-back legitimately log the same line twice in one file.
/// </summary>
public sealed class RaidLineDedup
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly List<(object Source, string Message, DateTimeOffset Time)> _recent = [];
    private readonly object _gate = new();

    /// <summary>True when this line is a fresh event this source should feed
    /// into the trackers; false when another source already delivered it.
    /// Thread-safe (sources pump on their own threads).</summary>
    public bool ShouldProcess(object source, string message, DateTimeOffset time)
    {
        lock (_gate)
        {
            _recent.RemoveAll(e => (time - e.Time).Duration() > Window);
            foreach (var e in _recent)
            {
                if (!ReferenceEquals(e.Source, source) && e.Message == message)
                    return false; // swallowed; NOT recorded, so a third source still matches the original
            }
            _recent.Add((source, message, time));
            return true;
        }
    }
}
