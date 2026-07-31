using EQ2Parser.Core.Combat;
using Microsoft.Data.Sqlite;

namespace EQ2Parser.Core.History;

public sealed record EncounterSummary(
    long Id,
    string SourceId,
    string Owner,
    string Zone,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds,
    SuccessLevel Success,
    long Damage,
    string? CorrelationId);

public sealed record CombatantSummary(
    string Name,
    long Damage,
    long Healed,
    long DamageTaken,
    long HealsTaken,
    int Deaths,
    bool IsAlly);

/// <summary>
/// Cross-session encounter history (the rich-data rule made durable): every
/// completed encounter is stored WITH its full swing list, so future app
/// features — replay, death recaps, per-ability drill-downs, re-uploads —
/// derive from stored data without re-parsing logs. WAL mode; one writer,
/// many readers. UI-free by design.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _conn;

    public HistoryStore(string path)
    {
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA foreign_keys=ON");
        CreateSchema();
    }

    public void Dispose() => _conn.Dispose();

    private void CreateSchema()
    {
        Execute($"""
            CREATE TABLE IF NOT EXISTS encounters (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id      TEXT NOT NULL,
                owner          TEXT NOT NULL,
                zone           TEXT NOT NULL,
                title          TEXT NOT NULL,
                start_ts       INTEGER NOT NULL,
                end_ts         INTEGER NOT NULL,
                duration_s     REAL NOT NULL,
                success        INTEGER NOT NULL,
                damage         INTEGER NOT NULL,
                correlation_id TEXT,
                saved_at       INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_encounters_start ON encounters(start_ts);
            CREATE INDEX IF NOT EXISTS idx_encounters_zone ON encounters(zone);
            CREATE INDEX IF NOT EXISTS idx_encounters_correlation ON encounters(correlation_id);

            CREATE TABLE IF NOT EXISTS combatants (
                encounter_id INTEGER NOT NULL REFERENCES encounters(id) ON DELETE CASCADE,
                name         TEXT NOT NULL,
                damage       INTEGER NOT NULL,
                healed       INTEGER NOT NULL,
                damage_taken INTEGER NOT NULL,
                heals_taken  INTEGER NOT NULL,
                deaths       INTEGER NOT NULL,
                is_ally      INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_combatants_encounter ON combatants(encounter_id);

            CREATE TABLE IF NOT EXISTS swings (
                encounter_id  INTEGER NOT NULL REFERENCES encounters(id) ON DELETE CASCADE,
                category      INTEGER NOT NULL,
                critical      INTEGER NOT NULL,
                special       TEXT NOT NULL,
                attacker      TEXT NOT NULL,
                ability       TEXT NOT NULL,
                damage_number INTEGER NOT NULL,
                damage_text   TEXT NOT NULL,
                time_ts       INTEGER NOT NULL,
                time_sorter   INTEGER NOT NULL,
                victim        TEXT NOT NULL,
                damage_type   TEXT NOT NULL,
                extra         TEXT,
                observed_at_ms INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_swings_encounter ON swings(encounter_id, time_sorter);

            PRAGMA user_version = {SchemaVersion};
            """);
    }

    /// <summary>Persist one completed encounter with combatant summaries and
    /// the full swing list (single transaction).</summary>
    public long SaveEncounter(Encounter encounter, string? correlationId = null)
    {
        using var tx = _conn.BeginTransaction();

        using var insertEnc = _conn.CreateCommand();
        insertEnc.Transaction = tx;
        insertEnc.CommandText = """
            INSERT INTO encounters (source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, correlation_id, saved_at)
            VALUES ($source, $owner, $zone, $title, $start, $end, $duration, $success, $damage, $correlation, unixepoch())
            RETURNING id;
            """;
        insertEnc.Parameters.AddWithValue("$source", encounter.SourceId);
        insertEnc.Parameters.AddWithValue("$owner", encounter.OwnerName);
        insertEnc.Parameters.AddWithValue("$zone", encounter.Zone);
        insertEnc.Parameters.AddWithValue("$title", encounter.Title);
        insertEnc.Parameters.AddWithValue("$start", encounter.StartTime.ToUnixTimeSeconds());
        insertEnc.Parameters.AddWithValue("$end", encounter.EndTime.ToUnixTimeSeconds());
        insertEnc.Parameters.AddWithValue("$duration", encounter.Duration.TotalSeconds);
        insertEnc.Parameters.AddWithValue("$success", (int)encounter.GetSuccessLevel());
        insertEnc.Parameters.AddWithValue("$damage", encounter.Damage);
        insertEnc.Parameters.AddWithValue("$correlation", (object?)correlationId ?? DBNull.Value);
        var encounterId = (long)insertEnc.ExecuteScalar()!;

        var allyKeys = new HashSet<string>(encounter.GetAllies().Select(a => a.Key), StringComparer.Ordinal);
        using var insertCombatant = _conn.CreateCommand();
        insertCombatant.Transaction = tx;
        insertCombatant.CommandText = """
            INSERT INTO combatants (encounter_id, name, damage, healed, damage_taken, heals_taken, deaths, is_ally)
            VALUES ($enc, $name, $damage, $healed, $taken, $healstaken, $deaths, $ally);
            """;
        foreach (var combatant in encounter.Combatants.Values)
        {
            insertCombatant.Parameters.Clear();
            insertCombatant.Parameters.AddWithValue("$enc", encounterId);
            insertCombatant.Parameters.AddWithValue("$name", combatant.Name);
            insertCombatant.Parameters.AddWithValue("$damage", combatant.Damage);
            insertCombatant.Parameters.AddWithValue("$healed", combatant.Healed);
            insertCombatant.Parameters.AddWithValue("$taken", combatant.DamageTaken);
            insertCombatant.Parameters.AddWithValue("$healstaken", combatant.HealsTaken);
            insertCombatant.Parameters.AddWithValue("$deaths", combatant.Deaths);
            insertCombatant.Parameters.AddWithValue("$ally", allyKeys.Contains(combatant.Key) ? 1 : 0);
            insertCombatant.ExecuteNonQuery();
        }

        using var insertSwing = _conn.CreateCommand();
        insertSwing.Transaction = tx;
        insertSwing.CommandText = """
            INSERT INTO swings (encounter_id, category, critical, special, attacker, ability, damage_number, damage_text, time_ts, time_sorter, victim, damage_type, extra, observed_at_ms)
            VALUES ($enc, $cat, $crit, $special, $attacker, $ability, $num, $text, $time, $sorter, $victim, $type, $extra, $observed);
            """;
        foreach (var swing in UniqueSwings(encounter))
        {
            insertSwing.Parameters.Clear();
            insertSwing.Parameters.AddWithValue("$enc", encounterId);
            insertSwing.Parameters.AddWithValue("$cat", (int)swing.Category);
            insertSwing.Parameters.AddWithValue("$crit", swing.Critical ? 1 : 0);
            insertSwing.Parameters.AddWithValue("$special", swing.Special);
            insertSwing.Parameters.AddWithValue("$attacker", swing.Attacker);
            insertSwing.Parameters.AddWithValue("$ability", swing.Ability);
            insertSwing.Parameters.AddWithValue("$num", swing.Damage.Number);
            insertSwing.Parameters.AddWithValue("$text", swing.Damage.TextOrDefault);
            insertSwing.Parameters.AddWithValue("$time", swing.Time.ToUnixTimeSeconds());
            insertSwing.Parameters.AddWithValue("$sorter", swing.TimeSorter);
            insertSwing.Parameters.AddWithValue("$victim", swing.Victim);
            insertSwing.Parameters.AddWithValue("$type", swing.DamageType);
            insertSwing.Parameters.AddWithValue("$extra", (object?)swing.Extra ?? DBNull.Value);
            insertSwing.Parameters.AddWithValue("$observed", (object?)swing.ObservedAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            insertSwing.ExecuteNonQuery();
        }

        tx.Commit();
        return encounterId;
    }

    /// <summary>Every swing exactly once: each combatant's outgoing reference
    /// bucket holds all their actions (buckets otherwise duplicate).</summary>
    private static IEnumerable<Swing> UniqueSwings(Encounter encounter) =>
        encounter.Combatants.Values
            .Where(c => c.OutgoingBuckets.ContainsKey(BucketConfig.AllOutgoingRef))
            .SelectMany(c => c.OutgoingBuckets[BucketConfig.AllOutgoingRef].All.Swings)
            .OrderBy(s => s.TimeSorter);

    public IReadOnlyList<EncounterSummary> QueryEncounters(string? zone = null, int limit = 100)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, correlation_id
            FROM encounters
            {(zone is null ? "" : "WHERE zone = $zone")}
            ORDER BY start_ts DESC LIMIT $limit;
            """;
        if (zone is not null)
            cmd.Parameters.AddWithValue("$zone", zone);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<EncounterSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EncounterSummary(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                reader.GetDouble(7), (SuccessLevel)reader.GetInt32(8), reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return results;
    }

    public IReadOnlyList<CombatantSummary> LoadCombatants(long encounterId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, damage, healed, damage_taken, heals_taken, deaths, is_ally
            FROM combatants WHERE encounter_id = $enc ORDER BY damage DESC;
            """;
        cmd.Parameters.AddWithValue("$enc", encounterId);
        var results = new List<CombatantSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CombatantSummary(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6) == 1));
        }
        return results;
    }

    /// <summary>Reload the full swing list in original order — the replay path.</summary>
    public IReadOnlyList<Swing> LoadSwings(long encounterId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT category, critical, special, attacker, ability, damage_number, damage_text, time_ts, time_sorter, victim, damage_type, extra, observed_at_ms
            FROM swings WHERE encounter_id = $enc ORDER BY time_sorter;
            """;
        cmd.Parameters.AddWithValue("$enc", encounterId);
        var results = new List<Swing>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Swing(
                (SwingCategory)reader.GetInt32(0),
                reader.GetInt32(1) == 1,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                new DamageValue(reader.GetInt64(5), reader.GetString(6)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12))));
        }
        return results;
    }

    /// <summary>Rebuild a full Encounter (buckets, combatants, title) by
    /// replaying the stored swing list through the same AddSwing path live
    /// parsing uses — a restored fight drills exactly like a live one.</summary>
    public Encounter RestoreEncounter(EncounterSummary summary)
    {
        var encounter = new Encounter(summary.SourceId, summary.Owner, summary.Zone);
        foreach (var swing in LoadSwings(summary.Id))
            encounter.AddSwing(swing);
        encounter.End();
        return encounter;
    }

    /// <summary>Retention sweep: drop every encounter that started before
    /// the cutoff (swings/combatants cascade). Returns rows removed.</summary>
    public int PruneBefore(DateTimeOffset cutoff)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM encounters WHERE start_ts < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeSeconds());
        return cmd.ExecuteNonQuery();
    }

    public bool DeleteEncounter(long encounterId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM encounters WHERE id = $enc;";
        cmd.Parameters.AddWithValue("$enc", encounterId);
        return cmd.ExecuteNonQuery() > 0;
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
