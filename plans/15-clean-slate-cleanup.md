# Clean-slate cleanup — drop legacy and pre-existing compat

**Status:** Open  
**Folder:** `plans/`  
**Policy:** As of this moment the platform is a **clean slate**. Do not keep, migrate, or document support for older stacks, pre-1.0 manager layouts, dual schemas, or “imported leftover” rows. Current create/update paths are the only supported shape.

If a stack, file, or API payload is not what the code writes **today**, treat it as invalid. Do not add startup jobs whose only job is to upgrade old data “so it still works.” Delete the shim; optionally fail fast with a clear error.

This is not a compatibility release. Breaking changes are expected and preferred over dual-write.

---

## Rules

1. **No dual read/write.** One column, one DTO field, one on-disk format.
2. **No lazy fixups** for missing ports, empty env maps, or version `0` “because the row is old.” Create and update always persist current defaults. Missing required fields → error, not a silent default in a hot path.
3. **No manager-era player paths.** Launchers talk to the stack client-server container only.
4. **No V1 JSON readers** once V2 is the writer. Do not keep PascalCase / camelCase dual parse except where a third-party file format requires it (armory upstream playermap, WDBX).
5. **No “legacy” comments or aliases** left behind after the code path is gone.
6. **Do not** treat stack **update** (`Skip` / `Merge` / `Fresh` config), **runtime artifact template versioning**, or **cloud firewall sync** as legacy — those are current features.

---

## Remove (inventory)

Use this as a delete list. After each item, grep for the symbol/comment and delete tests that only exist to protect the old shape.

### Stack rows & env vars

| Remove | Where |
|--------|--------|
| Lazy `ArmoryPort == 0` / `ClientPort == 0` compose fixups | `StackService.EnsureRuntimeConfigurationAsync` |
| Unused `AllocateArmoryPortAsync` | `StackService` |
| `CustomEnvVarsJson` + flat `CustomEnvVars` DTO + fold/mirror helpers | `ManagedStackEntity`, `AdvancedConfigDto`, `StackService` (`NormalizeServiceEnvForPersist`, `BuildServiceEnvDto`, …), `BuildService`, `EditStackConfigModal`, README “flat override” |
| Import writing flat env | `StackDiscoveryService` — write `ServiceEnvVarsJson` only, current buckets |

**Keep:** `ServiceEnvVarsJson` as the only env store. `RuntimeArtifactVersion` as **current** template drift tracking (bump `CurrentVersion` when templates change; do not special-case `0` as “pre-tracking stack”).

### Manager disk mirrors

| Remove | Where |
|--------|--------|
| Client/armory files on the manager data tree as a supported layout | `StackDockerService` (`IsClientLegacyMirrorPath`, `IsArmoryLegacyDataMirrorPath`, migrate/cleanup endpoints) |
| Mirror migration UI | `ManagerVolumeBrowser.tsx`, `GlobalDockerPage.tsx` migrate/cleanup |

Current model is **per-stack Docker named volumes** only. Do not keep “copy from manager folder” as a product path.

### Player / launcher paths

| Remove | Where |
|--------|--------|
| Empty `ClientContentBaseUrl` → “manager per-stack file endpoints” | `LauncherConfigDto`, `StackLauncherService`, launcher comments |
| Global `{RootPath}` client/addons API if the UI is stack-scoped | `ClientDistributionService` parameterless APIs, `GET /api/addons` |
| Stale defaults `/api/launcher/manifest` and `/api/launcher/profiles` | `LauncherPortalDtos`, `LauncherProfiles.cs`, `SecurityRegressionTests` |
| Timestamp launcher versions (`yyyyMMddHHmmss`) | `SelfUpdateService` — semantic versions only |
| Manifest splitter “older servers” correction | `ManifestSplitter.cs` once classification is the only writer |

**Keep:** Stack portal `/portal`, `/manifest`, `/files/*`, `/login`. Admin launcher **build** endpoints on the manager. Reword `ILauncherArtifactSource` comments (drop “legacy/local preview”).

Require the client container for player download. Empty URL is an error in the UI, not a fallback.

### On-disk / JSON formats

| Remove | Where |
|--------|--------|
| Armory layout V1 (`widgets`, root `templateId`) and `MigrateToV2` | `ArmoryLayoutDefaults`, `armory-layout-pages.ts`, `armory.types.ts`; armory container may keep camelCase/PascalCase **only** if the live site still emits both — otherwise V2 camelCase only |
| Wallpaper/video HTML rewrite for old static paths | `ArmoryImageService` (`StripDeprecatedVideoWallpaperMarkup`, `PatchLegacyWallpaperReferences`, `EnsureLegacyWallpaperImage`) after current templates don’t emit them |
| Plaintext secret fallback (no version marker) | `SecretProtector` — reject untagged values |
| Patch `remove.json` merge | `MigrationService.CollectMpqRemovals` — `mpq.json` `remove` array only |
| Individual Progression legacy folder slugs | `MigrationLayout.LegacyDefaultPatches`, `ProgressionRepoAlignment` legacy names |

Do not ship a one-time “upgrade everyone’s V1 layouts” job for old deployments. New writes are V2. Old files that fail to parse should fail the operation.

### API aliases & frontend leftovers

| Remove | Where |
|--------|--------|
| `DELETE …/docker/images/{imageId}` path aliases | `StacksController.DeleteDockerImageLegacy`, `DockerController.DeleteEngineImageLegacy` — query form only |
| Deprecated `LauncherNewsReadingPreview` | `NewsArticlePreview.tsx` |
| README / DOCKER.md player routes and “legacy flat override” | Docs |

**Keep:** Docker CLI / BuildKit host fallbacks (environment, not stack history). `react-grid-layout/legacy` import (library path). WDBX “obsolete” flags. Armory upstream `playermap.js` naming.

---

## Explicitly current (do not delete as “legacy”)

| Area | Why |
|------|-----|
| Config update mode `Skip` / `Merge` / `Fresh` | Live stack update, not old-stack support |
| Runtime artifact template versioning | Detects **current** compose/env template drift |
| External VPC firewall / security roles | Current cloud login product |
| Stack discovery of a **current** compose checkout | May remain if it writes today’s schema only |

---

## How to execute

1. Grep `legacy`, `older stack`, `backward-compat`, `CustomEnvVars`, `remove.json`, `MigrateToV2`, `LegacyDefaultPatches`, `migrate-client-mirrors`.
2. Delete shims and the tests that only exist to prove the old shape still loads.
3. Add tests that **create** a stack today and assert ports, `serviceEnvVars`, layout V2, encrypted secrets, client-server URL — no fixup branch taken.
4. Fail closed: plaintext secrets, layout V1, missing client portal URL, `ArmoryPort == 0` on a running path.
5. Update README / DOCKER.md in the same change as the code.

No migration sprint, no “keep dual-write until 1.1.” Cut over in place.

---

## File index

| Path | What to strip |
|------|----------------|
| `StackService.cs` | Port lazy fixup, env fold, `AllocateArmoryPortAsync` |
| `ManagedStackEntity.cs` | `CustomEnvVarsJson` |
| `AdvancedConfigDto.cs` / `stack.types.ts` | Flat `customEnvVars` |
| `BuildService.cs` | Env fold |
| `StackDiscoveryService.cs` | Flat env import |
| `StackDockerService.cs` | Manager mirrors |
| `ClientDistributionService.cs` | Global root APIs |
| `StackLauncherService.cs` / `LauncherConfigDto.cs` | Manager download fallback |
| `LauncherPortalDtos.cs` | Stale BaseManifestUrl |
| `ArmoryLayoutDefaults.cs` / `armory-layout-pages.ts` | V1 migrate |
| `ArmoryImageService.cs` | Old wallpaper/video markup |
| `SecretProtector.cs` | Plaintext fallback |
| `MigrationService.cs` / `MigrationLayout.cs` | `remove.json`, IP legacy folder names |
| `StacksController.cs` / `DockerController.cs` | Path-style image DELETE |
| `EditStackConfigModal.tsx` | Env mirror sync |
| `ManagerVolumeBrowser.tsx` | Mirror migrate UI |
| `launcher/.../ManifestSplitter.cs` | Older-server correction |
| `launcher/.../SelfUpdateService.cs` | Timestamp versions |
| `launcher/.../LauncherProfiles.cs` | Stale defaults |

---

*Clean slate as of 2026-08-17. No pre-existing deployment support.*
