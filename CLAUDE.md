# CLAUDE.md — EQ2Parser

## What this is

A modern Windows replacement for Advanced Combat Tracker (ACT), purpose-built
for EverQuest II TLE raiding and the EQ2 Lexicon site. .NET 10, WPF (Fluent
theme), Velopack distribution, MIT. See README.md for the decision table.

Sibling repos: `E:\git\EQ2Lexicon` (the website this app uploads to),
`E:\git\EQ2LexiconACTPlugin` (the ACT plugin this app will eventually replace —
its UploadClient/PayloadSigner are the reference for the upload protocol),
`E:\git\AdvancedCombatTracker` (EQAditu's companion repo — localization + plugin
sources; the app itself is closed source).

## Hard rules

- **Cleanroom parsing**: `Plugins/Standalone/ACT_English_Parser.cs` (and the
  Russian variant) in the AdvancedCombatTracker repo are UNLICENSED — behavioural
  reference only, never copy code from them. Correctness is proven by diffing
  our output against ACT's on real logs.
- **Core stays UI-free**: `EQ2Parser.Core` must never reference WPF or any
  Windows-only UI API. Everything testable lives there; the App project is a
  thin shell.
- **Upload protocol compatibility**: same contract as the ACT plugin — bearer
  token, HMAC-SHA256 over the UNCOMPRESSED JSON in `X-Lexicon-Signature`,
  body gzipped (`Content-Encoding: gzip`). Server side lives in
  EQ2Lexicon `backend/server/api/parses/ingest.py` + `core/gzip_request.py`.
- ACT **trigger XML share-format import** is a compatibility promise; binary
  ACT plugin compatibility is explicitly NOT.
- **Match the numbers, improve everything around them**: stat definitions that
  feed visible numbers (EncDPS, durations, ally graph, success level, 6 s idle
  rule) stay ACT-compatible so site rankings remain comparable. Improvements
  (multi-log, catch-up, grammar-as-data, etc.) live in
  docs/act-behavior.md → "Improvements over ACT".
- **Multi-log architecture**: one parse pipeline per log source (own tail
  reader / grammar / perspective state), feeding an encounter correlator that
  merges concurrent encounters across sources (zone + time overlap + shared
  enemies) with per-combatant authority — a character's own log wins for that
  character. A single configurable "primary character" scopes trigger
  audio/TTS/timers. Design every engine type source-aware from the start.

## Build / test

```
dotnet build
dotnet test
```

.NET 10 SDK pinned via global.json. `Directory.Build.props` sets nullable,
warnings-as-errors, and the single `<Version>` used by Velopack releases.

## Layout

| Path | Purpose |
|---|---|
| `src/EQ2Parser.Core` | Parse engine: log tailing, line grammar, encounter model, stats, trigger + spell-timer engine. No UI deps. |
| `src/EQ2Parser.App` | WPF shell (net10.0-windows): main window, overlay, tray, settings, Velopack bootstrap. |
| `tests/EQ2Parser.Core.Tests` | xunit. Golden-file tests use real EQ2 log excerpts under `tests/logs/`. |

## Upload wiring (parser → EQ2Lexicon)

The vertical (2026-08-03): `Core/Upload/` holds the testable pieces —
`LexiconUploadClient` (transport: bearer + HMAC + gzip, injectable handler,
plus the `UrlProblem` https-only guard with a loopback dev exception),
`LexiconPayload` (wire DTOs), `PayloadBuilder` (Encounter → payload; 8-hex
content encid so re-uploads dedupe server-side), `LogPaths.ParseServerName`
(logger_server = the log's parent dir; "" for the legacy `logs/` root →
server falls back to its default world), and `UploadQueue` (channel drain off
the pump thread, bounded retry on network/5xx, 401/403 sets AuthPaused so a
bad token is never hammered).

App side: `UploadService` (thin adapter on SourceManager, wired to
`Engine.EncounterEnded` in Add/Remove exactly like History.QueueSave;
`Configure` re-reads settings), `TokenProtector` (DPAPI CurrentUser — only
ciphertext ever reaches settings.json; ProtectedData is in-box on the
windows TFM, no package), Settings → "Parse uploads" card (PasswordBox is
read+cleared in the command, never bound), and a fight-tree "Upload to
EQ2Lexicon" context item that sends every source's view of the fight
(manual upload works with the auto toggle off and clears an auth pause —
the explicit click is the consent/retry).

Uploads mirror the ACT-plugin fleet model: every finished encounter per
source uploads (trash included — the site's retention sweep handles it);
multi-log mirrors of one fight are mirror-grouped server-side by distinct
logger_names, longest duration wins as primary. Token test button hits
`/api/auth/whoami` (accepts bearer) and shows the Discord name.

Log-writer provenance (2026-08-03): `Core/Logs/LogFileHolders` probes who
holds the log file via the Windows **Restart Manager** (the supported
"which processes are using this file" API — write-handle-specific
enumeration would need undocumented NtQuerySystemInformation; rejected as
fragile). EQ2 keeps its log open for append while /log is on, so
"EverQuest2 among the holders" is the live-writer signal. `LogProvenance`
(pure, tested) turns a probe into `client_warnings`: `log_writer_eq2` /
`log_writer_unverified` + capped `log_foreign_holder:<name>` entries. The
probe runs on the UploadQueue drain thread seconds after the fight ends
(~ms cost, once per fight, never per line); manual re-uploads of archived
fights skip it (`withProvenance: false`) — probing NOW says nothing about
who wrote the log THEN. Explicit decision: NO server-side stat recompute
from raw swings — needlessly expensive; provenance stamping is the
mechanism.

## EQ2 log format notes

Log lines are `(epoch)[local timestamp] message`, e.g.
`(1753738000)[Mon Jul 28 22:26:40 2026] You hit a training dummy for 100 points of crushing damage.`
Log path shape: `<install>/logs/<server>/eq2log_<character>.txt` (the ACT
plugin's LogPathParser documents the variants). Files are ANSI/UTF-8 mixed
historically — the tail reader must handle encoding defensively.
