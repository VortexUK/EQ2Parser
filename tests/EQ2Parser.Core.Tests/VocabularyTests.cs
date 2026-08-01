using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

/// <summary>Pins the grammar ↔ vocabulary contract: every status word the
/// grammar can emit either maps into the canonical control-effect list or
/// is a KNOWN flavour-only adjective. A new word added to the grammar's
/// StatusApplied alternation without a mapping decision fails here.</summary>
public class VocabularyTests
{
    /// <summary>Every effect word EnglishGrammar can emit on a status
    /// apply: the StatusApplied alternation plus the flavour shapes
    /// (frozen/terrified/gloomy) and the silence line.</summary>
    private static readonly string[] GrammarStatusWords =
    [
        "stunned", "mesmerized", "stupified", "confused", "unnerved",
        "dazzled", "gloomy", "afraid", "feared", "disoriented", "seared",
        "dazed", "rooted", "snared", "frozen", "terrified", "silenced",
    ];

    /// <summary>Adjectives whose real mechanic the log doesn't reveal —
    /// deliberately untagged (2026-07 sweep: raid-on-mob debuff flavour).</summary>
    private static readonly string[] KnownFlavourOnly =
        ["confused", "unnerved", "gloomy", "disoriented", "seared"];

    [Fact]
    public void Every_grammar_status_word_is_mapped_or_known_flavour()
    {
        foreach (var word in GrammarStatusWords)
        {
            var canonical = Vocabulary.CanonicalControlEffect(word);
            if (canonical is null)
                Assert.Contains(word, KnownFlavourOnly);
            else
                Assert.Contains(canonical, Vocabulary.ControlEffects);
        }
    }

    [Fact]
    public void Canonical_lists_are_distinct_lowercase()
    {
        Assert.Equal(Vocabulary.ControlEffects.Count, Vocabulary.ControlEffects.Distinct().Count());
        Assert.Equal(Vocabulary.DamageSchools.Count, Vocabulary.DamageSchools.Distinct().Count());
        Assert.All(Vocabulary.ControlEffects, w => Assert.Equal(w.ToLowerInvariant(), w));
        Assert.All(Vocabulary.DamageSchools, w => Assert.Equal(w.ToLowerInvariant(), w));
    }

    [Fact]
    public void Null_and_unknown_words_stay_untagged()
    {
        Assert.Null(Vocabulary.CanonicalControlEffect(null));
        Assert.Null(Vocabulary.CanonicalControlEffect("sleepy"));
    }
}
