<!--
Pull request template for EQ2Parser.

The headings below are prompts, not required sections. Delete the ones
that don't apply.
-->

## Summary

<!-- One or two sentences: what does this PR do, and why? -->

## Linked issue

<!-- Link the discussion issue for non-trivial changes. -->

## Gates

- [ ] `dotnet build` green (warnings-as-errors, analyzers)
- [ ] `dotnet format EQ2Parser.slnx --verify-no-changes` clean
- [ ] `dotnet test` green
- [ ] New behaviour has tests

## Project-specific checks

<!-- Tick the ones that apply; delete the rest. -->

- [ ] **No ACT source copied** — behaviour derived from real logs / docs/engine-behaviour.md only (the cleanroom rule)
- [ ] **Stat-surface change** — the stat definitions (EncDPS, durations, idle rule, success levels) stay stable so site rankings remain comparable, or docs/engine-behaviour.md's "Key capabilities" documents the deliberate divergence
- [ ] **New grammar shape** — pattern added with a literal pre-guard, grammar test added, golden corpus line added
- [ ] **Core vs App placement** — testable logic landed in Core, not the App shell; `Combat` gained no upward dependency
- [ ] **New user-facing strings** — added to all four `strings.*.json` dictionaries
- [ ] **Settings shape change** — old `settings.json` files still load (init defaults / nullable fields), no silent data loss
- [ ] **Upload payload change** — wire format still matches the EQ2Lexicon ingest contract (HMAC over uncompressed JSON, snake_case fields)
- [ ] **Server-supplied content path touched** — regex timeouts / non-local path refusal / HTTPS guard preserved (see SECURITY.md)

## Out of scope / follow-ups

<!-- Anything intentionally NOT in this PR that you've noted for later. -->
