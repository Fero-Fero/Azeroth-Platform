# Implementation order

Open work after the 2026-08-17 clean slate. Cloud login (plans 02–08 and follow-up gaps) is shipped.

| Order | Plan | Notes |
|-------|------|--------|
| — | [15-clean-slate-cleanup.md](../plans/15-clean-slate-cleanup.md) | Implemented. Dual-write / manager player paths / V1 layouts removed. |
| 1 | [16-clean-slate-cleanup-changelog.md](../plans/16-clean-slate-cleanup-changelog.md) | Changelog + test checklist for 15. Apply `DropCustomEnvVarsJson` then walk the checklist. |
| 2 | [13-cloud-followups-and-test.md](../plans/13-cloud-followups-and-test.md) | Remaining cloud test/hardening follow-ups. |
| 3 | [14-module-dbc-mpq-aggregation.md](../plans/14-module-dbc-mpq-aggregation.md) | Module DBC/MPQ aggregation. |
| — | [11-windows-os-support.md](11-windows-os-support.md) | Support: Windows OS. |
| — | [12-armory-styling-injection.md](12-armory-styling-injection.md) | Support: armory styling injection. |

Do not treat config update `Skip` / `Merge` / `Fresh`, runtime artifact versioning, or cloud VPC firewall as leftover compat.
