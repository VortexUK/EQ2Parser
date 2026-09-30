using System.Text;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// The "upload only raid fights" decision — pure, so the rule is testable
/// without the queue or the settings card. A fight counts as a raid when
/// EITHER signal fires:
///
///  * its title (the strongest enemy) is on the curated raid-boss list the
///    site publishes (GET /api/zones/raid-bosses) — catches an under-manned
///    boss attempt and a boss pulled before everyone has swung; or
///  * it had at least <see cref="RaidMinPlayers"/> player allies — the
///    site's own raid scope (a group is at most six), which keeps working
///    offline / before the first sync and covers raid trash and bosses the
///    curators have not added yet.
///
/// Boss names are compared after <see cref="NormaliseBossName"/>, a mirror
/// of the site's <c>_normalise_boss_key</c> (rankings.py): lowercase, NFC,
/// every apostrophe look-alike folded to <c>'</c>, odd spaces to a plain
/// space, trimmed. Keep the two in step or names silently stop matching.
/// </summary>
public static class RaidUploadFilter
{
    /// <summary>Player allies at or above which a fight is raid-sized. EQ2
    /// groups hold six; the site's rankings use the same floor for its
    /// "raid" scope (a real raid tallies higher once pets and swap-ins are
    /// classified out, never lower).</summary>
    public const int RaidMinPlayers = 7;

    private static readonly Dictionary<char, char> Folds = new()
    {
        ['`'] = '\'',        // U+0060 GRAVE ACCENT
        ['´'] = '\'',   // ACUTE ACCENT
        ['ʹ'] = '\'',   // MODIFIER LETTER PRIME
        ['ʺ'] = '\'',   // MODIFIER LETTER DOUBLE PRIME
        ['ʻ'] = '\'',   // MODIFIER LETTER TURNED COMMA
        ['ʼ'] = '\'',   // MODIFIER LETTER APOSTROPHE
        ['ʽ'] = '\'',   // MODIFIER LETTER REVERSED COMMA
        ['ʾ'] = '\'',   // MODIFIER LETTER RIGHT HALF RING
        ['ʿ'] = '\'',   // MODIFIER LETTER LEFT HALF RING
        ['ˈ'] = '\'',   // MODIFIER LETTER VERTICAL LINE
        ['‘'] = '\'',   // LEFT SINGLE QUOTATION MARK
        ['’'] = '\'',   // RIGHT SINGLE QUOTATION MARK
        ['‛'] = '\'',   // SINGLE HIGH-REVERSED-9 QUOTATION MARK
        ['′'] = '\'',   // PRIME
        ['＇'] = '\'',   // FULLWIDTH APOSTROPHE
        [' '] = ' ',    // NO-BREAK SPACE
        [' '] = ' ',    // THIN SPACE
    };

    /// <summary>The site's boss-key shape for a mob name (see the class
    /// summary). Idempotent; an already-normalised name passes through.</summary>
    public static string NormaliseBossName(string name)
    {
        var lowered = name.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
            sb.Append(Folds.TryGetValue(ch, out var folded) ? folded : ch);
        return sb.ToString().Trim();
    }

    /// <summary>Should this finished fight upload under the raid-only rule?</summary>
    /// <param name="title">The encounter title (strongest enemy).</param>
    /// <param name="playerAllies">Allies classified as players (pets excluded).</param>
    /// <param name="raidBosses">Normalised raid-boss names from the site, or
    /// null when nothing has been synced yet (headcount alone decides).</param>
    public static bool IsRaidEncounter(string title, int playerAllies, IReadOnlySet<string>? raidBosses)
    {
        if (playerAllies >= RaidMinPlayers)
            return true;
        return raidBosses is not null && raidBosses.Contains(NormaliseBossName(title));
    }
}
