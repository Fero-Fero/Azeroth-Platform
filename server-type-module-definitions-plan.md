# Plan — Server-type definitions as the product center

**Status:** Phase 1 implemented (string ids and catalog-exposed addon ids still later)  
**Scope:** Frontend (and catalog) — one definition file per server type: wizard notices, recommended addons, setup workflow, and other operator-facing information.  
**Out of scope:** Module DBC/MPQ install hooks — see `module-install-hooks-plan.md`. CSV/`server_dbc` baseline work — later.  
**Related:** `stack-setup-workflows-plan.md` (Overview setup — implemented), `ServerTypeCatalogOptions`, `ModulesStep.tsx`, `AddonService` (`atlas-loot-individual-progression`).

---

## Decisions (from review)

- **`recommendedAddonIds` live on the server type** and are rendered by a shared component that looks up the addon catalog (name, description, install link). Not hardcoded GitHub markup in `ModulesStep`.
- **Server Wide Progression Setup** is a **custom setup** (`src/setup/custom-setups/serverWideProgression.ts`). The **mod-individual-progression module setup calls it**. The IP server type lists it for wizard copy and sequences those steps around the playerbots pipeline. Not a module install hook.
- **Every server type gets a definition file**, even if most slots are empty — types will grow copy, requirements, and addons over time.
- **Backend module extract/merge** is a **separate plan** (`module-install-hooks-plan.md`).
- **Monolith is acceptable** if the folder is neat (one file per type, shared renderers).
- **Catalog data is not duplicated** in frontend consts (repos, required modules, visibility stay on the API).
- **Frontend definition registry and backend catalog must match.** A missing or extra id throws (dev/startup), naming the id.

---

## Target model

```text
frontend/src/server-types/
  index.ts                         # public barrel
  types.ts
  definitions/                     # one file per server type
    standard.ts
    playerbots.ts
    individualProgression.tsx
    npcBots.ts
    custom.ts
  notices/                         # shared wizard UI
    ServerTypeSlot.tsx
    RecommendedAddonsNotice.tsx
    CustomSetupNotice.tsx
  registry/                        # catalog match + setup wiring
    registry.ts
    defaultSetup.ts
    useAssertServerTypeRegistry.ts

Wizard ModulesStep / ReviewStep / Addons tab
  └── registry[serverType]
        ├── wizardModulesNotice(ctx)
        ├── recommendedAddonIds[]     → RecommendedAddonsNotice
        ├── requiredModuleIds         → from API catalog (not copied)
        ├── customSetups[]            → e.g. Server Wide Progression
        └── buildSetupSteps           → Overview pipeline (re-export existing)
```

`ModulesStep` has **no** `serverType === …`. It renders slots from the selected definition.

---

## Template

```typescript
export type WizardNoticeContext = {
  serverType: string
  selectedModuleIds: string[]
  browseTab: 'curated' | 'community'
}

export type CustomSetup = {
  id: string
  /** Shown in wizard / overview copy */
  title: string
  description: string
  /** Module ids that must be installed for this setup to appear */
  requiresModuleIds: string[]
}

export type ServerTypeDefinition = {
  id: string
  wizardModulesNotice?: (ctx: WizardNoticeContext) => React.ReactNode
  wizardReviewNotes?: (ctx: WizardNoticeContext) => React.ReactNode
  /** Addon catalog ids — rendered dynamically from the addons API */
  recommendedAddonIds: string[]
  /** Named workflows that are not modules (e.g. Server Wide Progression) */
  customSetups?: CustomSetup[]
  buildSetupSteps: SetupWorkflowBuilder
}
```

### Recommended addons (per server type, dynamic)

```typescript
// individualProgression.ts
recommendedAddonIds: ['atlas-loot-individual-progression']
```

`RecommendedAddonsNotice` fetches `/addons` (or uses the existing addon list), resolves each id, and renders name + description + install/website. Unknown id → visible error (“Addon catalog is missing `atlas-loot-individual-progression`”).

Dungeon Clear stays **module-related** (`RelatedModuleIds` on the addon), not an IP server-type addon, unless you explicitly add it to that type’s list.

### Custom setup: Server Wide Progression

Not a module. Not an install hook.

```typescript
customSetups: [
  {
    id: 'server-wide-progression',
    title: 'Server Wide Progression Setup',
    description: 'Bootstrap progression, sync from mod-individual-progression, apply patches in order.',
    requiresModuleIds: ['mod-individual-progression'],
  },
]
```

- Wizard notice: show this setup when `mod-individual-progression` is selected (it is required for the type today, so it always shows for IP).
- Module: `individualProgressionModuleSteps()` calls `serverWideProgressionSetup.buildSteps()`.
- Overview: the IP server type sequences those module-provided steps with the playerbots pipeline. Playerbots disable/re-enable stay **module** steps, only if `mod-playerbots` is selected.
- Backend: keep `IIndividualProgressionSyncService`. Do not fold this into module install hooks.

### Individual Progression wizard notice

```typescript
wizardModulesNotice: ({ selectedModuleIds, browseTab }) => {
  if (browseTab !== 'curated') return null
  return (
    <Notice>
      {selectedModuleIds.includes('mod-playerbots') && (
        <p>After create you will be asked to disable playerbots before first launch…</p>
      )}
      <RecommendedAddonsNotice ids={definition.recommendedAddonIds} />
      <CustomSetupNotice setups={definition.customSetups} selectedModuleIds={selectedModuleIds} />
    </Notice>
  )
}
```

---

## Registry match (frontend ↔ backend)

Concern 5 was **not** “backend vs backend.” It was: we already have `frontend/src/setup/server-types/` and were about to add `frontend/src/server-types/` — two lists to keep in sync.

**Fix:** one frontend registry. Setup builders are fields on the same definition (re-export the existing `*.setup.ts` files).

**Match rule (your request):**

On app load (and in a unit test):

1. Fetch backend `GET` server types (catalog).
2. For every catalog `id`, a frontend `ServerTypeDefinition` **must** exist.
3. For every frontend definition `id`, a catalog entry **must** exist (except a documented allowlist if any).
4. Mismatch → **throw** with a clear message:  
   `Server type "Foo" exists in the API catalog but has no frontend definition in server-types/.`  
   or the reverse.

Same idea later for `recommendedAddonIds` vs addon catalog ids.

---

## Why the `ServerType` enum does not scale — and how to fix it

**Today** the id is a **closed C# enum** plus a **closed TypeScript enum**. Adding type #6 means:

- `ServerType.cs`
- `frontend/src/types/stack.types.ts`
- catalog entry
- JSON/EF bindings
- every `switch` / `Record<ServerType, …>`
- a frontend definition file

The catalog is already the real list (enable/disable, repo, required modules). The enums are a **second, compile-time allowlist** that cannot grow from config.

**Fix (do this as part of this plan, not “someday”):**

1. **API and database store a string id** (`"IndividualProgression"`). If the column is already the enum name as string, keep it. If it is an integer, migrate to the name.
2. **C#:** `ServerTypeDefinition.Id` is `string`. Keep a `static class ServerTypeIds { public const string IndividualProgression = "IndividualProgression"; }` for references in catalog defaults and tests. Remove the requirement that every new type is an enum member.
3. **TypeScript:** `type ServerTypeId = string` plus `export const ServerType = { IndividualProgression: 'IndividualProgression', … } as const` for known ids. Wizard value is `string`.
4. **Allowlist = catalog.** Unknown id from a client → 400. Disabled catalog entry → hidden in wizard, existing stacks still load.
5. **Frontend file** still required (your rule: every type has a definition). Adding a type = catalog row + one frontend file. No enum edit. Registry match test fails if you forget the file.

`Custom` stays a real type (`allowCustomRepository: true`). New forks that are first-class products get their own string id + file, not “use Custom.”

---

## Single source of truth

| Concern | Source |
|---------|--------|
| Display name, description, icon, core repo, required/bundled modules, visibility | Backend `Configuration/ServerTypes/` (API) |
| Wizard notices, custom setups, recommended **addon ids**, setup pipeline | Frontend `server-types/<id>.ts` |
| Addon name, URL, install | Addon catalog (resolved from ids) |
| Module env defaults (AH bot GUIDs) | Frontend `modules/mod-ah-bot.ts` until catalog grows |

---

## Implementation phases

### Phase 1 — Definition template + IP/AH bot

- [x] `frontend/src/server-types/` — `definitions/`, `notices/`, `registry/`
- [x] One file per existing type (Standard, Playerbots, IP, NpcBots, Custom)
- [x] IP: `recommendedAddonIds: ['atlas-loot-individual-progression']`
- [x] IP: `customSetups: [server-wide-progression]` requiring `mod-individual-progression` (module setup calls `buildSteps()`)
- [x] IP notice: playerbots sentence only if `mod-playerbots` is selected
- [x] Move `MODULE_ENV_DEFAULTS` to `setup/steps/modules/envDefaults.ts`
- [x] Single frontend registry (`src/server-types/`); deleted `setup/server-types/`
- [x] Registry match test: frontend ids ↔ default catalog ids; runtime assert vs API
- [x] `ModulesStep` / `ReviewStep`: notices from `ServerTypeSlot` / `ServerTypeReviewNotes`

### Phase 2 — String server-type ids

- [ ] API/DTO: `id: string`
- [ ] C# `ServerTypeIds` constants; catalog `Id` is string
- [ ] TS `ServerTypeId`; keep const map for known ids
- [ ] Migrate stored stack `ServerType` if numeric
- [ ] Unknown catalog id without a frontend file → throw (same match rule)

### Phase 3 — Catalog-exposed addon ids (optional)

- [ ] `RecommendedAddonIds` on `ServerTypeInfoDto` if you want operators to edit addons without a frontend deploy
- [ ] Frontend still supplies notice **copy**; ids can come from API and merge with the definition

---

## Success criteria

- `ModulesStep.tsx` has no `serverType ===` checks.
- Recommended addons for a type are an id list + shared renderer (addon catalog).
- Server Wide Progression is a custom setup invoked by the IP **module** setup, gated on `mod-individual-progression`, not a module install hook.
- Playerbots wizard text only when that module is selected.
- One frontend registry; mismatch with the API catalog throws with the missing id.
- New server type = catalog string id + one frontend file (after Phase 2, no enum change).
