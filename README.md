# EQ2Parser

A modern, native Windows replacement for Advanced Combat Tracker, purpose-built
for EverQuest II (TLE) raiding and the [EQ2 Lexicon](https://eq2lexicon.com)
ecosystem.

## Why

ACT is a 20-year-old closed-source .NET Framework application. It works, but it
can't be modernized from outside, its plugin model is frozen on net48, and the
features EQ2 raiders actually use are a fraction of its surface. EQ2Parser
reimplements that working set on a modern stack, with first-class EQ2 Lexicon
integration (parse uploads, trigger/spell-timer pack subscriptions) built in
rather than bolted on.

## Feature goals

- **Live log parsing** — tail the EQ2 log, detect encounters, per-combatant
  damage/healing breakdowns.
- **Triggers** — regex on log lines → sound / TTS / countdown timers, with
  capture groups. Imports ACT's trigger XML share format so existing community
  triggers keep working.
- **Spell timers** — countdown window driven by parsed casts and triggers.
- **Overlay** — click-through "mini parse" window over the game.
- **Death recap, offline import, local history** (SQLite).
- **EQ2 Lexicon native**: one-click parse upload (token + HMAC + gzip), and
  per-encounter trigger packs subscribed straight from the site.

Deliberately out of scope: multi-game support, binary ACT-plugin compatibility,
ODBC/FTP exports, LCD hardware, embedded web servers.

## Architecture decisions (2026-07-29)

| Decision | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (LTS) | Current LTS; C# matches the team + all reference material. |
| UI | WPF with the built-in Fluent theme | Battle-tested; native Windows 11 look since .NET 9; overlays/tray are well-trodden. WinUI 3 judged too rough for a solo long-lived project. |
| Distribution | [Velopack](https://velopack.io) | One-click installer + delta auto-updates from GitHub Releases, no admin rights. |
| Storage | SQLite | Local encounter history; same operational comfort zone as the Lexicon server. |
| Licence | MIT | Community trust + contributions. |
| Parse grammar | Cleanroom | ACT's parser sources are unlicensed (all rights reserved); they serve as a behavioural reference only. Correctness is validated by diffing against ACT output on real logs. |

## Solution layout

```
src/EQ2Parser.Core/    Parse engine + trigger/timer engine. No UI dependencies.
src/EQ2Parser.App/     WPF application (net10.0-windows).
tests/                 xunit suites; golden-file tests against real log excerpts.
```

## Building

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK (pinned via `global.json`).
