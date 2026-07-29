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

## EQ2 log format notes

Log lines are `(epoch)[local timestamp] message`, e.g.
`(1753738000)[Mon Jul 28 22:26:40 2026] You hit a training dummy for 100 points of crushing damage.`
Log path shape: `<install>/logs/<server>/eq2log_<character>.txt` (the ACT
plugin's LogPathParser documents the variants). Files are ANSI/UTF-8 mixed
historically — the tail reader must handle encoding defensively.
