using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.App.Services;

/// <summary>
/// Picks the fight the mini parse shows and feeds it to the Core
/// <see cref="MiniParseBuilder"/>. While combat runs the engine's ACTIVE
/// encounter is the source of truth — encounters only reach the
/// correlator's history when they END, so a history-only meter lags a
/// whole fight behind. After the fight the newest correlated encounter
/// lingers, exactly how a meter should behave. Class detection is cached
/// per fight and refreshed every couple of seconds so a snapshot at 4 Hz
/// stays cheap.
/// </summary>
public sealed class MiniParseSnapshot(SourceManager manager)
{
    private object? _cachedFight;
    private Dictionary<string, string?> _classNames = [];
    private DateTimeOffset _classesRefreshed;

    public MiniParseData Build(int maxRows, string metric)
    {
        // A bulk import replays every fight as "current" — classifying and
        // row-building each one through the meters slowed imports to a
        // crawl. Pause until the reader catches up (locked overlays show
        // nothing; unlocked ones show the label).
        if (manager.ImportBusy)
            return new MiniParseData("Importing history…", "", metric, 0, []);
        lock (manager.Sync)
        {
            string title;
            TimeSpan duration;
            object cacheKey;
            Encounter classifySource;
            List<(string Key, Combatant Combatant)> members = [];
            HashSet<string> allyKeys;

            Encounter? live = null;
            foreach (var source in manager.Sources)
            {
                if (source.Engine is { InCombat: true, ActiveEncounter: { } active })
                {
                    live = active;
                    break;
                }
            }
            if (live is not null)
            {
                title = live.Title;
                duration = live.Duration;
                cacheKey = live;
                classifySource = live;
                HashSet<Combatant> allies = [.. live.GetAllies()];
                allyKeys = [];
                foreach (var (key, combatant) in live.Combatants)
                {
                    members.Add((key, combatant));
                    if (allies.Contains(combatant))
                        allyKeys.Add(key);
                }
            }
            else
            {
                var fight = manager.Correlator.History.Count > 0 ? manager.Correlator.History[^1] : null;
                if (fight is null)
                    return new MiniParseData("Waiting for combat…", "", metric, 0, []);
                title = fight.Title;
                duration = fight.Duration;
                cacheKey = fight;
                classifySource = fight.Primary;
                allyKeys = [.. fight.MergedAllyKeys];
                foreach (var (key, entry) in fight.MergedCombatants)
                    members.Add((key, entry.Combatant));
            }

            var now = DateTimeOffset.Now;
            if (!ReferenceEquals(cacheKey, _cachedFight) || now - _classesRefreshed > TimeSpan.FromSeconds(2))
            {
                _cachedFight = cacheKey;
                _classesRefreshed = now;
                _classNames = [];
                var tags = manager.Classifier.Classify(classifySource);
                foreach (var (key, combatant) in members)
                {
                    if (tags.TryGetValue(key, out var tag))
                        _classNames[combatant.Name] = tag.Class.ClassName;
                }
            }

            return MiniParseBuilder.Build(
                title, duration, metric, maxRows, members, allyKeys, _classNames,
                inCombat: live is not null);
        }
    }
}
