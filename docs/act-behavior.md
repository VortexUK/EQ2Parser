# ACT Core Engine — Behavioral Reference

**Provenance**: behavioral analysis of decompiled `Advanced Combat Tracker.exe`
v3.8.5.288 (closed source, unlicensed). This document describes *behavior* —
algorithms, thresholds, formulas, field names — in our own words. **No code
was copied and none may be**; this is the cleanroom boundary. Our engine is
implemented from this spec and validated by diffing outputs against ACT on
real logs.

**Deliberate divergences** (things ACT does that we choose NOT to reproduce):

- ACT has no log truncation/shrink detection (a shrunk file stalls its reader
  forever). Our `LogTailReader` restarts from 0 on shrink.
- ACT parses timestamps from the bracketed local-time string at fixed char
  offsets and ignores the unix epoch prefix entirely. We parse the epoch — it
  is unambiguous across locale/DST.
- ACT's `SetEncounter` fires its combat-start event with list indices that are
  wrong when a zone was inserted mid-list (a latent bug). We don't expose
  index-based identity at all.
- ACT updates stats on a separate action thread guarded by a global lock. We
  keep the engine single-threaded per log source (channel-fed), which removes
  the lock discipline for consumers.
- 1-second log granularity is disambiguated by ACT with a per-line monotonic
  `TimeSorter`; we keep that concept (it's load-bearing for ordering and
  duplicate detection).

Everything below is the observed ACT behavior our engine must either match
(stats, lifecycle, share formats) or knowingly improve on.

---

## 1. Log reading

- **Polling, not watchers** — no FileSystemWatcher anywhere. Reader thread
  checks `Length > Position` every 10 ms; a 1 s timer forces a read attempt
  regardless; folder scan for newer `eq2log*.txt` every 10 s (rollover).
- Opens with `FileShare.ReadWrite`, seeks to end — history is never replayed.
- Reads the whole pending tail, splits on CR/LF (empties removed), and holds
  back the final fragment unless the block ended with a terminator: **a line
  is never dispatched until its terminator is seen**.
- Default encoding UTF-8 (BOM detection can override). `TimeStampLen = 39`
  chars is stripped before trigger matching.
- Timestamp parse: fixed offsets into `(epoch)[Www Mmm dd hh:mm:ss yyyy]`;
  lines under 39 chars → no time. `LastKnownTime` + a stopwatch give
  `LastEstimatedTime` (interpolated log time) — spell timers tick on THIS,
  not wall time.
- If the log file exceeds the configured split size on open, ACT renames it
  to `<name>.<yyyy>.<MM>.<dd><ext>` and starts a fresh one.
- Companion logs (`CompanionLogs/<base>.<tag>.<ext>`) get their own reader
  threads that block until the main log's estimated time catches up, so
  content interleaves in rough time order.

### Line pipeline (exact order, serialized under one lock)

1. Parse time; increment global `TimeSorter`.
2. **Idle-timeout check** (may end the current encounter — before anything
   sees the line).
3. `BeforeLogLineRead` handlers — may REWRITE the line text and set a free-form
   `detectedType` int; the core reads both back.
4. Record the line into the active encounter's log (if in combat).
5. Enqueue for **custom triggers** (timestamp-stripped text) — evaluated
   asynchronously on a BelowNormal-priority thread pool (1 thread default).
6. XML-share sniff (chat lines carrying `<Trigger …/>` / `<Spell …/>`).
7. `OnLogLineRead` handlers — the game parser runs HERE, synchronously on the
   reader thread, calling `SetEncounter` + `AddCombatAction` per action.

## 2. Encounter lifecycle

- **Explicit start contract**: the parser calls
  `SetEncounter(time, attacker, victim)` before every action;
  `AddCombatAction` THROWS if not in combat. `SetEncounter` creates the
  encounter (and possibly a `ZoneData`, kept in a StartTime-ordered list),
  fires combat-start, stamps `LastHostileTime`, sets `InCombat`.
- An encounter's official `StartTimes[0]` is appended when its FIRST action
  arrives (not at SetEncounter).
- **Idle end rule**: in combat AND `now − LastHostileTime > idle limit` →
  end combat. Default **6 seconds**, on by default, checked on every new log
  line (log-time driven) with a wall-clock backstop timer at limit+2 s.
- **Silence cutting** (off unless configured): if the gap since the last
  SetEncounter exceeds a limit, close the current time segment and open a new
  one — the encounter gets multiple Start/End segments and dead air is
  excluded from Duration.
- `EndCombat`: drain pending actions → mark inactive → finalize (trim +
  title) → fire combat-end → history record → prune/culling → exports.
- **Title** = "strongest enemy": among non-ally combatants, score
  `DamageTaken / max(Deaths, 1)` (integer division), take the highest. Until
  finalize the title is the placeholder "Encounter".
- **Success level**: 0 = indeterminate (no allies / no resolvable enemy);
  else `enemyDied` = strongest enemy has Deaths > 0, `allySurvived` = some
  ally with 0 deaths whose name has no space (the "is a player" heuristic
  used throughout). Both → **1** (win); one → **2**; neither → **3**.
- Zone changes do NOT end combat by themselves.

## 3. Stat definitions (the part uploads must match)

Object graph: Zone → Encounter → Combatant (keyed UPPERCASE name) →
damage-type buckets → AttackType (per ability + synthetic "All") → swings.

**Buckets** (EQ2 defaults) with ally-polarity values: outgoing —
Auto-Attack(−1), Skill/Ability(−1), Outgoing Damage(0), Healed(+1),
Power Drain(−1), Power Replenish(+1), Cure/Dispel(0), Threat(−1),
All Outgoing (Ref)(0); mirrored incoming set. Swing types: 1 melee, 2
non-melee, 3 healing, 10 power drain, 13 power replenish, 16 threat,
20 cure/dispel. **Outgoing damage lands in TWO buckets** (its split bucket +
aggregate "Outgoing Damage"); incoming lands in one. Unknown swing types are
silently ignored except for the Ref buckets.

**Dnum** (damage value type): >0 real, 0 no-damage, −1 miss, −2 resist,
−3 parry, −4 riposte, −5 block, −6…−9 custom/unknown, −10 death. Traps:
long→Dnum conversion clamps <−10 to −9; equality compares the custom STRING
too (a `-1 "Dodge"` is NOT equal to Miss); sums ignore negative operands.

**Key stats** (per AttackType, cached incrementally):
- Damage = Σ swings > 0. Hits = count ≥ 0 (block-is-hit default) — a
  0-damage swing IS a hit. Crits require the hit threshold. Misses = count
  == Miss. "Blocked"/avoids = count in −2…−9 excluding death.
- Median = upper median of non-negative damages. MinHit uses the hit
  threshold; MaxHit strictly > 0.
- AverageDelay (default mode) = duration / (distinct timestamps − 1).

**Duration — the load-bearing definition**:
- Encounter StartTime = earliest outgoing action by ANYONE (any type — the
  Ref bucket).
- Encounter EndTime (default) = **the last DAMAGING swing by an ALLY**
  ("ShortEndTime": max over allies of their Outgoing Damage bucket end).
  Trailing heals/threat/enemy actions do NOT extend an encounter.
- Multi-segment (silence-cut) encounters sum segment durations; combatant/
  ability durations clip their own swing windows to the encounter segments.
- Combatant personal window = their own first→last outgoing action (Ref
  bucket); a combatant with only incoming actions has Duration 0.

**DPS family**: `EncDPS = Damage / encounter Duration` (this is what the
encounter tables and uploads show); `DPS = Damage / personal Duration`;
ExtDPS is a legacy alias of EncDPS. Healing mirrors as EncHPS. No
divide-by-zero guards. Encounter-level Damage/Healed sum over ALLIES only.
Allied deaths exclude space-containing names (pets/NPCs).

**Kills/Deaths**: from the localized "Killing" attack type (or death-coded
swings). Allies get kill credit for anything; non-allies only for
space-free victim names.

**Ally detection**: an incremental interaction graph — every action adds the
bucket's polarity (±1) between the two parties. Resolution seeds from the
player and sign-propagates until stable; allies = everyone whose final sign
matches the player's. **If the player isn't in the encounter, the ally set is
empty** → Damage 0, title "Encounter", success 0. (Explains the plugin-side
`ally` flag semantics and why observer-only logs parse "empty".)

## 4. Triggers + timers

**CustomTrigger fields**: regex (compiled once at construction), category
(default "General"), restrict-to-category/zone flag, active, timer flag +
timer name, sound type (0 none / 1 beep / 2 wav / 3 TTS) + sound data,
tabbed + tab age. Key = `category|regex`.

**Trigger XML share format** (`<Trigger …/>`, attributes in order):
`R` regex (escaped; `#`→`&#35;`, `\\`→`&#92;&#92;`, `\s`→`&#92;s`),
`SD` sound data, `ST` sound type int, `CR` restrict T/F, `C` category,
`T` timer T/F, `TN` timer name, `Ta` tabbed T/F. On import `R` + `C` are
required (they form the identity key; existing triggers update in place);
other attributes are individually optional. Booleans are exactly "T".

**Evaluation**: after BeforeLogLineRead, independent of parsing, against the
timestamp-stripped line, EVERY active trigger per line (no prefiltering),
async on a low-priority thread. Match semantics: a group literally named
`YOU` must contain the player's name or the match is dropped; timer action
calls NotifySpell (attacker/victim groups default "None", Self forced true);
audio rate-limited to 1/second per trigger; TTS text runs match.Result() so
`$1`/`${name}` expand. Imports never fire audio/timers.
Restrict-to-category = case-insensitive SUBSTRING test of the category
against the current zone (instance numbers stripped), rebuilt on zone change.

**Spell timer definition fields** (share format `<Spell …/>`): `N` name,
`T` duration s (default 30), `WV` warning s (10), `RV` remove at −15 s,
`A` absolute (one-only), `R` restrict-to-me, `RC` restrict-to-category,
`OM` only-master-ticks, `M` modable, `RD` radial, `FC` ARGB color,
`Tt` tooltip, `C` category, optional `SS`/`WS` start/warning sounds.
Key = lowercase `category|name`.

**Runtime timer semantics**: NotifySpell(attacker, spell, self, victim,
success) is the single entry point (every combat action calls it with the
ability name; triggers call it too). Category-restricted defs match when
category equals attacker/victim/current zone; preferred over unrestricted.
Re-trigger within 2 s of the frame's newest timer → ignored; within 12 s →
sub-timer (extra bar, no sound reset); else new master timer (sounds reset,
start sound with `${group}` expansion). Timers tick on interpolated log time.
Mods (recast/haste) sum de-duplicated by name; final = base × (1 + mods).
Owner death within 2 s of start removes their mods; dispel within 1 s.

## 5. What replaces the plugin contract

ACT's split: core owns bookkeeping; the game plugin (OnLogLineRead) turns
lines into `SetEncounter` + `AddCombatAction(swingType, critical, special,
attacker, attackType, dnum, time, timeSorter, victim, damageType)` calls.
Special is normalized ("hit"/"hits"/blank → "None"); names are trimmed and
upper-cased for keying; "Unknown" is excluded from ally bookkeeping; the
space-in-name heuristic marks NPCs/pets. `BeforeCombatAction` may mutate or
cancel an action; stats update on a separate thread afterwards.

In EQ2Parser the "plugin" is just our grammar module calling the same-shaped
engine API — the contract above defines the engine surface; the heuristics
(space-in-name, Unknown, Special normalization) are inherited because 20
years of EQ2 data conventions bake them in.
