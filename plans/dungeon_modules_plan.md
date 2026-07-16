# Dungeon Clear + Dungeon Sim modules

Small plan to add [mod-dungeon-clear](https://github.com/jrad7/mod-dungeon-clear) and [mod-playerbot-dungeon-sim](https://github.com/TopHatMan/mod-playerbot-dungeon-sim) to the module catalog, enforce their dependency in the wizard and stack modules UI, and suggest the companion client addon when dungeon-clear is installed.

---

## Goals

| Requirement | Design choice |
|---|---|
| Catalog both server modules | Add built-in entries in `ModuleCatalogService` |
| Dungeon-sim requires dungeon-clear | Selecting sim auto-selects clear; clear cannot be deselected while sim is selected |
| Companion addon | When `mod-dungeon-clear` is on the stack, surface [mod-dungeon-clear-addon](https://github.com/jrad7/mod-dungeon-clear-addon) on the Addons tab |
| Playerbots prerequisite | Both modules only make sense with Playerbots — hide on NPCBots server type; document playerbots requirement |

---

## Module catalog entries

Add to `backend/AzerothPlatform.Infrastructure/Services/ModuleCatalogService.cs` → `BuiltInModules`:

| Id | Name | Repository | Branch | Notes |
|---|---|---|---|---|
| `mod-dungeon-clear` | Dungeon Clear | `https://github.com/jrad7/mod-dungeon-clear.git` | `master` | Autonomous tank-led 5-man clears for playerbots |
| `mod-playerbot-dungeon-sim` | Playerbot Dungeon Sim | `https://github.com/TopHatMan/mod-playerbot-dungeon-sim.git` | `main` | Offscreen raid sim + real 5-man ladder; **requires** `mod-dungeon-clear` |

Descriptions should mention:
- **Dungeon Clear** — tank bot drives the party boss-to-boss; pairs with the DungeonClear client addon.
- **Dungeon Sim** — progression engine for random bots (dungeon ladder + sim raids); requires Dungeon Clear.

### Server-type visibility

Add rules in `ServerTypeCatalogOptions.Defaults.ModuleRules`:

```csharp
// Both bot systems conflict — same pattern as mod-playerbots.
new ModuleVisibilityRule
{
    ModuleId = "mod-dungeon-clear",
    HiddenForServerTypes = [ServerType.NpcBots]
},
new ModuleVisibilityRule
{
    ModuleId = "mod-playerbot-dungeon-sim",
    HiddenForServerTypes = [ServerType.NpcBots]
}
```

No `VisibleForServerTypes` allowlist — show on Standard (with playerbots module), Playerbots fork, Individual Progression, and Custom. Playerbots is bundled on the Playerbots server type so dungeon modules appear as optional add-ons there.

### Operator caveat (document in module description)

[mod-playerbot-dungeon-sim](https://github.com/TopHatMan/mod-playerbot-dungeon-sim) calls `DungeonClearControl::StartAutonomousClear(...)`, which is **not** in upstream jrad7/mod-dungeon-clear today — it exists only on a fork with autonomous bot runs enabled. Until that lands upstream, operators who enable Dungeon Sim may need a repository override (custom catalog entry or `ServerTypeCatalog` `RepositoryOverrides`) pointing at a fork that exports `StartAutonomousClear`. Catalog the jrad7 URL as requested; flag the fork requirement in the sim module description and in stack setup warnings.

Dungeon Sim also ships SQL (`playerbot_dungeon_progression` on the **characters** DB) and expects `BotActivityRegistry.h` to stay in sync with related mods — note in setup warnings, not auto-applied by the platform today.

---

## Module dependency model (new)

### Backend contract

Extend `ModuleDto` (`backend/.../ModuleDto.cs` + `frontend/src/types/stack.types.ts`):

```csharp
/// <summary>Other module ids that must be selected when this module is selected.</summary>
public List<string> RequiredModuleIds { get; set; } = new();
```

Populate on built-in entries:

- `mod-dungeon-clear` → `[]` (no hard platform dependency; playerbots is a soft/runtime prerequisite)
- `mod-playerbot-dungeon-sim` → `["mod-dungeon-clear"]`

Expose via existing `GET /api/modules/catalog` — no new endpoint.

### Validation

In `StackConfigurationValidator.ValidateModulesAsync`:

- If `mod-playerbot-dungeon-sim` ∈ `moduleIds` and `mod-dungeon-clear` ∉ `moduleIds` → error on `moduleIds`: *"Playerbot Dungeon Sim requires the Dungeon Clear module."*

Optional follow-up (not required for v1): also error when dungeon-clear is selected on a server type without playerbots (bundled or `mod-playerbots` selected).

### Shared dependency helper (frontend)

Add `frontend/src/lib/module-dependencies.ts`:

```ts
export const MODULE_REQUIRED_BY: Record<string, string[]> = {
  'mod-playerbot-dungeon-sim': ['mod-dungeon-clear'],
}

export function applyModuleToggle(
  moduleId: string,
  selectedIds: string[],
  adding: boolean,
): string[] { /* add deps on enable; block/remove dependents on disable */ }
```

Mirror the backend list in catalog DTOs so a single source of truth lives server-side; the TS helper is a thin client mirror for instant UX (backend still validates on save).

---

## Module selection UI

Touch both pickers — they share the same toggle pattern today:

| File | Role |
|---|---|
| `frontend/src/components/wizard/steps/ModulesStep.tsx` | Wizard module step |
| `frontend/src/components/modules/StackModulesTab.tsx` | Existing stack → Modules tab |

### Behaviour

**Enable `mod-playerbot-dungeon-sim`**
1. Add `mod-dungeon-clear` to selection if missing (auto-select).
2. Optionally inject env default when sim is enabled (see below).

**Disable `mod-dungeon-clear`**
- If `mod-playerbot-dungeon-sim` is selected → **ignore** the click (checkbox stays checked).
- Show a short hint on the dungeon-clear row: *"Required by Playerbot Dungeon Sim"*.

**Disable `mod-playerbot-dungeon-sim`**
- Allow; dungeon-clear may stay selected (operator choice).

**Visual**
- When a module is locked as a dependency, render the checkbox as checked + disabled (or show a small lock icon / muted row). Do not use a separate "required" badge on sim — the auto-select + lock on clear is enough.

### Env defaults (optional, small)

When enabling dungeon-sim, seed worldserver module config if we add parsers later. Short term, document in `ModuleSetupWarnings` that autonomous sim runs need `DungeonClear.AllowAutonomousBotRuns = 1` in dungeon-clear config. Defer `ModuleConfigService` entry until someone needs in-UI editing.

---

## Companion addon suggestion

### Catalog entry

Add to `AddonService.BuiltInAddons`:

| Field | Value |
|---|---|
| Id | `dungeon-clear-addon` |
| Name | Dungeon Clear |
| Category | UI |
| DownloadUrl | `https://github.com/jrad7/mod-dungeon-clear-addon/archive/refs/heads/master.zip` |
| Website | `https://github.com/jrad7/mod-dungeon-clear-addon` |
| Folders | `["DungeonClear"]` |
| RelatedModuleIds | `["mod-dungeon-clear"]` |

### Install folder rename

The GitHub archive root is `mod-dungeon-clear-addon-master`, but the WoW client **must** load the folder as `DungeonClear` ([addon README](https://github.com/jrad7/mod-dungeon-clear-addon)). `InstallArchive` today uses the detected `.toc` parent folder name as-is.

Add optional `InstallAsFolder` on `AddonCatalogEntryDto`. When set, after detecting the addon root, move it to `Interface/AddOns/{InstallAsFolder}` instead of the archive folder name. Set `InstallAsFolder = "DungeonClear"` for this entry.

### Conditional “Suggested” on Addons tab

Extend `AddonCatalogEntryDto`:

```csharp
public List<string> RelatedModuleIds { get; set; } = new();
public bool Suggested { get; set; }  // computed per stack in GetCatalogAsync
```

In `AddonService.GetCatalogAsync(stackId)`:
1. When `stackId` is set, load the stack's `configuration.moduleIds`.
2. For each catalog entry with `RelatedModuleIds`, set `Suggested = true` when any related module id is installed (and entry not already installed).

In `AddonsManager.tsx`:
- Sort suggested entries first (after global `recommended`).
- Show a **Suggested** badge (distinct colour from **Recommended**) when `entry.suggested`.
- Optional one-line callout at top of catalog section when any suggested addon is not installed: *"Dungeon Clear is installed — consider adding the Dungeon Clear addon for in-game control."*

Global addons page (`stackId` omitted) keeps today’s behaviour — no module-aware suggestion.

---

## Implementation order

1. **Catalog** — `ModuleCatalogService` entries + `ServerTypeCatalog` visibility rules.
2. **Contract** — `RequiredModuleIds` on `ModuleDto` (backend + frontend types).
3. **Validator** — sim-without-clear error in `StackConfigurationValidator`.
4. **UI** — dependency toggle + lock in `ModulesStep` and `StackModulesTab` (shared helper).
5. **Addon** — catalog entry, `InstallAsFolder`, `RelatedModuleIds` + `Suggested` in `GetCatalogAsync`.
6. **Addons UI** — suggested badge, sort, optional callout in `AddonsManager`.
7. **Polish** — `ModuleSetupWarnings` line when dungeon-sim is selected (fork + SQL + `AllowAutonomousBotRuns`).

---

## Files to touch

| Area | Files |
|---|---|
| Module catalog | `ModuleCatalogService.cs`, `ModuleDto.cs`, `stack.types.ts` |
| Visibility | `ServerTypeCatalogOptions.cs` |
| Validation | `StackConfigurationValidator.cs` |
| Module UI | `ModulesStep.tsx`, `StackModulesTab.tsx`, new `module-dependencies.ts` |
| Addon catalog | `AddonDtos.cs`, `AddonService.cs` |
| Addon UI | `AddonsManager.tsx`, addon types in frontend if mirrored |
| Warnings | `ModuleSetupWarnings.tsx` (optional) |

---

## Test plan

| Check | How |
|---|---|
| Catalog lists both modules | API `GET /api/modules/catalog?serverType=Playerbots` |
| NPCBots hides both | Same API with `serverType=NpcBots` |
| Validator rejects sim without clear | Unit test on `StackConfigurationValidator` |
| Selecting sim auto-adds clear | Manual wizard + stack modules tab |
| Clear locked while sim selected | Manual — cannot uncheck clear |
| Suggested addon when clear installed | Stack with `mod-dungeon-clear` → Addons tab shows Dungeon Clear addon as Suggested |
| Addon installs as `DungeonClear` | Install from catalog; verify folder name under client `Interface/AddOns` |

---

## Out of scope (for this plan)

- Forked mod-dungeon-clear repository override baked into defaults (operator uses custom module or config override).
- Automatic application of dungeon-sim SQL migrations on stack deploy.
- `ModuleConfigService` parsers for `mod_dungeon_clear.conf` / `mod_playerbot_dungeon_sim.conf`.
- Bundling dungeon-clear into the Playerbots core fork.
