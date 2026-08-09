# EQ2Parser

[![CI](https://github.com/VortexUK/EQ2Parser/actions/workflows/ci.yml/badge.svg)](https://github.com/VortexUK/EQ2Parser/actions/workflows/ci.yml)
[![CodeQL](https://github.com/VortexUK/EQ2Parser/actions/workflows/codeql.yml/badge.svg)](https://github.com/VortexUK/EQ2Parser/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A modern, native Windows combat parser for EverQuest II (TLE) raiding, built
around the [EQ2 Lexicon](https://eq2lexicon.com) ecosystem.

## Why

EQ2 raiders need live damage/healing parsing, triggers, and spell timers, plus
a clean path to share parses and trigger packs through EQ2 Lexicon. EQ2Parser
delivers that on a modern stack (.NET 10, WPF), with first-class Lexicon
integration (parse uploads, trigger/spell-timer pack subscriptions) built in
rather than bolted on.

## Feature goals

- **Live log parsing** — tail the EQ2 log, detect encounters, per-combatant
  damage/healing breakdowns.
- **Triggers** — regex on log lines → sound / TTS / countdown timers, with
  capture groups. Imports the community trigger XML share format so existing
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

## Contributing & Security

- Dev setup, the gates, and the PR checklist live in [CONTRIBUTING.md](CONTRIBUTING.md) — including the non-negotiable cleanroom rule.
- Architecture and key decisions are documented in [CLAUDE.md](CLAUDE.md) and [docs/engine-behaviour.md](docs/engine-behaviour.md).
- Found a security issue? See [SECURITY.md](SECURITY.md) — please report privately, not via public issues.

## License

[MIT](LICENSE).
