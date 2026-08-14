# Legacy & Back-Compat Cleanup Plan (v1.0.0)

This document inventories **legacy, older-stack, and deprecated compatibility** code paths in Azeroth Platform. It is a **plan only** — nothing here should be removed until each item is verified against live usage and covered by tests.

**Context:** Platform is at **v1.0.0**. Pre-1.0 stacks and manager-era workflows are no longer a supported target. New stacks should always get current defaults (ports, env-var shape, per-stack Docker volumes, stack-hosted client-server portal). Existing **1.0.x deployments** must keep working through any cleanup.

**Related docs:** [EXTERNAL-VPC-SETUP.md](./EXTERNAL-VPC-SETUP.md) (current external VPC model).

---

## Methodology

For each item we record:

| Column | Meaning |
|--------|---------|
| **Status** | `ACTIVE` = still required for current features · `TRANSITIONAL` = migration path in progress · `CANDIDATE` = safe to remove after verification · `STALE` = likely dead code |
| **Risk** | `LOW` / `MED` / `HIGH` if removed without migration |
| **Action** | Recommended next step |

**Do not delete** anything marked `ACTIVE` or `TRANSITIONAL` until the migration column is satisfied.

---

## Summary by priority

| Priority | Theme | Item count (approx.) |
|----------|-------|----------------------|
| **P1** | Stack DB fields & runtime lazy-fixups (`ArmoryPort == 0`, env-var dual write) | 6 |
| **P2** | Manager filesystem mirrors (client / armory data on manager volume) | 1 subsystem |
| **P3** | Player path fallbacks (manager-served client vs stack client-server) | 4 |
| **P4** | Data-format migrations (armory layout V1, PascalCase JSON, secrets, MPQ remove.json) | 5 |
| **P5** | API aliases & stale defaults | 4 |
| **P6** | Frontend deprecated exports & docs drift | 3 |

---

## P1 — Stack entity & “older stacks” lazy fixups

These paths exist because stacks created **before a feature landed** may have missing DB values. At 1.0.0, **new stacks always populate these fields at create time**; lazy fixups are only for imported or pre-release DB rows.

### 1.1 Armory / client port allocation on compose regen

| | |
|--|--|
| **Location** | `backend/AzerothPlatform.Infrastructure/Services/StackService.cs` (~2080–2091) |
| **Comment** | `// Older stacks (created before the armory feature)…` / `// Older stacks (created before the client-server feature)…` |
| **Behavior** | If `ArmoryPort == 0` or `ClientPort == 0`, allocates default ports (8100/8101) during `EnsureRuntimeConfigurationAsync`. |
| **Status** | `TRANSITIONAL` — still protects imported stacks; new creates set ports in `CreateStackAsync`. |
| **Risk** | `MED` — removing without a one-time DB migration breaks armory/client compose for rows with `0`. |
| **Action** | Add a **1.0.0 data migration** (SQL or startup job): set `ArmoryPort`/`ClientPort` to defaults where `0` and feature enabled. Then replace lazy fixup with an invariant assertion in dev/test. |

### 1.2 Runtime artifact version drift

| | |
|--|--|
| **Location** | `ManagedStackEntity.RuntimeArtifactVersion`, `RuntimeArtifactTemplate.cs`, `StackService` update status, `StackUpdateCheckerService` |
| **Comment** | Defaults to `0` for stacks created before tracking existed |
| **Behavior** | Flags stacks whose generated `.env` / `docker-compose.override.yml` predate template fixes; prompts re-apply. |
| **Status** | `ACTIVE` — this is **ongoing** versioning, not pre-1.0 legacy. Keep; bump `CurrentVersion` when template output changes. |
| **Risk** | N/A (keep) |
| **Action** | None for removal. Document that v1 baseline is `RuntimeArtifactTemplate.CurrentVersion = 1`. |

### 1.3 Unused `AllocateArmoryPortAsync` wrapper

| | |
|--|--|
| **Location** | `StackService.cs` (~1191) |
| **Behavior** | Thin alias to `AllocateStackPortAsync`; **no callers**. |
| **Status** | `STALE` |
| **Risk** | `LOW` |
| **Action** | Delete wrapper when touching `StackService` next. |

---

## P1 — Dual environment-variable storage (`CustomEnvVars` ↔ `ServiceEnvVars`)

The platform moved from a **flat** worldserver env map to **per-service buckets**. Both columns and DTO fields are still read/written.

### 1.4 Database & entity

| | |
|--|--|
| **Location** | `ManagedStackEntity.CustomEnvVarsJson`, `ManagedStackEntity.ServiceEnvVarsJson` |
| **Docs** | Entity comment: legacy flat dict “superseded by `ServiceEnvVarsJson`” |
| **Status** | `TRANSITIONAL` — dual-write still happens on create/update/import. |
| **Risk** | `HIGH` — wizard, env tab, modules, build, and discovery all touch one or both. |
| **Action** | **Phase A:** stop accepting flat-only API payloads (require `serviceEnvVars`). **Phase B:** one-time migration copy flat → worldserver bucket where missing. **Phase C:** remove `CustomEnvVarsJson` column + DTO field after read paths gone. |

### 1.5 Read/write folding logic

| | |
|--|--|
| **Locations** | `StackService.cs` (`NormalizeServiceEnvForPersist`, `BuildServiceEnvDto`, `ReadServiceEnvForConfig`, `BuildServiceEnvVarsForOverride`), `BuildService.cs` (~696), `AdvancedConfigDto.CustomEnvVars`, `EditStackConfigModal.tsx` (sync comment), `stack.types.ts` / README |
| **Behavior** | If `ServiceEnvVars` lacks worldserver but flat `CustomEnvVars` has entries, fold into worldserver; mirror worldserver back to flat on persist. |
| **Status** | `TRANSITIONAL` |
| **Risk** | `HIGH` |
| **Action** | Same phased plan as 1.4. Keep dual-write until migration completes. |

### 1.6 Stack discovery import

| | |
|--|--|
| **Location** | `StackDiscoveryService` → `DiscoveredEnvVars`; `StackService` import path writes `CustomEnvVarsJson` |
| **Behavior** | Importing filesystem stacks seeds flat env vars. |
| **Status** | `ACTIVE` (import feature) but writes **legacy shape**. |
| **Risk** | `MED` |
| **Action** | On import, write `ServiceEnvVarsJson` directly; optionally drop flat write. |

---

## P2 — Manager volume mirrors (client / armory on manager disk)

Before per-stack **Docker named volumes**, uploads lived under the manager data tree (`client/`, `stacks/*/static/data/`). Migration UI still supports moving data off the manager.

### 2.1 `StackDockerService` legacy mirror detection & cleanup

| | |
|--|--|
| **Location** | `StackDockerService.cs` — `IsClientLegacyMirrorPath`, `IsArmoryLegacyDataMirrorPath`, migrate/cleanup endpoints, volume browser copy |
| **API** | `POST /api/docker/manager/migrate-client-mirrors`, `POST /api/docker/manager/cleanup-mirrors` |
| **UI** | `ManagerVolumeBrowser.tsx`, `GlobalDockerPage.tsx` |
| **Status** | `TRANSITIONAL` — required until all deployments use stack volumes and mirrors are empty. |
| **Risk** | `HIGH` if removed while manager copies are still authoritative. |
| **Action** | Keep through 1.0.x. Add admin health check: “manager mirror present?” on Docker tab. Remove mirror logic only after documented migration + empty mirror tree. |

---

## P3 — Player path: manager vs stack client-server

**Current model (1.0.0):** Launcher talks to each stack’s **client-server container** (`/portal`, `/manifest`, `/files/*`, `/login`). Manager is control-plane only.

### 3.1 Empty `ClientContentBaseUrl` fallback

| | |
|--|--|
| **Location** | `LauncherConfigDto.ClientContentBaseUrl`, `StackLauncherService.BuildClientContentBaseUrl`, launcher `LauncherConfig.cs` comment |
| **Behavior** | When client container disabled or no port, URL is blank; DTO comment says launcher falls back to “manager's per-stack file endpoints (legacy)”. |
| **Status** | `TRANSITIONAL` — **manager no longer exposes public manifest/files routes** (`StackLauncherController` comment confirms removal). Fallback may be **broken or dead**. |
| **Risk** | `MED` — stacks with `ClientEnabled = false` may not download. |
| **Action** | Audit launcher behavior when URL is empty. Either (a) require client container for download at 1.0.0, or (b) reintroduce explicit error UX. Remove misleading “legacy endpoints” comment if dead. |

### 3.2 Global client distribution (`ClientDistributionOptions` root)

| | |
|--|--|
| **Location** | `ClientDistributionService` “Global (backward-compatible) API”, `AddonsController` (`/api/addons`), `AddonService.RescanAsync(null)` |
| **Behavior** | Parameterless `GetManifestAsync` / `RescanAsync` use global `{RootPath}` instead of per-stack `{RootPath}/stacks/{id}/game`. |
| **Status** | `TRANSITIONAL` — global addons path still active; player download uses stack container. |
| **Risk** | `MED` — global addons UI may still depend on this. |
| **Action** | Confirm frontend only uses `/stacks/{id}/addons` in stack details. If global `/api/addons` unused in UI, deprecate controller + global rescan. |

### 3.3 `LauncherProfilesDto` / launcher model stale defaults

| | |
|--|--|
| **Location** | `LauncherPortalDtos.BaseManifestUrl` default `/api/launcher/manifest`; `launcher/Models/LauncherProfiles.cs` same default; `SecurityRegressionTests` GET `/api/launcher/profiles` |
| **Behavior** | **No HTTP route** serves these paths anymore. `GetProfilesAsync` is internal (feeds `StackRegistryService` → portal push). Launcher uses `/portal` from stack container. |
| **Status** | `STALE` defaults + **stale test** |
| **Risk** | `LOW` for defaults; test may be wrong or testing removed route |
| **Action** | Remove or update defaults; fix `SecurityRegressionTests` to probe `/portal` on client-server or a live admin route. |

### 3.4 `ILauncherArtifactSource` “legacy/local preview” wording

| | |
|--|--|
| **Location** | `ILauncherArtifactSource.cs`, `PortalClient.cs` |
| **Behavior** | Abstraction supports stack portal self-update (`/launcher/latest`). Manager build endpoints still used for **admin** launcher builds. |
| **Status** | `ACTIVE` (wording only) |
| **Action** | Reword comments; no removal. |

---

## P4 — Data format & content migrations

These normalize **on-disk or API JSON** from earlier schema versions. They run on read/save and do not require “older stacks” in DB.

### 4.1 Armory layout V1 → V2

| | |
|--|--|
| **Backend** | `ArmoryLayoutDefaults.MigrateToV2`, `MaybeRefreshLegacyCharacterTemplate`, `MaybeRefreshLegacyCharacterSubpage` |
| **Frontend** | `armory-layout-pages.ts` `migrateLayoutToV2`, `armory.types.ts` deprecated V1 fields (`widgets`, `templateId` on root) |
| **Armory container** | `frontend-armory/src/armory/ArmoryLayout.ts` — PascalCase + camelCase normalization |
| **Status** | `TRANSITIONAL` — any stack with saved V1 layout JSON still loads. |
| **Risk** | `MED` — admin layout editor + runtime armory |
| **Action** | One-time migration job: load each stack layout, persist V2, strip V1 keys. Then simplify readers to V2-only. |

### 4.2 Armory wallpaper / video markup

| | |
|--|--|
| **Location** | `ArmoryImageService.StripDeprecatedVideoWallpaperMarkup`, `PatchLegacyWallpaperReferences`, `EnsureLegacyWallpaperImage` |
| **Status** | `TRANSITIONAL` — fixes old static HTML paths (`img/bg/wallpaper.jpg`, `<video class="bg-video">`) |
| **Risk** | `LOW` if custom styling already migrated |
| **Action** | Run strip on all stacks during armory asset sync; then narrow to upload-time only. |

### 4.3 Encrypted secrets plaintext fallback

| | |
|--|--|
| **Location** | `SecretProtector.cs` — values without version marker returned as plaintext |
| **Status** | `TRANSITIONAL` |
| **Risk** | `MED` — old DB rows may still be plaintext |
| **Action** | Startup migration: re-`Protect()` all stored secrets; then fail on untagged values in non-dev. |

### 4.4 MPQ `remove.json` vs `mpq.json` `remove` array

| | |
|--|--|
| **Location** | `MigrationService.CollectMpqRemovals`, tests in `MpqRemovalImportTests` |
| **Status** | `TRANSITIONAL` — merges legacy per-patch `remove.json` with manifest |
| **Risk** | `LOW` if all patches use `mpq.json` only |
| **Action** | Inventory patch folders; convert remaining `remove.json`; then drop merge. |

### 4.5 Individual Progression legacy patch folder names

| | |
|--|--|
| **Location** | `MigrationLayout.LegacyDefaultPatches`, `ProgressionRepoAlignment` legacy slug folders, tests |
| **Status** | `TRANSITIONAL` |
| **Risk** | `MED` for IP-enabled stacks with old folder names |
| **Action** | Document canonical folder names; run `RemoveOrphanedManagedPatches` / alignment on upgrade. |

---

## P5 — API route aliases & tooling compat

### 5.1 Docker image delete legacy routes

| | |
|--|--|
| **Location** | `StacksController.DeleteDockerImageLegacy` — `DELETE …/docker/images/{imageId}`; `DockerController.DeleteEngineImageLegacy` — `DELETE …/images/{imageId}` |
| **Current UI** | Uses query form: `DELETE …/docker/images?imageId=` (`frontend/services/api.ts`) |
| **Status** | `CANDIDATE` — alias for URL-encoded image IDs in path |
| **Risk** | `LOW` if no external clients use path form |
| **Action** | Grep access logs / document breaking change; remove alias in 1.1.0 if unused. |

### 5.2 Docker CLI / BuildKit comments

| | |
|--|--|
| **Location** | `MigrationImageService` “legacy builder”; `StackDockerService` older docker `--format`; `MigrationService.Apply` `docker-compose` vs `docker compose` |
| **Status** | `ACTIVE` — host environment compat, not stack legacy |
| **Action** | Keep. |

### 5.3 Launcher self-update timestamp versions

| | |
|--|--|
| **Location** | `SelfUpdateService.cs` — semantic vs `yyyyMMddHHmmss` timestamp builds |
| **Status** | `TRANSITIONAL` for old launcher builds in the field |
| **Risk** | `LOW` |
| **Action** | Keep until all distributed launchers are semantic-versioned. |

### 5.4 Manifest splitter “older servers”

| | |
|--|--|
| **Location** | `launcher/.../ManifestSplitter.cs` |
| **Behavior** | Corrects misclassified managed files from older manifest servers |
| **Status** | `TRANSITIONAL` |
| **Action** | Keep until manifest signing + classification proven stable. |

---

## P6 — Frontend deprecated & docs

### 6.1 Deprecated React export

| | |
|--|--|
| **Location** | `NewsArticlePreview.tsx` — `LauncherNewsReadingPreview` (`@deprecated`) |
| **Usage** | **No imports** outside defining file |
| **Status** | `CANDIDATE` |
| **Risk** | `LOW` |
| **Action** | Remove export. |

### 6.2 `react-grid-layout/legacy` import

| | |
|--|--|
| **Location** | `ArmoryLayoutCanvas.tsx` |
| **Status** | `ACTIVE` — library API path, not platform legacy |
| **Action** | None (upgrade library separately). |

### 6.3 README / DOCKER.md drift

| | |
|--|--|
| **Examples** | README “legacy flat override”; DOCKER.md `/api/launcher/assets/*` player routes |
| **Status** | `STALE` docs |
| **Action** | Update docs when player path cleanup (P3) is done. |

---

## Explicitly NOT legacy (do not remove)

| Area | Why it stays |
|------|----------------|
| **Config migration mode** (`Skip` / `Merge` / `Fresh`) | Active stack **update** workflow, not pre-1.0 stack support |
| **Stack discovery / import** | Active feature for adopting existing compose checkouts |
| **External VPC security roles & firewall sync** | Current 1.0.0 deployment model |
| **Runtime artifact template versioning** | Ongoing deploy drift detection |
| **Armory “legacy playermap” port** (`playermap.js`) | Upstream PHP armory lineage, unrelated to platform stack versions |
| **WDBXEditor “obsolete” flags** | Third-party tool vendored under `wdbx/` |
| **npm “deprecated” packages** | Transitive lockfile metadata only |

---

## Recommended phased rollout

### Phase 0 — Inventory & safety (no behavior change)

1. Add integration test: create stack at 1.0.0 asserts `ArmoryPort`, `ClientPort`, `ServiceEnvVarsJson`, `RuntimeArtifactVersion` set without lazy fixup path.
2. Fix stale tests (`SecurityRegressionTests` `/api/launcher/profiles`).
3. Admin report: stacks with `ArmoryPort = 0`, `ClientPort = 0`, `RuntimeArtifactVersion = 0`, non-empty manager client mirror.

### Phase 1 — Data migrations (DB + on-disk)

1. SQL migration: default ports where zero.
2. SQL/script: fold `CustomEnvVarsJson` → `ServiceEnvVarsJson` worldserver bucket.
3. Job: armory layout V1 → V2 persist.
4. Job: re-encrypt plaintext secrets.
5. Optional: migrate manager client/armory mirrors to volumes (existing UI).

### Phase 2 — Code simplification

1. Remove lazy port fixup (after Phase 1).
2. Single-write `ServiceEnvVarsJson` only; remove flat mirror paths.
3. Drop global `/api/addons` if unused.
4. Remove Docker image DELETE path aliases if unreferenced.
5. Remove dead code (`AllocateArmoryPortAsync`, `LauncherNewsReadingPreview`, unused `MigrationService._clientDistribution` injection).

### Phase 3 — Contract tightening (1.1+)

1. API rejects `advanced.customEnvVars` without `serviceEnvVars`.
2. Require client container for player download (or hard error).
3. Remove manager mirror detection once mirrors unsupported.
4. Armory layout API accepts V2 only.

---

## File index (quick reference)

| Path | Category |
|------|----------|
| `backend/.../StackService.cs` | P1 ports, P1 env dual-write, runtime artifact |
| `backend/.../ManagedStackEntity.cs` | P1 env columns, runtime artifact |
| `backend/.../AdvancedConfigDto.cs` | P1 flat env DTO |
| `backend/.../BuildService.cs` | P1 env fold on build |
| `backend/.../StackDiscoveryService.cs` | P1 import env shape |
| `backend/.../StackDockerService.cs` | P2 manager mirrors |
| `backend/.../ClientDistributionService.cs` | P3 global client API |
| `backend/.../StackLauncherService.cs` | P3 client URL fallback |
| `backend/.../LauncherConfigDto.cs` | P3 legacy endpoint comment |
| `backend/.../LauncherPortalDtos.cs` | P3 stale BaseManifestUrl |
| `backend/.../ArmoryLayoutDefaults.cs` | P4 layout V1/V2 |
| `backend/.../ArmoryImageService.cs` | P4 wallpaper legacy paths |
| `backend/.../SecretProtector.cs` | P4 plaintext secrets |
| `backend/.../MigrationService.cs` | P4 remove.json merge |
| `backend/.../MigrationLayout.cs` | P4 IP legacy patch names |
| `backend/.../StacksController.cs` | P5 DeleteDockerImageLegacy |
| `backend/.../DockerController.cs` | P5 DeleteEngineImageLegacy |
| `frontend/src/lib/armory-layout-pages.ts` | P4 layout migration |
| `frontend/src/types/armory.types.ts` | P4 deprecated V1 fields |
| `frontend/src/components/EditStackConfigModal.tsx` | P1 env mirror sync |
| `frontend/src/components/docker/ManagerVolumeBrowser.tsx` | P2 mirror migration UI |
| `launcher/.../ManifestSplitter.cs` | P5 older manifest servers |
| `launcher/.../SelfUpdateService.cs` | P5 timestamp versions |
| `launcher/.../LauncherProfiles.cs` | P3 stale defaults |
| `frontend-armory/src/armory/ArmoryLayout.ts` | P4 PascalCase JSON |

---

## Verification checklist (before each removal)

- [ ] Grep repo + production logs for symbol/route/string
- [ ] Backend + frontend build clean
- [ ] Stack create → build → start → armory → launcher download path
- [ ] External VPC firewall sync still lists correct ports
- [ ] Import stack flow (if still supported at removal time)
- [ ] No manager mirror files remain (for P2 removal)

---

*Last updated: 2026-08-14 — platform version 1.0.0*
