# Plan — Module install hooks (DBC / MPQ / extra data)

**Status:** Plan (later — do not start until the server-type definitions work is underway or done)  
**Scope:** Backend — optional install hook keyed by **module id**. If a hook exists, run its extract/filter/merge pipeline. If not, continue with the normal C++ / conf install.  
**Not this plan:** Server Wide Progression / Individual Progression sync (custom **server-type** setup; depends on `mod-individual-progression` but is not a module install). See `server-type-module-definitions-plan.md`.  
**Not this plan:** CSV / `server_dbc` baseline capture — later, you have ideas.  
**Related note:** `plans/module-dbc-mpq-aggregation.md` (layout + aggregation order; this plan is the hook mechanism).

---

## Why a hook, not a generic folder scan only

Most modules are C++ + `.conf.dist`. Those stay on the current path: clone/unpack → copy into the AzerothCore build → rebuild.

A few modules also ship **client/server data** (`dbc/`, `mpq/`, later maybe SQL/maps). Those need extra work after the package lands. Hardcoding `if (id == "mod-foo")` in `ModuleCatalogService` / `BuildService` will rot.

**Rule:** hook **id = module catalog id** (e.g. `mod-individual-progression`).

```text
Install / select module
  → existing clone/zip/copy (always)
  → look up IModuleInstallHook where HookId == module.Id
       match    → run hook pipeline (extract / filter / merge per that hook’s instructions)
       no match → continue
```

Same shape as `IModuleConfigParser` (keyed handler, missing = skip), but for **install-time data**, not conf UI.

---

## Interface (sketch)

```csharp
public interface IModuleInstallHook
{
    /// <summary>Must equal the module catalog id (e.g. "mod-ah-bot").</summary>
    string ModuleId { get; }

    Task ExecuteAsync(ModuleInstallContext context, CancellationToken cancellationToken);
}

public sealed class ModuleInstallContext
{
    public required string ModuleId { get; init; }
    public required string PackageRoot { get; init; }   // custom-modules/{id} or unpacked zip
    public required Guid? StackId { get; init; }        // null = catalog-only install
    // existing services the hook may use: migration apply, package storage, logger
}
```

Register hooks in DI (`AddScoped<IModuleInstallHook, …>`).  
`ModuleInstallHookRunner` resolves `IEnumerable<IModuleInstallHook>`, finds `ModuleId` match (ordinal ignore-case), runs **one** hook (or none). Duplicate ids → startup throw.

No hook registered → log debug and return. Do not fail the install.

---

## Default (generic) vs named hooks

Two layers, both later:

1. **Generic contribution (no hook required)**  
   If the package has `dbc/` and/or `mpq/` (see `plans/module-dbc-mpq-aggregation.md`), fold them into the existing patch aggregator:
   - baseline `server_dbc`
   - enabled modules (catalog / wizard order)
   - operator patches  
   Missing folders → no-op. This covers most content modules without a C# class.

2. **Named hook (this plan’s “match the module name”)**  
   Use when the module needs **instructions** beyond “copy `dbc/` and `mpq/`”:
   - filter which DBC tables to apply
   - remap paths
   - generate patches from repo layout that is not the standard folders
   - skip generic aggregation and do a custom merge

If a hook exists, **it owns** that module’s data path (it can call the generic aggregator internally). If not, only the generic folder scan runs (when we implement that).

---

## What a hook must not do

- **Not** Server Wide Progression / `IIndividualProgressionSyncService`. That is a custom setup on the Individual Progression **server type**. It uses the IP module as a source, but installing `mod-individual-progression` is still “C++ module + optional data folders.” The sync/bootstrap UI stays on the frontend IP definition.
- **Not** force a worldserver rebuild by itself. C++ copy still triggers rebuild; DBC/MPQ apply through the existing migration/publish path.
- **Not** write into the core client `Data/*.MPQ`. Overlays only.
- **Not** invent a second MPQ publisher.

---

## Suggested first hook (when we implement)

Start with a module that actually ships `dbc/` / `mpq/` in-repo (or a test fixture zip). Do **not** start by rewriting IP sync as a hook.

Likely touch points (from the aggregation note):

- `ModulePackageStorage` — keep `dbc/` and `mpq/` when extracting (do not strip as non-source)
- Stack module select / `BuildService` — after copy, call the hook runner
- Migration apply — extra input: module data roots, then patches
- Disable/remove module — drop that module’s contribution and re-apply remaining modules + patches

---

## Registry match (optional, same spirit as server types)

If we later list “this module has an install hook” in the catalog, a hook without a catalog module (or the reverse, if marked required) should throw at startup with the id. Until then: **missing hook = continue** is the product rule.

---

## Phases (when we pick this up)

### Phase A — Hook runner only

- [ ] `IModuleInstallHook` + runner
- [ ] Call site after successful catalog install / stack module select
- [ ] One no-op or test hook to prove match / no-match
- [ ] Duplicate `ModuleId` → startup error

### Phase B — Generic `dbc/` + `mpq/` aggregation

- [ ] Preserve folders in package extract
- [ ] Fold into existing migration apply order
- [ ] UI hint on Modules tab that a package may include client/server data

### Phase C — First real named hook

- [ ] Only when a module’s layout does not fit the generic folders
- [ ] Instructions live in that hook class (or a small sidecar next to it)

### Later (explicitly deferred)

- CSV / `server_dbc` baseline
- SQL / maps from modules
- Catalog-declared hook metadata

---

## Success criteria

- Installing a module with no hook is unchanged.
- Installing a module whose id matches a hook runs that pipeline once, then continues.
- IP Server Wide Progression is **not** implemented as a module install hook.
- Aggregation reuses the patch/MPQ pipeline; no second publisher.
