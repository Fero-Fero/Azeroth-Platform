# Armory Styling — How It Works & Simplification Options

**Status:** Architecture reference (Aug 2026)  
**Implementation order:** **#12** — **parallel OK** (no cloud dependency). See [00-implementation-order.md](./00-implementation-order.md).

---

## Executive summary

**Verdict: conceptually simple, operationally medium complexity.**

The **core idea is elegant**: armory pages use CSS custom properties (`--armory-*`). Operators pick Classic / TBC / WotLK / Custom in the manager UI. The backend writes a small generated file (`azp-theme.css`) that redefines `:root` variables. Every component in `theme.css` picks up the new colors automatically — no per-selector CSS wars.

What adds complexity is **everything around delivery**:

- Config and generated CSS live on the **manager data volume**, then get **baked into a per-stack Docker image**
- A **second path** (live container file copy) can apply changes without rebuild — but rebuild is still the durable path
- **`layout.hbs` is mutated at build time** (stylesheet links, wallpaper `<div>`, favicon)
- **Three places** define Classic palette defaults (`theme.css`, `ArmoryStylingTheme.cs`, frontend fallback)
- **Launcher / patch themes are a separate system** with similar names but no shared config

**Good news:** You do not need to rethink the CSS-variable approach to simplify. Most wins come from reducing dual apply paths, consolidating palette sources, and toning down build-time string surgery.

---

## What armory styling is (and is not)

| System | Config | Applied to | UI location |
|--------|--------|------------|-------------|
| **Armory styling** | `armory-styling.json` + generated CSS | Per-stack **armory website** (Docker image) | Stack → Armory → **Styling** |
| **Armory layout** | `armory-layout.json` + `azp-layout.css` | Widget grid/chrome on armory pages | Stack → Armory → **Layout** |
| **Launcher template** | Global launcher config + `stack.LauncherTemplate` | **Desktop launcher** download portal | Launcher admin / stack launcher tab |
| **Patch launcher theme** | Patch `config/launcher.json` → `{ "theme": "classic" \| "tbc" \| "wotlk" }` | Launcher only (on patch apply) | Patches tab → launcher theme panel |

Armory styling does **not** flow through patches. Patch `launcher.json` theme and armory `armory-styling.json` are independent.

The **News editor** is the only UI that previews both side-by-side (launcher CSS vs armory `--armory-*` vars).

---

## Architecture at a glance

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Manager UI — ArmoryStylingTab                                           │
│  Template: Classic | TBC | WotLK | Custom (+ 14 colors, wallpaper)      │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ REST: PUT /armory-assets/styling
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  ArmoryAssetsService                                                     │
│  • Normalize colors (ArmoryStylingTheme.Normalize)                       │
│  • Persist armory-styling.json                                           │
│  • Write static/css/azp-theme.css                                        │
│  • TrySyncLiveArmoryShellAsync → copy files into running container       │
│  • Set .static-rebuild-pending marker                                    │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ Operator clicks Rebuild (or job triggers)
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│  ArmoryImageService.BuildImageAsync                                      │
│  1. Copy frontend-armory/ → per-stack work dir                           │
│  2. Overlay armory-static Docker volume (uploaded armory.static.zip)     │
│  3. Overlay manager static/ (generated CSS, wallpaper, favicon)          │
│  4. EnsureLayoutShell: patch layout.hbs, inject wallpaper/favicon       │
│  5. docker build → azeroth-platform-armory-{stackId}                     │
│  6. Clear .static-rebuild-pending                                        │
└───────────────────────────────┬──────────────────────────────────────────┘
                                ▼
                    Armory container serves themed HTML + static files
```

---

## The CSS injection model (the simple part)

### Layer 1 — Base theme (shipped in repo)

**File:** `frontend-armory/static/css/theme.css`

Defines default Classic WoW brown/gold tokens on `:root`:

```css
:root {
  --armory-primary: #8a5a24;
  --armory-accent: #d8a84f;
  /* … 14 palette tokens + derived mix tokens … */
}
```

All armory component rules consume `var(--armory-*)`. Bulma structural CSS lives in `armory.css`; page CSS (character, guild, etc.) also references these vars.

### Layer 2 — Generated override (per stack)

**File:** `static/css/azp-theme.css` (generated, not hand-edited)

**Generator:** `ArmoryStylingTheme.BuildCss()` in backend

Output is only a `:root { … }` block redefining the 14 colors plus derived tokens (`--armory-panel-highlight`, `--armory-border-bright`, wallpaper overlay gradient).

**Classic special case:** If template is Classic, `advancedEnabled` is false, and there is no custom wallpaper, `BuildCss()` returns **empty string** and the file is **deleted** — the bundled `theme.css` defaults apply unchanged.

### Layer 3 — Layout & responsive (related, not “styling” tab)

| File | Generator | Purpose |
|------|-----------|---------|
| `azp-layout.css` | `ArmoryLayoutTheme.BuildCss()` | Per-page grid + widget border/background overrides |
| `azp-responsive.css` | `ArmoryResponsiveTheme.BuildCss()` | Mobile shell rules (full-width below 1200px) |

These share the same `--armory-*` vars but are configured from the **Layout** tab, not Styling.

### Stylesheet load order in armory

```
bulma.min.css → armory.css → theme.css → azp-theme.css → azp-layout.css → tooltip.css → azp-responsive.css
               (structure)   (defaults)  (generated)      (layout chrome)              (responsive)
```

**Why this works well:** One `:root` override re-themes the entire site. No `!important` selector battles.

### Wallpaper (not in CSS for presets)

Wallpaper is injected as HTML in `layout.hbs`:

- **Templates:** `img/bg/wallpaper_{classic|tbc|wotlk}.jpg` from the armory static bundle (zip)
- **Custom:** uploaded `img/azp-wallpaper.*` on manager volume

Fixed-position `.azp-wallpaper` div with inline `background-image` — patched in by `ArmoryImageService.InjectWallpaper()`.

---

## Data storage

### Manager data volume

```
/app/data/armory-assets/stacks/{stackId}/
├── armory-styling.json       # operator config (not served to browser)
├── armory-layout.json        # layout config
├── .static-rebuild-pending   # marker: styling/layout changed, rebuild advised
└── static/
    ├── css/azp-theme.css     # generated
    ├── css/azp-layout.css    # generated
    ├── css/azp-responsive.css
    ├── img/azp-wallpaper.*   # custom upload
    ├── img/azp-favicon.*     # favicon upload (Assets tab)
    └── data/                 # model-viewer dataset (separate)
```

### Docker volumes (per stack)

| Volume | Role for styling |
|--------|------------------|
| `acore-{stackId}-armory-static` | Uploaded `armory.static.zip` — base web assets including preset wallpapers |
| Manager `/app/data/armory-assets` | Generated CSS + custom wallpaper/favicon — **overlays static at image build** |

Armory styling is **not** driven by container environment variables.

### Image build overlay order

1. Copy `frontend-armory/` source into work dir  
2. Fetch **armory-static** volume contents  
3. **Overlay manager `static/` last** — generated files and custom uploads win  

Generated styling assets are **protected** when replacing static uploads (`IsGeneratedStylingAsset()`).

---

## User-facing flow

### Stack → Armory → Styling (`ArmoryStylingTab.tsx`)

1. Pick template card (Classic / TBC / WotLK / Custom)
2. Custom: 14 color pickers + optional wallpaper upload
3. Live preview panel applies `--armory-*` inline (mirrors runtime)
4. **Save styling** → backend persists + live sync attempt + rebuild pending
5. **ArmoryRebuildBanner** prompts rebuild when `staticRebuildPending`

### Adjacent UI

| Tab | Styling relationship |
|-----|---------------------|
| **Assets** | Favicon upload (injected into `layout.hbs`) |
| **Layout** | Widget chrome uses `--armory-*`; shares preview cache with Styling |
| **Launcher → News** | Preview news in armory theme vs launcher theme |

### API

```
GET  /api/stacks/{id}/armory-assets/styling/defaults
GET  /api/stacks/{id}/armory-assets/styling
PUT  /api/stacks/{id}/armory-assets/styling
POST /api/stacks/{id}/armory-assets/styling/wallpaper
POST /api/stacks/{id}/armory-assets/rebuild-image
```

---

## Template palettes

Defined in **`ArmoryStylingTheme.cs`** (backend source of truth for TBC/WotLK/Custom defaults):

| Template | Colors | Wallpaper |
|----------|--------|-----------|
| **Classic** | Bundled `theme.css` (no generated override unless wallpaper/advanced) | `/img/bg/wallpaper_classic.jpg` |
| **TBC** | Fel green / dark grey palette | `/img/bg/wallpaper_tbc.jpg` |
| **WotLK** | Icy blue palette | `/img/bg/wallpaper_wotlk.jpg` |
| **Custom** | Operator-defined 14 hex colors | Uploaded `azp-wallpaper.*` |

Frontend preview uses `GET …/styling/defaults` when available; falls back to `CLASSIC_STYLING_FALLBACK` in `armory-styling.ts` (must stay in sync manually).

---

## Dual apply path (main complexity source)

### Path A — Live sync (immediate, partial)

On save styling/layout, `ArmoryAssetsService.TrySyncLiveArmoryShellAsync()` → `ArmoryImageService.SyncLiveLayoutAsync()` copies a fixed file list into the **running** `frontend-armory` container:

- `css/azp-theme.css`, `css/azp-layout.css`, `layout.hbs`, wallpaper/favicon images, etc.

**Pros:** Operator sees changes quickly without waiting for Docker build.  
**Cons:** Ephemeral — container recreate or image rebuild without overlay loses unsynced state; operators must still understand rebuild.

### Path B — Image rebuild (durable)

`ArmoryImageService.BuildImageAsync()` stages source + volumes + `EnsureLayoutShell()`, then `docker build -t azeroth-platform-armory-{stackId}`.

**Pros:** Canonical themed image; survives restarts.  
**Cons:** Slow; requires explicit rebuild action or job.

**Operator mental model today:** Save → maybe live preview → **Rebuild when prompted**.

---

## Build-time `layout.hbs` mutation

`EnsureLayoutShell()` runs during image build (and live sync staging):

| Step | What it does |
|------|--------------|
| `EnsureThemeStylesheet` | Writes/regenerates `azp-theme.css` from JSON |
| `InjectThemeStylesheet` | Ensures `<link href="…/azp-theme.css">` after `theme.css` |
| `EnsureLayoutStylesheet` + link inject | `azp-layout.css` |
| `EnsureResponsiveStylesheet` + link inject | `azp-responsive.css` |
| `InjectWallpaper` | Inserts `.azp-wallpaper` div after `<body>` |
| `InjectFavicon` | Inserts `<link rel="icon">` |
| `RemoveDeprecatedVideoWallpaper` | Strips legacy `<video class="bg-video">` |
| `PatchLegacyWallpaperReferences` | Rewrites old `wallpaper.jpg` refs |

Bundled `layout.hbs` already contains placeholder `<link>` tags for generated CSS; build step ensures they exist and are ordered correctly.

**Fragility:** String/regex surgery on Handlebars template vs a dedicated partial or build-time template variable.

---

## Key files reference

| Layer | File | Role |
|-------|------|------|
| Runtime CSS | `frontend-armory/static/css/theme.css` | Default `:root` tokens + all component styles |
| Runtime shell | `frontend-armory/static/layout.hbs` | HTML shell, CSS link order |
| Backend generator | `ArmoryStylingTheme.cs` | Palettes + `BuildCss()` |
| Backend layout | `ArmoryLayoutTheme.cs`, `ArmoryResponsiveTheme.cs` | Layout/responsive CSS |
| Backend orchestration | `ArmoryImageService.cs` | Image build, shell injection, live sync |
| Backend persistence | `ArmoryAssetsService.cs` | Save styling, wallpaper, rebuild marker |
| Frontend UI | `ArmoryStylingTab.tsx` | Template picker, colors, preview |
| Frontend preview | `frontend/src/lib/armory-styling.ts` | CSS var preview helpers |
| Docker wiring | `DockerComposeOverrideGenerator.cs` | Armory service + volume mounts |

---

## Complexity scorecard

| Area | Rating | Notes |
|------|--------|-------|
| **CSS variable theming** | ✅ Simple | Solid pattern; keep as-is |
| **Operator UI** | ✅ Simple | Clear template cards + preview |
| **Palette definitions** | ⚠️ Medium | Triplicated Classic defaults |
| **Delivery (build + live sync)** | ⚠️ Medium–High | Two paths, rebuild marker |
| **`layout.hbs` injection** | ⚠️ Medium | Build-time string patching |
| **Storage layout** | ⚠️ Medium | JSON at stack root + `static/` + Docker volume |
| **Launcher/patch overlap** | ✅ Simple | Correctly separated; naming only confuses |
| **Test coverage** | ⚠️ Gap | Layout/responsive tested; `BuildCss()` not |

**Overall: medium complexity** — the hard parts are operational (Docker rebuild cycle), not the styling mechanism itself.

---

## Simplification options (ranked)

### Tier 1 — Low risk, high clarity

| Change | Effort | Benefit |
|--------|--------|---------|
| **Single palette source** | Small | Remove `CLASSIC_STYLING_FALLBACK` duplication; frontend always waits on `/styling/defaults` API |
| **Always emit `azp-theme.css`** | Small | Drop Classic “empty file = delete” special case; always write explicit `:root` block |
| **Add `ArmoryStylingThemeTests`** | Small | Lock palette/CSS output; prevent drift |
| **Clearer save UX copy** | Tiny | One sentence: “Saved to manager; rebuild armory image to make permanent” |

### Tier 2 — Moderate refactors

| Change | Effort | Benefit | Trade-off |
|--------|--------|---------|-----------|
| **Pick one apply path** | Medium | Eliminate live sync *or* auto-enqueue rebuild on save | Live sync = fast but confusing; auto-rebuild = slow but simple |
| **Consolidate wallpaper resolution** | Medium | Single helper used by backend + frontend preview | Touch several call sites |
| **`layout.hbs` partial** | Medium | Replace regex inject with `{{> azp-shell}}` partial committed to repo | Requires armory template change + migration |

### Tier 3 — Larger changes (only if redesigning)

| Change | Effort | Benefit | Trade-off |
|--------|--------|---------|-----------|
| **Serve generated CSS from manager API** (volume mount or proxy) | Large | No rebuild for color changes | Runtime dependency on manager; caching/CDN complexity |
| **Shared expansion theme manifest** (armory + launcher palettes in one JSON) | Large | One Classic/TBC/WotLK definition | Different application layers still diverge |
| **Move all styling into armory-static volume** | Large | Single storage location | Upload UX changes; manager loses overlay model |

### Recommended direction (if simplifying)

1. Keep **CSS custom properties** — do not change the injection mechanism.
2. **Tier 1** items in next touch of armory styling (quick wins).
3. Choose **either** live sync **or** auto-rebuild — not both as equal citizens. Most support-friendly: **auto-enqueue rebuild on save** with progress UI; drop live sync unless rebuild times are unacceptable.
4. Defer shared launcher/armory palette JSON unless you actively want “one theme picks both.”

---

## Mental model for operators

```
Configure  →  Save (manager stores JSON + generates CSS)
           →  Rebuild armory image (bakes theme into container)
           →  Restart / deploy uses new image
```

Live sync is an optimization; rebuild is the source of truth.

---

## Related docs

- README armory / static upload sections
- Patch launcher theme: `PatchLauncherThemePanel.tsx` (launcher only)
- News dual preview: `NewsEditor.tsx`, `NewsArticlePreview.tsx`
