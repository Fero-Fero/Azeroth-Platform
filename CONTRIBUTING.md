# Contributing

Contributions are welcome. This file covers the conventions that are easy to get wrong; the
architecture and feature reference live in [README.technical.md](./README.technical.md), and coding
standards for AI assistants live in [`.github/copilot-instructions.md`](./.github/copilot-instructions.md).

## Before opening a pull request

Use the [pull request template](./.github/pull_request_template.md). In addition:

- Follow the patterns already in the area you are changing rather than introducing a new one.
- Build and run the tests for every solution you touched:
  - `dotnet test backend/AzerothPlatform.sln`
  - `dotnet test launcher/AzerothPlatform.Launcher.sln`
  - `npm run lint && npm run test` in `frontend/` (needs Node 18 or newer)
- Write Conventional-Commits messages.
- Update the docs when you change an API or a user-visible feature.

## Comments

The full rule lives in [`.cursor/rules/comments.mdc`](./.cursor/rules/comments.mdc) and applies to
everything outside the vendored `wdbx/WDBXEditor/`. In short:

**A comment states a constraint, invariant, or consequence the code cannot express. Nothing else.**

Do write a comment for a constraint imposed from outside the code (a protocol rule, an OS behaviour, a
WoW client quirk), for an invariant a future edit could quietly break, for a consequence that is not
local, or for a non-obvious choice between two reasonable options.

Do not write:

- **History** — no `used to`, `no longer`, `previously`, `that broke`. That belongs in the commit
  message and the pull request, where it stays attached to the change. The test is tense, not
  vocabulary: a present-tense counterfactual ("`up -d` would otherwise be a no-op") describes what the
  code prevents and is fine; "this no longer returns the password" narrates a change and rots.
- **Notes to the reviewer** — no `NOTE:`, `IMPORTANT:`, `Deliberately`.
- **Restatements of the next line**, or commented-out code.

XML `<summary>` and TSDoc on public API stay, and say what the member is for in at most three lines.

## Duplicated contracts

The launcher is a separate solution and cannot reference the backend, so a handful of contracts exist
twice (`ClientManifest`, `SharedClientDataFiles`, `LauncherProfile`). Drift tests in both projects pin
the shared shape — if you change one copy, the other project's suite fails. Update both.

## Reporting bugs

Open a [bug report](https://github.com/Fero-Fero/AzerothPlatform/issues/new?template=bug_report.yml).
The form asks for server type, local vs external (including cloud provider), modules, addons, patches,
and the error or log line. Do not paste passwords, SSH keys, or cloud tokens.
