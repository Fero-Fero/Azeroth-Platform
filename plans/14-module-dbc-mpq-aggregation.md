# Note — Module install: aggregate DBC data and MPQ patches

**Status:** Note (not started)  
**Folder:** `plans/`  
**Scope:** Small change to how **module installation** contributes client/server data. Do not redesign the patch pipeline.

---

## Today

Modules are C++ (and optional Lua) sources. The catalog clones a git repo or unpacks a `.zip` into `custom-modules/{id}`. Selecting a module for a stack copies that tree into the AzerothCore build (`modules/`) and **rebuilds** the worldserver.

Client and server **data** live on a different path:

- Stack **Patches** (migration apply) already compile DBC CSV onto `server_dbc`, publish MPQs (`patch-D.MPQ`, overlay MPQs), SQL, maps, and `lua/`.
- Later patches overwrite earlier files in apply order.
- Modules have **no first-class `dbc/` or `mpq/` contribution**. A module that ships creature/item DBC edits or a client MPQ must be copied by hand into a patch.

That split is awkward for modules that are both code **and** content (custom items, spells, maps, UI).

---

## Desired change (slight)

When a module is installed or selected for a stack, also **aggregate** optional data folders from the module package into the same overlay the patch system already understands.

Proposed layout inside a module repo or zip (names can match patch layout):

```text
mod-example/
  src/                  # existing C++ (unchanged)
  conf/                 # existing .conf.dist (unchanged)
  dbc/                  # CSV (.txt) and/or binary .dbc — same rules as a patch
  mpq/                  # client MPQ overlays (or files the MPQ packer already accepts)
```

Aggregation order (later wins, same as patch `lua/`):

1. Server DBC baseline (`server_dbc` from a running stack)
2. **Enabled modules**, catalog / wizard order
3. Operator **patches**, apply-level order

SQL and maps from modules can wait; this note is DBC + MPQ only.

---

## Constraints

- C++ still requires a **rebuild**. DBC/MPQ aggregation should not force a rebuild by itself; apply through the existing migration/publish path (or a “sync module data” step after select).
- Direct `.dbc` uploads stay allowed; CSV still needs a captured `server_dbc` baseline.
- Do not invent a second MPQ publisher. Reuse `MigrationService` pack/publish (client manifest bump included).
- Disable/remove a module → drop its aggregated DBC/MPQ contribution and re-apply remaining modules + patches (or document that the operator must re-apply patches).
- Built-in catalog modules without `dbc/` / `mpq/` are unchanged.
- Never merge module data into the **core client** `Data/*.MPQ`; overlays only (same as launcher profiles).

---

## Likely touch points

- `ModulePackageStorage` / catalog extract: keep `dbc/` and `mpq/` (do not strip as “non-source”).
- Stack module select / build copy: besides `modules/{id}` source, register data roots for aggregation.
- Migration apply (or a sibling job): after baseline, fold module `dbc/` then patch `dbc/`; same for MPQ publish.
- UI: Modules tab hint that a package may include DBC/MPQ; Patches tab still owns operator overrides.

Keep the change small: **one extra input folder into the existing aggregator**, not a new content system.
