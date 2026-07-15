# Armory account hub + guild bank

Plan for fixing the `/account` page, adding tabbed account management, player display-name privacy, and a classic WoW–style guild view with bank support.

---

## Goals

| Requirement | Design choice |
|---|---|
| Characters visible on `/account` | Fix template/controller mismatch (immediate bug) |
| Tabbed account page | **Account details** · **Characters** · **Guild** |
| Public identity | Player-chosen **display name** replaces username in all public/nav contexts |
| Hide sensitive fields | **Never** show account username or email on public pages, forums (future), or other players’ views |
| Guild on account page | Show the logged-in player’s guild (or guild picker if multiple) |
| Guild members | Reuse/extend existing roster DataTables endpoint |
| Guild bank | New bank tab: tabs, slots, gold, item icons; only when bank is unlocked on the guild |
| Item presentation | DBC-driven icons + tooltips; reuse armory item/DBC pipeline (same prerequisite as character pages) |
| Visual style | Classic WotLK guild window aesthetic (tab strip, parchment panels, emblem header) |

---

## Current state (codebase)

### `/account` — why characters don’t show today

**Root cause:** `AccountController.account()` passes a **multi-realm** model; `account.hbs` expects a **flat single-realm** model.

| Controller passes | Template expects |
|---|---|
| `realms: [{ realm, characters[] }, …]` | `realm`, `characters[]` |
| `hasCharacters` (unused) | `characters.length` |

Result: the character list branch never runs even when the SQL query returns rows.

**Files:**
- `frontend-armory/src/armory/controllers/AccountController.ts` — `account()`
- `frontend-armory/static/account.hbs` — stale template

**Secondary issues:**
- `account.hbs` is under `/static/*` in `.gitignore` and is **not** whitelisted (unlike `login.hbs`, `register.hbs`). It may be missing from built/synced armory images.
- Navbar shows `currentUser.username` (game account name) for logged-in users.
- `layout.hbs` embeds all Handlebars locals in `handlebarsData` JSON — includes `currentUser` on every page (privacy leak for username/email in page source).

### Guild — what exists today

| Piece | Status |
|---|---|
| `GET /guild/:realm/:name` | Works — layout-driven page with emblem header + roster widget |
| `GET /guild/:realm/:id/members` | Works — DataTables SSP roster JSON |
| `guild_bank_*` tables | Exist in AzerothCore `characters` DB — **not queried anywhere** |
| `guildExists()` | **Bug** — SQL is `WHERE guildid` with no `= ?`; ignores guild id param |
| `guild.css` | Referenced by `guild.hbs` but not whitelisted in `.gitignore` (may be absent from repo/image) |

### Privacy exposure today

| Surface | Exposed |
|---|---|
| Navbar (logged in) | Game **username** |
| Pending registration | **Email** in navbar |
| Character pages (public) | Character name, guild name — **no** account username/email |
| Account page | Username in panel title |
| Page source (`layout.hbs`) | `currentUser` object in JSON |

### Forums

No forum module exists in the repo yet. This plan defines the **privacy contract** future forum features must follow: public attribution uses **display name only**.

---

## Recommended data model

### New table: `armory_account_profile` (auth DB)

Created on armory startup (same pattern as `armory_pending_registration`).

```sql
armory_account_profile (
  account_id          INT UNSIGNED PRIMARY KEY,  -- acore_auth.account.id
  display_name        VARCHAR(32) NOT NULL,       -- public armory identity
  hide_username       TINYINT(1) NOT NULL DEFAULT 1,
  updated_at          DATETIME NOT NULL
)
```

**Rules:**
- `display_name`: 3–32 chars, letters/numbers/spaces/dashes; trimmed; uniqueness optional in v1 (collision → suffix suggestion).
- Default on first login: **display name = first character name** if any, else `Player{id}` — never default to username.
- `hide_username`: when true (default), navbar, account UI, and any future forum/comment UI use `display_name` only.
- Email and game username remain in `account` for auth/game; never rendered on public routes.

**Session / navbar:**
- Extend JWT session or resolve profile on each request.
- `currentUser.displayName` for UI; keep `username` server-side only for auth checks.

**Page source leak:**
- Stop serializing full `locals` in `layout.hbs`, or strip `currentUser.username` / email from client-visible JSON.

---

## `/account` page redesign

### Route structure

Keep single route `GET /account` with in-page tabs (no separate URLs required in v1).

```
/account
├── [Account details]   default tab
├── [Characters]
└── [Guild]             only if account has ≥1 character in a guild
```

Optional v2: `GET /account/:tab` for deep links (`/account/characters`, `/account/guild`).

### Tab 1 — Account details

**Shows (owner only):**
- Display name (editable, save via POST)
- Email — only when email-confirmation mode is on; masked (`r***@example.com`) with “used for login only” note
- Game account name — labeled “In-game login name”, with note that it is **not shown publicly** when hide is on
- Sign out

**Does not show:**
- Raw password, SRP fields, session secret

**Actions:**
- `POST /account/profile` — update display name (CSRF + rate limit)
- Validation: length, charset, profanity filter (optional later)

### Tab 2 — Characters

**Per realm** (accordion or realm sections):

| Column | Source |
|---|---|
| Name | `characters.name` → link to `/character/:realm/:name` |
| Level / class / race | existing query fields + icon assets |
| Guild | join `guild_member` + `guild` |
| Online | `characters.online` |
| Optional | Small portrait or class icon |

**Empty state:** “No characters yet — log in to the game to create one.”

**Fix:** Align template with controller `realms[]` model (or flatten in controller — prefer template iteration for multi-realm).

### Tab 3 — Guild

**Visibility:** Render tab only if any character on the account belongs to a guild.

**Multiple guilds:** If characters are in different guilds, show a **guild picker** (dropdown) at top of tab; default to highest-level character’s guild.

**Sub-tabs (classic WoW tab strip):**

| Sub-tab | Content |
|---|---|
| **Members** | Existing roster table (DataTables) — rank, level, class, race, online |
| **Bank** | See [Guild bank](#guild-bank) below |

**Header (all sub-tabs):**
- Guild emblem (existing canvas emblem renderer)
- Guild name, leader link, member count, faction
- Bank money (copper formatted) on Bank sub-tab

**Link-out:** “Open full guild page” → existing `/guild/:realm/:name` (optional; account tab may embed same widgets).

---

## Guild bank

### AzerothCore tables (characters DB)

| Table | Purpose |
|---|---|
| `guild` | `BankMoney`, emblem fields |
| `guild_bank_tab` | Purchased tabs: `TabId`, `TabName`, `TabIcon`, `TabText` |
| `guild_bank_item` | `guildid`, `TabId`, `SlotId`, `item_guid` |
| `guild_bank_right` | Per-rank permissions: `TabId`, `gbright`, `SlotPerDay` |
| `item_instance` | Item entry, enchantments, random properties |

### “Unlocked” bank tab — definition

**Guild bank tab visible when:**
1. Guild has **≥1 row** in `guild_bank_tab` (tabs purchased in-game), **and**
2. Armory **3D/item assets are available** (`assetProxyUrl` set or local DBC/meta present — same gate as character item icons).

If assets are missing, show Bank sub-tab disabled with message: *“Guild bank requires armory item data — upload armory data on the platform.”*

**Viewer permissions (v1):** Show bank contents read-only to any logged-in guild member on their own account page. Respect rank permissions in v2 if needed (`guild_bank_right`).

### Bank UI layout (classic WotLK)

```
┌─────────────────────────────────────────────────────┐
│  [Emblem]  <Guild Name>          Bank: 123g 45s 6c │
├─────────────────────────────────────────────────────┤
│  Members │ Bank │                                  │
├──────────┴──────────────────────────────────────────┤
│ [Tab1] [Tab2] [Tab3] [Tab4] [Tab5] [Tab6]         │  ← guild_bank_tab icons
├─────────────────────────────────────────────────────┤
│ ┌──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┬──┐       │
│ │  │  │  │  │  │  │  │  │  │  │  │  │  │  │       │  98 slots / tab (7×14)
│ └──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┴──┘       │
│  Tab: "Main" — "Officer notes..."                  │
└─────────────────────────────────────────────────────┘
```

**Item cells:**
- Icon from DBC `itemEntry` (same path as `CharacterController` inventory icons)
- Quality border color
- Hover tooltip: name, ilvl, enchant, random stats (reuse item tooltip builder if one exists; otherwise minimal v1)

**API:**
- `GET /account/guild/bank?realm=&guildId=&tabId=` — JSON grid for active tab (auth: must be guild member)
- Or server-render bank on account page load for first tab

---

## Privacy contract (for future forums)

When forum/comments are added:

| Context | Show |
|---|---|
| Post author | `display_name` only |
| Profile link | `/player/:displayName` or character link — **not** `/account` |
| Never | `account.username`, `account.email`, `reg_mail` |
| Moderation/admin | Platform admin tools may map display name → account (out of armory scope) |

Armory navbar after this work:
- Logged in: `{{currentUser.displayName}}` → `/account`
- Pending email flow: still show email only on verify/choose-username pages, not navbar

---

## Known bugs to fix

These are confirmed issues in the current armory codebase. **All P0/P1 bugs should be resolved in or before Phase 1** unless noted otherwise.

| ID | Bug | Priority | Phase |
|---|---|---|---|
| BUG-1 | Account template/controller mismatch — characters never list | **P0** | 1 |
| BUG-2 | `account.hbs` not whitelisted — missing from built/synced images | **P0** | 1 |
| BUG-3 | `GuildController.guildExists()` ignores guild id | **P1** | 1 or 3 |
| BUG-4 | `guild.css` missing / not synced to stack images | **P1** | 3 or 4 |
| BUG-5 | `layout.hbs` leaks `currentUser` in page-source JSON | **P1** | 2 |
| BUG-6 | Navbar shows game username (and pending email) publicly | **P1** | 2 |
| BUG-7 | `hasCharacters` passed by controller but unused in template | **P2** | 1 |
| BUG-8 | `GuildController.getGuildId()` defined but never called | **P2** | 3 |
| BUG-9 | `account.hbs` panel title exposes raw `username` | **P2** | 2 |

---

### BUG-1 — Account characters never render (P0)

**Symptom:** Logged-in user on `/account` always sees *“No characters on this account yet”* even when characters exist in-game.

**Root cause:** View model mismatch between controller and template.

| `AccountController.account()` passes | `account.hbs` expects |
|---|---|
| `realms: [{ realm, characters[] }, …]` | `realm`, `characters[]` |
| `hasCharacters: boolean` | `{{#if characters.length}}` |

Because `characters` is undefined at the top level, the `{{#if characters.length}}` branch is never taken.

**Files:**
- `frontend-armory/src/armory/controllers/AccountController.ts` — `account()` (~line 647)
- `frontend-armory/static/account.hbs`

**Fix:**
- Update `account.hbs` to `{{#each realms}}` with nested `{{#each characters}}`, **or**
- Flatten in controller for single-realm stacks only (worse for multi-realm).

**Verify:** Account with ≥1 character in `acore_characters.characters` shows linked names per realm.

---

### BUG-2 — `account.hbs` not shipped with armory image (P0)

**Symptom:** `/account` may render a stale template, empty layout, or 500 depending on what was baked into the stack’s armory static bundle.

**Root cause:** `frontend-armory/.gitignore` ignores `/static/*` with exceptions for `login.hbs`, `register.hbs`, etc. — **`account.hbs` is not excepted**. `ArmoryImageService.EnsureLayoutTemplates()` does not copy `account.hbs` from the manager image source.

**Files:**
- `frontend-armory/.gitignore`
- `backend/AzerothPlatform.Infrastructure/Services/ArmoryImageService.cs` — `LiveLayoutRootFiles`, `EnsureLayoutTemplates()`

**Fix:**
1. Add `!/static/account.hbs` to `.gitignore`.
2. Add `account.hbs` to `LiveLayoutRootFiles` and `CopyLayoutFileIfExists(..., "account.hbs")`.

**Verify:** Fresh stack build / armory static sync includes `account.hbs`; template matches controller after BUG-1 fix.

---

### BUG-3 — `guildExists()` SQL does not filter by guild id (P1)

**Symptom:** `GET /guild/:realm/:id/members` may return roster data for the wrong guild or succeed when the guild id does not exist (behaviour depends on whether any guild row exists in the DB).

**Root cause:** Broken query — parameter is bound but never used in SQL.

```sql
-- Current (wrong)
SELECT guildid FROM guild WHERE guildid

-- Expected
SELECT guildid FROM guild WHERE guildid = ? LIMIT 1
```

**Files:**
- `frontend-armory/src/armory/controllers/GuildController.ts` — `guildExists()` (~line 175)

**Fix:** Add `= ?` (or `= ? LIMIT 1`) and compare `rows.length > 0`.

**Verify:** Members endpoint returns 404 for invalid `guildId`; correct roster for valid id.

---

### BUG-4 — `guild.css` missing from repo / image sync (P1)

**Symptom:** Guild pages load without intended layout/styling; `guild.hbs` references `{{websiteRoot}}/css/guild.css` but the file may not exist in the running container.

**Root cause:** Same gitignore pattern as BUG-2 — only `theme.css` and `azp-responsive.css` are whitelisted under `/static/css/*`. `guild.css` is not committed or copied by `ArmoryImageService`.

**Files:**
- `frontend-armory/static/guild.hbs` (stylesheet link)
- `frontend-armory/.gitignore`
- `backend/AzerothPlatform.Infrastructure/Services/ArmoryImageService.cs`

**Fix:**
1. Add/commit `static/css/guild.css` (classic guild window styles).
2. Whitelist `!/static/css/guild.css` in `.gitignore`.
3. Add to `EnsureLayoutTemplates()` copy list (with `theme.css`).

**Verify:** Guild page loads stylesheet; emblem header and roster table match WotLK-style layout.

---

### BUG-5 — `layout.hbs` exposes session user in client JSON (P1)

**Symptom:** View page source on any armory page while logged in — `handlebarsData` JSON contains `currentUser.username` (game account name) or pending `email`.

**Root cause:** Global layout embeds all Handlebars locals:

```javascript
const handlebarsData = {{{JSONstringify locals}}};
```

**Files:**
- `frontend-armory/static/layout.hbs`

**Fix (pick one):**
- Stop serializing full `locals`; expose only safe, page-specific data, **or**
- Serialize a redacted object: `{ displayName, isPending }` without username/email.

**Verify:** Page source on `/character/...` while logged in contains no username or email.

---

### BUG-6 — Navbar exposes username / email (P1)

**Symptom:** Logged-in players see their **game account name** in the navbar; pending registrations see **email**.

**Root cause:** `Armory.ts` middleware sets `currentUser.username` from session; `armory-navbar.hbs` renders it as a public-facing label.

**Files:**
- `frontend-armory/src/armory/Armory.ts` — `res.locals.currentUser`
- `frontend-armory/static/partials/armory-navbar.hbs`

**Fix:** Part of display-name work (Phase 2): navbar shows `displayName` only; pending users see neutral label (*“Verify email”*) or display name placeholder — not raw email.

**Verify:** Navbar never shows `account.username` or login email after profile feature ships.

---

### BUG-7 — `hasCharacters` unused in template (P2)

**Symptom:** No functional breakage; dead controller field.

**Root cause:** Controller sets `hasCharacters: totalCharacters > 0` but `account.hbs` never references it.

**Fix:** Use `hasCharacters` for empty-state vs tab content, or remove from controller.

---

### BUG-8 — `getGuildId()` dead code (P2)

**Symptom:** None (unused).

**Root cause:** `GuildController.getGuildId()` is implemented but never called.

**Fix:** Remove or wire up where guild name → id resolution is needed (account guild tab).

---

### BUG-9 — Account page title shows raw username (P2)

**Symptom:** `/account` panel title is `{{username}}` (uppercase game login name).

**Root cause:** `account.hbs` uses session username before display-name feature exists.

**Fix:** Phase 2 — show `displayName` in UI; move game username to account-details tab as “In-game login name (private)”.

---

## Bugs to fix (prerequisites)

Summary checklist (maps to implementation phases):

| # | Bug ID | Fix | Priority |
|---|---|---|---|
| 1 | BUG-1 | Rewrite `account.hbs` to use `realms[]` / tab layout | **P0** |
| 2 | BUG-2 | Whitelist `account.hbs` in `.gitignore` + `ArmoryImageService` template sync | **P0** |
| 3 | BUG-3 | Fix `GuildController.guildExists()` → `WHERE guildid = ?` | **P1** |
| 4 | BUG-4 | Whitelist `guild.css` (and add if missing) | **P1** |
| 5 | BUG-5 | Reduce `layout.hbs` client JSON leak | **P1** |
| 6 | BUG-6 | Navbar: display name instead of username/email | **P1** |
| 7 | BUG-7 | Use or remove `hasCharacters` | **P2** |
| 8 | BUG-8 | Remove or use `getGuildId()` | **P2** |
| 9 | BUG-9 | Account panel: display name not username | **P2** |

---

## Implementation phases

### Phase 1 — Fix account characters (smallest shippable)

**Bugs closed:** BUG-1, BUG-2, BUG-7 (optional)

- Fix `account.hbs` ↔ controller data contract (BUG-1)
- Whitelist `account.hbs` + `ArmoryImageService` template sync (BUG-2)
- Fix `GuildController.guildExists()` (BUG-3) — low risk, do here if touching guild routes
- Basic tab shell (Characters tab works)
- **Verify:** logged-in user with in-game chars sees list + links

### Phase 2 — Account details + display name

**Bugs closed:** BUG-5, BUG-6, BUG-9

- `armory_account_profile` table + `AccountProfileStore`
- `POST /account/profile` update display name
- Account details tab UI
- Navbar uses display name (BUG-6)
- Strip sensitive fields from `handlebarsData` (BUG-5)
- Account panel shows display name, not username (BUG-9)

### Phase 3 — Account page tabs (Characters + Guild shell)

**Bugs closed:** BUG-3 (if not in Phase 1), BUG-4, BUG-8

- Refactor `account.hbs` → tabbed layout + `account.css` (classic panels)
- Characters tab: realm groups, class/race icons, guild column
- Guild tab: emblem header, Members sub-tab (embed existing DataTables roster)
- Guild picker for multiple guilds
- Add/sync `guild.css` (BUG-4)
- Wire or remove `getGuildId()` (BUG-8)

### Phase 4 — Guild bank

- `GuildBankService` — query tabs, items, money
- Asset availability gate
- Bank sub-tab UI (tab strip + slot grid + tooltips)
- `GET` endpoint for tab switching (optional AJAX)
- Classic WoW styling (`guild.css` / `account-guild.css`)

### Phase 5 — Polish

- Rank-based bank visibility (optional)
- Guild MOTD / info sub-tab
- Deep links `/account/guild`
- Tests: profile CRUD, bank query, permission gate, template regression

---

## Files likely touched

| Area | Files |
|---|---|
| Account | `AccountController.ts`, `account.hbs`, new `AccountProfileStore.ts` |
| Templates | `account.hbs`, `partials/account-*.hbs`, `armory-navbar.hbs`, `layout.hbs` |
| Guild | `GuildController.ts`, `GuildBankService.ts`, `guild.hbs`, `guild.css` |
| Session | `Session.ts`, `Armory.ts` middleware (`currentUser`) |
| Sync | `frontend-armory/.gitignore`, `ArmoryImageService.cs` |
| Styles | `static/css/theme.css`, new `account.css`, `guild-bank.css` |

---

## Decisions to finalize

| # | Topic | Options |
|---|---|---|
| 1 | **Display name uniqueness** | Global unique per stack vs allow duplicates |
| 2 | **Default display name** | First character name vs prompt on first `/account` visit |
| 3 | **Guild bank permissions** | Read-only for all guild members (v1) vs enforce `guild_bank_right` |
| 4 | **Bank tab count** | Show only purchased tabs vs show locked empty tabs |
| 5 | **Multiple guilds** | Picker on account tab vs separate row per guild |
| 6 | **Email on account details** | Show masked email vs hide entirely (login-only) |

### Recommended defaults (v1)

1. Display names **unique per stack** (case-insensitive).
2. Default to **first character name**; prompt to customize on first visit.
3. Bank **read-only** for any logged-in member whose character is in the guild.
4. Show **only purchased** tabs from `guild_bank_tab`.
5. **Guild picker** when multiple guilds.
6. Show **masked email** on account details when email-confirmation is enabled.

---

## Config flow (end-to-end)

```
Player logs in → session { accountId, username }
  → load armory_account_profile (or create default)
  → navbar shows displayName

/account
  → Account details: edit displayName, view masked email
  → Characters: realms[] → character links
  → Guild: pick guild → Members (roster DT) | Bank (if tabs exist + assets available)

Public /character/:realm/:name
  → No account username/email (unchanged)

Future forum post
  → author = displayName only
```

---

## Immediate next step

**Phase 1** closes **BUG-1** and **BUG-2** (and ideally **BUG-3**) in a small PR — that unblocks seeing characters on `/account` without waiting for the full tab/guild bank work.
