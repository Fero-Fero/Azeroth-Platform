# Clean-slate cleanup changelog

**Status:** Implemented with `plans/15-clean-slate-cleanup.md`  
**Folder:** `plans/`  
**Policy:** Current create/update paths are the only supported shape. Apply EF migration `20260817120000_DropCustomEnvVarsJson` before restarting the API.

This file records what 15 removed, what the product does now, and what to test so current features were not dropped by accident.

---

## What changed

### Stack rows and env vars

| Removed | Current behavior |
|---------|------------------|
| Lazy `ArmoryPort == 0` / `ClientPort == 0` compose fixups | `EnsureRuntimeConfigurationAsync` throws if either port is missing |
| `AllocateArmoryPortAsync` wrapper | Port allocation still uses `AllocateStackPortAsync` on **create** |
| `CustomEnvVarsJson` column + `CustomEnvVars` DTO + fold/mirror | `ServiceEnvVarsJson` / `serviceEnvVars` is the only store |
| Discovery writing flat env | Import writes `{ worldserver: discoveredEnv }` into `ServiceEnvVarsJson` |
| Module config apply writing the flat column | `ApplyModuleConfigAsync` merges into the worldserver bucket |

EF: `DropCustomEnvVarsJson` (includes a Designer so SQLite can rebuild `ManagedStacks`). User applies the migration.

### Manager disk mirrors

| Removed | Current behavior |
|---------|------------------|
| Client/armory files on the manager data tree as a product path | Per-stack Docker named volumes only |
| `POST /api/docker/manager/migrate-client-mirrors` and `cleanup-mirrors` | Deleted |
| Manager Volume Browser migrate/cleanup buttons | Browser is inspect + delete leftover files only |

Leftover `client/` on the manager volume can still be deleted as unused files; it is not a client source.

### Player / launcher paths

| Removed | Current behavior |
|---------|------------------|
| Empty `ClientContentBaseUrl` → manager file endpoints | Empty URL means the client container is not published |
| `GET /api/addons` global APIs | Stack-scoped `GET /api/stacks/{id}/addons` only |
| Parameterless `IClientDistributionService` (global root) | Context-scoped (per-stack) APIs only |
| Stale defaults `/api/launcher/manifest` and `/api/launcher/profiles` | Portal-relative `/manifest`; anonymous regression test hits admin preview assets |
| Timestamp launcher version compare | Semantic `Release.Update.Minor.Patch` only; non-semantic treated as `0.0.0.0` |
| Manifest splitter “older servers” promotion | Split by classified `Group` only |

**Kept:** stack portal `/portal`, `/manifest`, `/files/*`, `/login`. Admin launcher **build** endpoints. `ILauncherArtifactSource` is the admin build + stack `/launcher/*` abstraction.

### On-disk / JSON formats

| Removed | Current behavior |
|---------|------------------|
| Armory layout V1 + `MigrateToV2` | V2 with `pages` only. V1 files fail normalize (armory load falls back to default layout, no widget copy) |
| Wallpaper/video HTML rewrite for old static paths | Current templates inject `azp-wallpaper` only |
| Plaintext secret fallback | Untagged values throw `CryptographicException` |
| Patch `remove.json` / `.remove.json` merge | `mpq.json` `remove` array only (zip import copies that file; UI `SetMpqRemovals` writes it; leftover `remove.json` is skipped) |
| IP `patch 1`–`patch 4` placeholder slugs | Placeholders are `patch 1.0` … `patch 4.0` |

### API / frontend leftovers

| Removed | Current behavior |
|---------|------------------|
| `DELETE …/docker/images/{imageId}` path aliases | Query form `DELETE …/docker/images?imageId=` only |
| `LauncherNewsReadingPreview` | `LauncherNewsArticlePreview` |
| README / DOCKER.md / launcher README player routes and “legacy flat override” | Docs describe stack portal + per-service env buckets |

**Kept:** Docker CLI / BuildKit host fallbacks. `react-grid-layout/legacy` import. WDBX obsolete flags. Armory upstream `playermap.js` naming. Config update `Skip` / `Merge` / `Fresh`. `RuntimeArtifactVersion`. Cloud VPC firewall.

---

## Tests added or rewritten

- `ArmoryLayoutDefaultsTests.Normalize_rejects_v1_root_widgets` — V1 does not migrate widgets
- `ArmoryLayoutThemeTests.BuildCss_generates_per_page_grid_config` — CSS from V2 `pages` only
- `SecretProtectorTests` — round-trip tagged secrets; reject plaintext
- `MpqRemovalImportTests` — import `mpq.json` `remove`; skip leftover `remove.json` in zip
- `IndividualProgressionRecreateTests` — removes current `patch 1.0` placeholders, not `patch 1`
- `SecurityRegressionTests` — anonymous admin launcher preview asset is not 401
- Deleted wallpaper video-strip test and `TryParseMpqRemovalJson` sidecar tests
- EF: `DropCustomEnvVarsJson` includes a Designer so SQLite can rebuild `ManagedStacks` (raw `DropColumn` is not supported)

---

## What to test (manual checklist)

Use a **new** stack created after this change (wizard create or cloud launch). Do not expect pre-existing SQLite rows with `CustomEnvVarsJson` or V1 layouts to keep working.

### Apply migration

- [ ] Apply `20260817120000_DropCustomEnvVarsJson` (user applies migrations)
- [ ] Rebuild/restart the API
- [ ] Manager starts without EF pending-migration errors

### Create → env → build → start

- [ ] Create a local stack through the wizard (defaults: auth 3724, world 8085, armory/client allocated)
- [ ] Overview shows armory and client ports (not 0)
- [ ] **Environment Variables** tab: set a worldserver var (e.g. `AC_MOTD`), save, restart — compose override has it only on worldserver
- [ ] Set an authserver/armory/client var the same way — each stays in its bucket
- [ ] Edit stack config (ports, realm name) with `Skip` / `Merge` / `Fresh` — still works
- [ ] AH Bot setup still writes `AC_AUCTION_HOUSE_BOT_GUIDS` into worldserver env and the overview warning clears
- [ ] Build completes; start brings up db, auth, world, armory, client

### Armory

- [ ] Armory loads with V2 default pages (home, character, connect, …)
- [ ] Layout editor save/reload keeps widgets (no V1 root `widgets` in `armory-layout.json`)
- [ ] Wallpaper is the current `azp-wallpaper` layer (classic/tbc/wotlk/custom), not `video.bg-video` / `img/bg/wallpaper.jpg`
- [ ] Email confirmation save does not require a flat `customEnvVars` field

### Client / launcher / addons

- [ ] Client tab shows a client-server URL (`http(s)://host:clientPort`)
- [ ] Empty URL is treated as “client not published”, not a manager download fallback
- [ ] Stack **Addons** tab lists/uploads/installs (`/api/stacks/{id}/addons` only)
- [ ] `GET /api/addons` is 404
- [ ] Launcher download from the stack portal; Play syncs from `/manifest` and `/files/*`
- [ ] Launcher self-update still works with semantic versions (`0.0.0.x`)
- [ ] Admin **Launcher** page can still compile and download the exe (`/api/launcher-build/*`)

### Docker

- [ ] Delete a stack image via the Docker tab (query `imageId`, not path `/images/{id}`)
- [ ] Global Docker → manager volume browser: inspect files; **no** “Migrate legacy client mirrors” / “Remove legacy stack mirrors”
- [ ] Deleting leftover `client/` on the manager does not empty stack client-base volumes

### Patches / MPQ / IP

- [ ] Import a collection whose `mpq/mpq.json` has `"remove": ["Patch-L.MPQ"]` — apply retires that MPQ
- [ ] A zip that only has `mpq/remove.json` does **not** import removals
- [ ] Patches tab MPQ removal UI still saves (writes `mpq.json` `remove`)
- [ ] New Individual Progression stack gets `patch 1.0` … `patch 4.0` placeholders, not `patch 1`

### Secrets / cloud

- [ ] External stack SSH key still encrypts as `enc:v1:…` and reconnects after restart
- [ ] A row with a raw PEM in the SSH column fails closed (re-enter the key) instead of treating it as plaintext
- [ ] Cloud Connect → launch/pick still applies host bootstrap + provider edge firewall (unchanged product)

### Regression greps (dev)

- [ ] No live `CustomEnvVars` / `customEnvVars` DTO field (module UI tab name `CustomEnvVarsTab` is unrelated)
- [ ] No `migrate-client-mirrors`, `MigrateToV2`, `LegacyDefaultPatches`
- [ ] `react-grid-layout/legacy` import still present (library path)

---

*Clean slate as of 2026-08-17. Breaking change; no dual-write.*
