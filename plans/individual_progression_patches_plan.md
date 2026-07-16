# Individual Progression + patch orchestration

Plan to wire **mod-individual-progression** into module selection and the patch system so expansion progression is guided, config stays in sync, and release content can be imported from GitHub zips.

Upstream reference: [Grimfeather/mod-individual-progression](https://github.com/Grimfeather/mod-individual-progression) — `ProgressionState` enum in [`IndividualProgression.h`](https://github.com/Grimfeather/mod-individual-progression/blob/master/src/IndividualProgression.h).

---

## Goals

| Requirement | Design choice |
|---|---|
| Module picker guidance | When `mod-individual-progression` is in `moduleIds`, notify that progression is managed via **Stack → Game → Patches** |
| Patches empty-state CTA | If IP module is installed and no patches applied, offer **Apply server-wide progression** (prepare only — no auto-apply) |
| Bootstrap | Set Classic `Expansion = 0`, IP `StartingProgression = 1`, `ProgressionLimit = 1`, lock TBC race keys at `8`; seed 19 patch templates — admin applies patches manually |
| Server Progression tab | New sub-tab on Patches page: conf keys grepped from module `.conf`, editable key names + values |
| Per-patch sync | Each patch apply increments IP counters **except** `1.0 [START]`; first TBC patch sets `Expansion = 1`, first WotLK sets `Expansion = 2` |
| Patch catalog | Replace placeholder patches with one template per `ProgressionState` (Classic `1.x`, TBC `2.x`, WotLK `3.x`); `1.0` = START |
| Release import | New **merge import** accepts separate SQL + MPQ release zips into the correct patch folders |
| Release download | Configurable array of GitHub release URLs — **stub until links provided**, then update config |

---

## Current state (codebase)

### What exists

| Piece | Location | Notes |
|---|---|---|
| Patch system | `MigrationService`, `PatchesTab.tsx` | Full CRUD, incremental apply, zip import (`append` / `override`) |
| Default placeholders | `MigrationLayout.DefaultPatches` | `patch 1`–`patch 4` (empty Classic/TBC/WotLK/Custom roots) |
| Patch index scheme | `PatchIndex` | Root `1`=Classic, `2`=TBC, `3`=WotLK; sub-segments `1.1`, `1.1.1` |
| Game tab | `StackDetailsPage.tsx` | **Modules · Patches · Lua Scripts** under Game |
| Server config editor | `ServerConfigService` | Reads/writes `{builds}/{stackId}/azerothcore-wotlk/env/dist/etc/*.conf` |
| Module config seeding | `ServerConfigService.SeedMissingModuleConfigs` | Copies `*.conf.dist` → `*.conf` from built image |
| IP module catalog | `mod-individual-progression` | Visible only for `IndividualProgression` server type; Grimfeather repo override |
| Module notifications | `ModulesStep`, `StackModulesTab`, `ModuleSetupWarnings` | Pattern from dungeon-clear / AH-bot work |

### What is missing

- No link between patch apply and `worldserver.conf` `Expansion`
- No link between patch apply and `individual_progression.conf` (or equivalent) keys
- No `ProgressionState` catalog in the platform
- Placeholder patches are not IP-aware
- Import expects a single archive in patch-folder layout — no **merge** of separate SQL + MPQ release trees
- `IndividualProgression.h` is not vendored; may exist on disk only after a stack build

---

## ProgressionState → patch mapping

Source enum ([`IndividualProgression.h`](https://raw.githubusercontent.com/Grimfeather/mod-individual-progression/master/src/IndividualProgression.h)):

```cpp
enum ProgressionState : uint8
{
    PROGRESSION_START           = 0,  // Classic
    PROGRESSION_MOLTEN_CORE     = 1,
    PROGRESSION_ONYXIA          = 2,
    PROGRESSION_BLACKWING_LAIR  = 3,
    PROGRESSION_PRE_AQ          = 4,
    PROGRESSION_AQ_WAR          = 5,
    PROGRESSION_AQ              = 6,
    PROGRESSION_NAXX40          = 7,
    PROGRESSION_PRE_TBC         = 8,  // TBC
    PROGRESSION_TBC_TIER_1      = 9,
    PROGRESSION_TBC_TIER_2      = 10,
//  PROGRESSION_TBC_TIER_3      = 11, // skipped in upstream
    PROGRESSION_TBC_TIER_4      = 12,
    PROGRESSION_TBC_TIER_5      = 13,
    PROGRESSION_WOTLK_TIER_1    = 14, // WotLK
    PROGRESSION_WOTLK_TIER_2    = 15,
    PROGRESSION_WOTLK_TIER_3    = 16,
    PROGRESSION_WOTLK_TIER_4    = 17,
    PROGRESSION_WOTLK_TIER_5    = 18,
};
```

### Patch index naming

Use existing `PatchIndex` roots; **display name** includes the enum slug; **folder key** follows `patch {index} [{name}]`:

| Group | ProgressionState values | Patch index | Display label example | Increments IP counters on apply? |
|---|---|---|---|---|
| Classic | 0–7 | `1.0` … `1.7` | `patch 1.0 [START]` … `patch 1.7 [NAXX40]` | **No** for `1.0 [START]`; **yes** for `1.1`–`1.7` |
| TBC | 8–13 (skip 11) | `2.0` … `2.4` | `patch 2.0 [PRE_TBC]` … `patch 2.4 [TBC_TIER_5]` | Yes |
| WotLK | 14–18 | `3.0` … `3.4` | `patch 3.0 [WOTLK_TIER_1]` … `patch 3.4 [WOTLK_TIER_5]` | Yes |

**Note:** Today `PatchIndex` treats a lone root (`1`, `2`, `3`) as expansion-tier patches. IP progression uses **sub-segment `.0`** (`1.0`, `2.0`, `3.0`) as the first content patch per era. Confirm `PatchIndex` parsing accepts `N.0` (it should — `sub1 = 0` is valid). Remove/replace the four placeholder expansion roots (`patch 1`–`patch 3`) when seeding the IP template set.

### ProgressionState metadata (new static catalog)

Add `IndividualProgressionPatchCatalog.cs` (Infrastructure):

```csharp
public sealed record ProgressionPatchDefinition(
    int State,           // ProgressionState value
    string Slug,         // e.g. MOLTEN_CORE
    string Expansion,    // classic | tbc | wotlk
    string Index,        // e.g. 1.1
    string Title,        // human label
    string Description);
```

**Resolve `IndividualProgression.h` source order:**

1. `{builds}/{stackId}/azerothcore-wotlk/modules/mod-individual-progression/src/IndividualProgression.h` (post-build)
2. `{builds}/{stackId}/modules/mod-individual-progression/src/IndividualProgression.h` (if alternate layout)
3. Fetch raw from Grimfeather GitHub (`master` branch, pinned commit optional)
4. Fall back to baked-in static catalog (same table as above) if parse fails

Parser: regex `PROGRESSION_(\w+)\s*=\s*(\d+)` inside the `ProgressionState` enum block; skip commented lines; validate monotonic ids.

---

## Config sync model

### Eligibility

All IP features (bootstrap CTA, Server Progression tab, apply hooks, release import) require **`mod-individual-progression` ∈ `stack.configuration.moduleIds`**. Server type alone is **not** sufficient — the module must be explicitly installed on the stack.

### Files to edit

| File | Keys |
|---|---|
| `worldserver.conf` | `Expansion` (`0` Classic, `1` TBC, `2` WotLK) — key name configurable in Server Progression tab |
| Module conf (path from `.conf.dist`, e.g. `modules/individual_progression.conf`) | IP progression keys — names grepped from conf, editable in UI |

### IP config keys (default mapping — seeded from conf grep)

| Logical field | Default conf key | Bootstrap value | After patch apply | Never change after bootstrap |
|---|---|---|---|---|
| Starting progression | `IndividualProgression.StartingProgression` | `1` | `+1` (except START) | — |
| Progression limit | `IndividualProgression.ProgressionLimit` | `1` | `+1` (except START) | — |
| TBC races unlock | `IndividualProgression.TbcRacesUnlockProgression` | `8` | — | locked at `8` |
| TBC races starting | `IndividualProgression.TbcRacesStartingProgression` | `8` | — | locked at `8` |

**Key discovery:** On first load (bootstrap or Server Progression tab open), grep the module's effective `.conf` / `.conf.dist` for lines matching `IndividualProgression.* =` and populate the key-name fields. If a expected logical field has no match, show an empty editable field for the operator to fill in. Key names are persisted per stack so renames in upstream conf do not break sync.

### Expansion auto-update rules

| Event | `Expansion` value |
|---|---|
| Bootstrap (server-wide progression) | `0` |
| `1.0 [START]` applied | `0` (no IP counter increment) |
| First TBC patch applied (`2.0` / `PRE_TBC`) | `1` |
| First WotLK patch applied (`3.0` / `WOTLK_TIER_1`) | `2` |
| Later patches within same era | unchanged |

### Stack-persisted progression settings (new)

Store on stack configuration (or dedicated JSON column):

```typescript
interface IndividualProgressionSettings {
  bootstrapped: boolean
  moduleConfPath: string           // e.g. modules/individual_progression.conf
  worldserverConfPath: string      // worldserver.conf
  expansionKey: string             // Expansion
  keys: {
    startingProgression: string
    progressionLimit: string
    tbcRacesUnlockProgression: string
    tbcRacesStartingProgression: string
  }
  // Optional: cached current values for UI display
  values?: Record<string, string>
}
```

### New service: `IndividualProgressionSyncService`

Responsibilities:

1. **`BootstrapAsync(stackId)`** — called by Patches CTA
   - Grep module conf → seed `IndividualProgressionSettings.keys`
   - Write bootstrap values to worldserver + module conf
   - Replace placeholder patches with 19 IP templates + `progression.json`
   - **Does not** run the apply pipeline — admin imports content and applies patches manually
2. **`OnPatchAppliedAsync(stackId, patchIndex, progressionState)`** — hook from `MigrationService.Apply.cs` after `persist-level`
   - If `progressionState === 0` (START): update `Expansion` only if needed; **do not** increment `StartingProgression` / `ProgressionLimit`
   - Otherwise: increment both counters by 1
   - Update `Expansion` when crossing `2.0` / `3.0`
   - Use key names from `IndividualProgressionSettings` (not hardcoded strings)
   - Persist via `ServerConfigService`
   - Log lines in apply result
3. **`DiscoverConfigKeysAsync(stackId)`** — grep conf files, return key/value pairs for Server Progression tab
4. **`SaveSettingsAsync(stackId, settings)`** — persist operator-edited key names and values

Conf editing: extend `ServerConfigService` with `SetConfigValueAsync(stackId, relativePath, key, value)` that preserves comments and unrelated lines (line-based `Key = Value` replace).

Store bootstrap completion: `IndividualProgressionSettings.bootstrapped = true` hides the Patches bootstrap CTA.

---

## UI changes

### 1. Module selection notification

**When:** `mod-individual-progression` is in `moduleIds` (wizard `ModulesStep` + stack `StackModulesTab`).

**What:** Info callout (blue/violet, non-blocking):

> Individual Progression unlocks server-wide progression patches. Visit **Stack → Game → Patches** to bootstrap progression and apply content releases.

Link: `/stacks/{stackId}?tab=patches` (stack tab) or wizard text without link until stack exists.

**Files:** `ModulesStep.tsx`, `StackModulesTab.tsx` (inline banner above module list when IP selected).

### 2. Patches page — bootstrap CTA

**When:**

- `stack.configuration.moduleIds` includes `mod-individual-progression` (**required**)
- `AppliedPatchLevel === 0` (no patches applied)
- Not currently applying a patch

**What:** Top-of-page card in `PatchesTab.tsx`:

> **Individual Progression** — No progression patches applied yet.  
> **Apply server-wide progression** prepares Classic-era config, discovers conf keys, and installs the progression patch template set. You apply patches yourself when ready.

Button triggers `POST /api/stacks/{id}/migrations/individual-progression/bootstrap`. **Does not** auto-apply any patch.

**Follow-up:** Patch list grouped Classic / TBC / WotLK with enum slugs; `patch 1.0 [START]` marked **Next** for the admin to import content and apply manually.

### 3. Patches page — Server Progression tab (new)

Add a third inner tab on the Patches page (alongside patch list / detail workflow):

**Server Progression** — visible only when `mod-individual-progression` ∈ `moduleIds`.

| Section | Content |
|---|---|
| Conf key mapping | Editable text fields for each logical setting (starting progression, progression limit, TBC race keys, expansion key). Pre-filled by grepping the module `.conf` on first open. Operator can rename keys if upstream changes them. |
| Current values | Read-only or editable inputs showing live values from conf files; **Save** writes back via `ServerConfigService` |
| Bootstrap status | Whether server-wide progression has been prepared |
| Actions | Re-scan conf keys, reset key mapping to grep defaults |

API:
- `GET /api/stacks/{id}/migrations/individual-progression/settings`
- `PUT /api/stacks/{id}/migrations/individual-progression/settings`

**Files:** `PatchesTab.tsx` (tab shell), new `ServerProgressionTab.tsx`.

### 4. Apply-patch confirmation (IP stacks)

When applying a patch linked to a `ProgressionState`, extend existing apply confirm dialog:

- Show progression slug + resulting IP config delta (`ProgressionLimit` 3 → 4)
- For `START` (state 0): note *"Does not increment progression counters"*
- Show `Expansion` change if applicable
- Checkbox default on: **Update Individual Progression config** (calls sync service as part of apply)

---

## Patch template seeding

### Replace placeholders

On bootstrap (only when `mod-individual-progression` ∈ `moduleIds`):

1. Delete empty `patch 1`–`patch 4` folders if `AppliedPatchLevel === 0` and folders contain only default descriptions
2. Create **19** patch folders from `IndividualProgressionPatchCatalog` (skip state 11), including `patch 1.0 [START]`
3. Seed each `description.md` and `progression.json` (`incrementsProgression: false` for state 0)

```markdown
# Molten Core

**Progression state:** `PROGRESSION_MOLTEN_CORE` (1)  
**Expansion:** Classic · **Patch index:** 1.1

Unlocks Blackwing Lair progression tier in mod-individual-progression. Applying increments StartingProgression and ProgressionLimit.

## Content
- SQL: import from release archive into `sql/world|auth|characters`
- Client: import MPQ release into `mpq/`
```

`progression.json` for START (`1.0`):

```json
{ "state": 0, "slug": "START", "expansion": "classic", "incrementsProgression": false }
```

All other patches: `"incrementsProgression": true`.

4. Empty `sql/`, `dbc/`, `map/`, `mpq/` subdirs (existing layout)

### API

`POST /api/stacks/{stackId}/migrations/individual-progression/bootstrap`

Response: `{ templatesCreated: 19, configUpdated: true, expansion: 0, keysDiscovered: true }` — no patch applied.

---

## Release import — merge mode

### Problem

GitHub release zips may arrive as **two trees**:

```
sql-release.zip
  sql/world/...
  sql/characters/...

mpq-release.zip
  mpq/patch-a.MPQ
  mpq/patch-b.MPQ
```

Today `ImportPatchCollectionAsync` expects patch-key prefixes (`patch 1.1/sql/...`). Merge mode maps release content **onto an existing patch folder** by category.

### New import mode: `merge`

Extend `ImportPatchCollectionMode`: `append` | `override` | `merge`.

**Request:** `POST .../migrations/import` with:

- `mode=merge`
- `targetPatchKey` (e.g. `patch 1.1 [MOLTEN_CORE]`)
- One or two archives:
  - `sqlArchive` (optional)
  - `clientArchive` (optional)

**Behaviour:**

1. Extract each archive to temp
2. Accept top-level `sql/` and/or `mpq/` (also `dbc/`, `map/` if present)
3. Copy into `migrations/{targetPatchKey}/{category}/...` (merge files; overwrite same relative path)
4. Reject if target patch is already **Applied** (content locked)
5. Return file counts per category

**UI:** On patch detail in `PatchesTab`, add **Import release** button → upload SQL zip + MPQ zip → merge into selected patch.

### Release download (phase 6 — URLs TBD)

Configuration array — **stub structure now; populate once release links are provided**:

```json
"IndividualProgressionReleases": {
  "Patches": [
    { "state": 0, "slug": "START", "sqlUrl": null, "mpqUrl": null },
    { "state": 1, "slug": "MOLTEN_CORE", "sqlUrl": "https://github.com/.../sql.zip", "mpqUrl": "https://github.com/.../mpq.zip" }
  ]
}
```

New endpoint: `POST .../migrations/individual-progression/download-releases`

- For each catalog entry with non-null URLs, download server-side (same HTTPS guard as addon catalog)
- Call merge import into the matching patch folder
- Report per-patch success/failure
- Entries with `null` URLs are skipped until operator adds links to config

---

## Apply pipeline hook

In `MigrationService.Apply.cs`, after `persist-level` (stage 11) and before `restart`:

```csharp
if (stack.ModuleIds.Contains("mod-individual-progression") &&
    TryResolveProgressionState(patch, out var state, out var meta))
{
    await _ipSync.OnPatchAppliedAsync(stackId, patch.Index, state, meta.IncrementsProgression, cancellationToken);
}
```

Patch ↔ state resolution: `progression.json` beside `description.md`:

```json
{ "state": 1, "slug": "MOLTEN_CORE", "expansion": "classic", "incrementsProgression": true }
```

`GetPatchOverview` includes optional `progressionState` for UI badges.

---

## Implementation phases

### Phase 1 — Guidance (low risk)
- Module picker notification (wizard + stack modules tab)
- `ModuleSetupWarnings` link to Patches tab when IP module installed
- Document manual steps until bootstrap exists

### Phase 2 — Progression catalog + config sync
- `IndividualProgressionPatchCatalog` + header parser (local file → GitHub → static fallback)
- `IndividualProgressionSyncService` + `ServerConfigService.SetConfigValueAsync`
- Conf key grep + `IndividualProgressionSettings` persistence
- **Server Progression** tab (key mapping + values)
- Unit tests: enum parse, config line rewrite, expansion transitions, START skip increment

### Phase 3 — Bootstrap + template seeding
- Bootstrap API + Patches CTA (prepare only — no auto-apply)
- Replace placeholders with 19 IP patch templates + `progression.json` (START has `incrementsProgression: false`)
- Bootstrap writes worldserver + IP conf; discovers conf keys

### Phase 4 — Apply hook
- `OnPatchAppliedAsync` after each apply (skip counter increment for START)
- Apply confirmation UI showing config delta
- Increment `StartingProgression` / `ProgressionLimit` for all patches except `1.0`; set `Expansion` on era boundaries

### Phase 5 — Merge import
- `merge` import mode (API + `MigrationService`)
- Patch detail UI: dual zip upload (SQL + MPQ)
- Tests: merge into unapplied patch; reject on applied patch

### Phase 6 — Release automation
- Populate `IndividualProgressionReleases` once operator provides GitHub links
- Download endpoint + merge into patch folders
- Optional: one-click “download releases for next unapplied patch”

---

## Files to touch

| Area | Files |
|---|---|
| Catalog / parse | New `IndividualProgressionPatchCatalog.cs`, optional `IndividualProgressionHeaderParser.cs` |
| Config sync | New `IndividualProgressionSyncService.cs`, extend `ServerConfigService.cs`, `IServerConfigService.cs` |
| Migrations API | `MigrationsController.cs`, `MigrationService.cs`, `MigrationService.Apply.cs`, `MigrationLayout.cs` |
| Patch DTOs | `PatchDtos.cs`, `patch.types.ts` (`progressionState?`, `progressionSlug?`) |
| UI | `PatchesTab.tsx`, `ServerProgressionTab.tsx`, `ModulesStep.tsx`, `StackModulesTab.tsx`, `ModuleSetupWarnings.tsx` |
| Stack config | `StackConfigurationDto` + `individualProgressionSettings` |
| Config | `appsettings.json` → `IndividualProgressionReleases` (phase 6) |
| Tests | `IndividualProgressionPatchCatalogTests.cs`, `IndividualProgressionSyncTests.cs`, import merge tests |

---

## Test plan

| Check | How |
|---|---|
| IP module selected shows Patches notification | Wizard + stack modules tab |
| Bootstrap CTA only when `mod-individual-progression` in moduleIds + level 0 | Patches tab; hidden without module |
| Bootstrap does not auto-apply any patch | No `AppliedPatchLevel` change after bootstrap |
| Bootstrap sets `Expansion=0`, IP keys | Read conf files on disk |
| Server Progression tab shows grepped keys | Open tab → fields populated |
| Edited key names used on next apply | Rename key in UI → apply patch → correct conf line updated |
| 19 template folders created, placeholders removed | List migrations dir |
| Apply `1.0 [START]` does not increment counters | Apply START → values unchanged |
| Apply `1.1` increments progression keys | Apply patch → verify conf |
| Apply `2.0` sets `Expansion=1` | Apply TBC first patch |
| Apply `3.0` sets `Expansion=2` | Apply WotLK first patch |
| TBC race keys stay `8` after many applies | Regression test |
| Merge import adds SQL + MPQ to target patch | API test |
| Merge blocked on applied patch | API returns error |
| Header parser reads built module path | Test with fixture file |

---

## Decisions (confirmed)

| # | Decision |
|---|---|
| 1 | **Bootstrap does not auto-apply.** It imports/prepares config, discovers conf keys, and seeds patch templates. Admin applies patches manually. |
| 2 | **Conf keys from grep + editable UI.** Grepped from module `.conf` into a new **Server Progression** tab on Patches; key names and values editable if upstream changes. |
| 3 | **`1.0 [START]` is a real patch** (state 0). Applying it does **not** increment `StartingProgression` or `ProgressionLimit`. |
| 4 | **Release URLs stubbed** until operator provides GitHub links; config section updated at that time. |
| 5 | **`mod-individual-progression` must be in `moduleIds`.** Server type alone does not enable IP features. |

---

## Out of scope

- Character-level progression UI on armory
- Auto-generating SQL/DBC content (only templates + import/download)
- Auto-applying patches during bootstrap
- Changing Grimfeather module source code
- IP features when module is not installed (even on Individual Progression server type)
