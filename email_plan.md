# Email confirmation for armory registration

Plan for adding optional email-verified registration to stack setup and the armory player auth flow.

---

## Goals

| Requirement | Design choice |
|---|---|
| Toggle during stack setup | `useEmailConfirmation` on stack config |
| Registration uses email when ON | Replace username field with email on `register.hbs` |
| **Login uses email when ON** | Replace account name field with email on `login.hbs` |
| Email must be verified before play | Pending registration gate before `account` row exists |
| After verify → pick WoW username | New “choose username” step after token validation |
| Unverified users blocked at login | Login with email routes to “verify your email” page until verified + username chosen |
| Email provider + template config | New optional wizard step + stack settings page |
| Step can be skipped initially | Stack can be created with toggle ON but `emailConfigured: false` |
| Toggle OFF hides everything | No wizard step, no armory UI changes, no email env vars injected |
| Email uniqueness | One email per stack; duplicate register shows a fixed generic message (see [Email uniqueness](#email-uniqueness)) |

---

## Current state (codebase)

- Stack creation is a **6-step admin wizard** (`CreateStackWizardPage.tsx`) that persists `StackConfigurationDto` to `ManagedStacks`.
- Armory is **not a wizard step** today; player accounts are enabled by default at runtime via `ACORE_ARMORY_ACCOUNTS__*` env vars in `DockerComposeOverrideGenerator.cs`.
- Armory registration/login writes directly to `acore_auth.account` via SRP6 (`AccountController.ts`).
- The `account` table has `email` and `reg_mail` columns; armory registration currently inserts empty strings for both.
- **No SMTP or outbound email infrastructure exists** in the platform today.

---

## Recommended auth model

**Do not** insert unverified users into `account`. AzerothCore expects a real `username` at insert time.

Introduce a pending table in the auth database (per stack):

```sql
armory_pending_registration (
  id,
  email,
  salt,
  verifier,                    -- SRP6 credentials (same scheme as account)
  verification_token_hash,
  expires_at,
  created_at,
  verified_at,
  account_id NULL              -- set after username is chosen
)
```

### Registration (email confirmation ON)

1. User submits **email + password** (no username).
2. Normalize email (lowercase, trim) and enforce [email uniqueness](#email-uniqueness). If the email is already pending or already on an `account` row, reject with the canonical registration error (do not create a row or send mail).
3. Armory stores a pending row and sends a verification email.
4. User lands on **“Check your email”** page (with option to resend).

### Verification link

4. `GET /verify-email?token=...` validates the token and sets `verified_at`.
5. Redirect to **“Choose your username”** (3–16 chars, same rules as today).
6. On submit → insert into `account` with SRP6 salt/verifier from the pending row, set `email` / `reg_mail`, link `account_id`, delete or archive the pending row, log the user in.

### Login (email confirmation ON)

- Login form label and field change from **“Account name”** to **“Email”**.
- User submits **email + password** (not username).
- Lookup order:
  1. If a matching **pending** row exists and is not fully activated (no `account_id` or not verified): authenticate SRP6 against pending credentials → redirect to **“Verify your email to continue”** (resend + logout only).
  2. If a matching **account** row exists (by `email` / `reg_mail`): authenticate SRP6 against account → normal session.
  3. Otherwise: show **“Invalid email or password.”** (no email-existence leak).

After verification + username selection, subsequent logins use **email + password** and resolve the `account` row by email. The chosen **username** is only used for the WoW game client, not the armory login form.

### Login (email confirmation OFF)

- Current flow unchanged: **account name + password** at registration and login.

### Unverified gate page

Users who are not fully activated (registered but email unverified, or verified but username not yet chosen) see **“Verify your email to continue”** or the appropriate step page, with only:

- **Resend verification email** (if not yet verified)
- **Continue** (if verified and ready to choose username — link to choose-username page)
- **Log out**

No armory navigation, account page, or character links until the flow is complete.

---

## Configuration model

Add to stack configuration (DB + DTOs):

```typescript
armoryAccounts: {
  useEmailConfirmation: boolean   // master toggle
  emailConfigured: boolean        // false if wizard step was skipped
}

armoryEmail?: {                   // only when useEmailConfirmation = true
  smtpHost: string
  smtpPort: number
  smtpSecurity: 'none' | 'starttls' | 'tls'
  smtpUsername: string
  smtpPassword: string            // encrypted at rest (like SSH keys)
  fromAddress: string
  fromName: string
  verificationSubject: string
  verificationBodyHtml: string    // placeholders: {{verifyUrl}}, {{siteName}}, {{expiryHours}}
}
```

### Storage

- Persist on `ManagedStackEntity` (e.g. new JSON column `ArmoryEmailConfigJson` + boolean flags), or extend `ServiceEnvVarsJson` under `armory`.
- Prefer **entity fields + encrypted secret** for the SMTP password; inject non-secret values and the password into armory env at runtime (same pattern as `ArmorySessionSecret`).

### Runtime injection (`DockerComposeOverrideGenerator`)

When `useEmailConfirmation && emailConfigured`:

```
ACORE_ARMORY_ACCOUNTS__EMAIL_CONFIRMATION_ENABLED=1
ACORE_ARMORY_EMAIL__SMTP_HOST=...
ACORE_ARMORY_EMAIL__SMTP_PORT=...
ACORE_ARMORY_EMAIL__SMTP_SECURITY=...
ACORE_ARMORY_EMAIL__SMTP_USERNAME=...
ACORE_ARMORY_EMAIL__SMTP_PASSWORD=...
ACORE_ARMORY_EMAIL__FROM_ADDRESS=...
ACORE_ARMORY_EMAIL__FROM_NAME=...
ACORE_ARMORY_EMAIL__VERIFICATION_SUBJECT=...
ACORE_ARMORY_EMAIL__VERIFICATION_BODY_HTML=...
```

When toggle OFF: omit all `ACORE_ARMORY_EMAIL_*` vars; armory behaves as today.

When toggle ON but `emailConfigured: false`: set `EMAIL_CONFIRMATION_ENABLED=1` but armory treats registration as **disabled** until SMTP is configured (banner in admin + message on register page).

---

## Stack wizard UX

### Master toggle

Add an **“Armory accounts”** section to the **Advanced** step (or a small dedicated subsection):

- [ ] Allow player registration (existing, via env)
- [ ] **Require email confirmation before account activation** ← new master toggle

When toggle OFF, hide all email-related UI in the wizard, review step, and stack details.

### New optional step: “Email delivery”

Insert before **Review**, only visible when toggle ON.

| Field | Required if step completed? |
|---|---|
| SMTP host | Yes |
| SMTP port | Yes |
| Security (TLS / STARTTLS / none) | Yes |
| SMTP username / password | Yes (or explicit “no auth” for open relay) |
| From address / name | Yes |
| Verification email subject | Yes (sensible default provided) |
| Verification email body (HTML) | Yes (template with `{{verifyUrl}}`, `{{siteName}}`, `{{expiryHours}}`) |
| **Send test email** button | Optional; skipping test is allowed with a warning (see Decisions) |

Footer actions:

- **Continue** — save config, set `emailConfigured: true` (test email optional; see Decisions)
- **Skip for now** — allow proceeding; set `emailConfigured: false`

If the email step is skipped or completed without a successful test send, show a **persistent warning on the stack overview page** until SMTP is configured and a test succeeds (optional but recommended).

### Review step

- Toggle OFF → no email section shown.
- Toggle ON + configured → show SMTP summary (host, from address; never show password).
- Toggle ON + skipped → warning: *“Email confirmation is enabled but not configured — armory registration is disabled until email is set up.”*

### Post-create

Banner on stack details / **overview**: **“Complete email setup”** when `useEmailConfirmation && !emailConfigured`. This warning persists until email delivery is fully configured (not only during the first visit).

---

## Armory UI changes

| Page | When email confirmation ON |
|---|---|
| `register.hbs` | **Email** + password (not account name) |
| `login.hbs` | **Email** + password (not account name) |
| `verify-email-pending.hbs` | “Verify your email to continue” + resend + logout |
| `verify-email.hbs` | Token landing (success / invalid / expired) |
| `choose-username.hbs` | Post-verification username picker (WoW login name) |

| Page | When email confirmation OFF |
|---|---|
| `register.hbs` | Account name + password (unchanged) |
| `login.hbs` | Account name + password (unchanged) |

### Session middleware

When `emailConfirmationEnabled`:

- After session load, if user is pending or incomplete → allow only `/verify-email*`, `/choose-username`, `/logout`, and static assets.
- Block `/account`, `/character/*`, and other authenticated routes until verified and username chosen.

---

## Email sending

### Phase 1 (recommended): send from armory container

- Add an SMTP client (e.g. `nodemailer`) in `frontend-armory`.
- Read `ACORE_ARMORY_EMAIL_*` from `Config.ts`.
- Keeps auth + email in one place; matches the existing direct-DB auth pattern.

### Phase 2 (optional later): send via platform API

- Only if SMTP secrets must never live in the armory container.
- More moving parts; defer unless required.

### Template rendering

- Simple placeholder replacement in armory: `{{verifyUrl}}`, `{{siteName}}`, `{{expiryHours}}`.
- Ship a default HTML template; editable in wizard and stack settings.

### Security

- Verification token: cryptographically random, stored hashed, single-use, expires (e.g. 24–48 hours).
- Rate-limit registration, login attempts, and resend: **3 resends per hour per email** (see Decisions).
- **Registration duplicate email:** always use the canonical message below — never reveal pending vs registered.
- **Login failure:** always use **“Invalid email or password.”** — never reveal whether the email exists.

### User-facing error messages (canonical)

| Situation | Message |
|---|---|
| Register with email already pending or on an existing account | **Unable to create an account with this email. Try signing in or use a different email.** |
| Login with wrong email/password (email confirmation ON) | **Invalid email or password.** |
| Login with wrong account name/password (email confirmation OFF) | **Invalid username or password.** (unchanged) |

---

## Post-setup management

On **Stack details** (new sub-tab or section under Armory / Environment):

- Master toggle: **Use email confirmation**
- Same SMTP + template fields as the wizard email step
- **Send test email**
- Status: configured / not configured
- Warning when disabling is **blocked** if pending registrations exist (see Decisions)
- Note: changing SMTP may require armory restart

---

## Implementation phases

### Phase 1 — Foundation (platform + config)

- DTOs + DB migration (`ManagedStackEntity`, `StackConfigurationDto`, frontend types)
- Wizard toggle + optional email step (with skip)
- Review warnings + stack details “complete setup” banner
- `DockerComposeOverrideGenerator` env injection
- Encrypted storage for SMTP password

### Phase 2 — Armory auth + DB

- SQL migration for `armory_pending_registration` (stack auth DB init or armory startup)
- Extend `IAccountsConfig` + `Config.ts` with `emailConfirmationEnabled` and email settings
- Registration, login (email-based), verification, choose-username controllers
- Email uniqueness check on register + canonical `authError` message in `register.hbs`
- Unverified / incomplete session gate middleware
- New Handlebars templates; update `login.hbs` and `register.hbs` labels and validation

### Phase 3 — Email delivery

- SMTP client in armory
- Send verification email on register
- Resend verification (rate limited)
- Test email from wizard + stack settings

### Phase 4 — Polish

- Expired token UX
- Admin visibility: pending registration count (optional)
- Block disabling email confirmation while pending registrations exist
- Tests: token validation, email login lookup, username collision, email uniqueness, toggle-off regression

---

## Decisions (finalized)

| # | Topic | Decision |
|---|---|---|
| 1 | **Resend limits** | **3 resends per hour per email address.** Applies to the “resend verification” action on the pending page. Further attempts return a friendly rate-limit message. |
| 2 | **Username rules after verify** | **Same as today:** 3–16 characters, letters, numbers, dashes, underscores. Stored **uppercase** in `account.username` (AzerothCore convention). |
| 3 | **Email uniqueness** | **Confirmed.** One email → one registration path per stack. Duplicate register uses the canonical message: *“Unable to create an account with this email. Try signing in or use a different email.”* See [Email uniqueness](#email-uniqueness). |
| 4 | **Test email in wizard** | **Optional.** User may skip the test send, but a **warning must persist on the stack overview page** until SMTP is configured (and ideally until a test succeeds). Registration/login remain disabled until `emailConfigured: true`. |
| 5 | **Disable while pending users exist** | **Block disable.** The operator cannot turn off email confirmation while any row exists in `armory_pending_registration`. They must wait for pending users to complete verification, expire, or be manually cleared (future admin tool) before disabling. |

### Email uniqueness (confirmed)

**What it means:** Each email address may only be tied to **one** signup on this stack — either in progress (pending) or already completed (active account).

**Why it matters:** Without this, the same person could register multiple times with one email, or two different people could conflict if emails are reused. It also avoids ambiguity at login when armory authenticates by email.

**Rules:**

1. **On register**, normalize email (lowercase, trim) and reject if:
   - A row already exists in `armory_pending_registration` for that email, or
   - A row already exists in `account` where `email` or `reg_mail` matches (AzerothCore uses both columns; check both so existing game accounts are not duplicated).

2. **Error shown to user (registration only):** Always this exact string — no variation, no extra detail:

   > **Unable to create an account with this email. Try signing in or use a different email.**

   Do **not** reveal whether the email is pending vs already registered (prevents account enumeration). Do **not** use this message on login failure (login uses *“Invalid email or password.”*).

3. **Re-register after expiry:** If a pending row expired and was deleted (or marked inactive), the same email may register again.

4. **After verification:** The chosen username must still be unique in `account.username`; email remains the armory login identifier and is stored on `account.email` and `account.reg_mail` when the account row is created.

**What it does *not* mean:** It does not require globally unique emails across multiple stacks on the same platform — only **per stack** (each stack has its own auth database).

---

## Suggested v1 scope (smallest shippable slice)

1. Wizard toggle + skippable email step + post-setup completion UI
2. Pending table + email registration + email login + verification link + choose username
3. “Verify your email” gate page with resend + logout
4. SMTP via armory env + one default HTML template
5. Registration and login **disabled** (not broken) when toggle ON but email not configured

---

## Files likely touched

| Area | Files |
|---|---|
| Wizard | `frontend/src/pages/CreateStackWizardPage.tsx`, new `EmailConfirmationStep.tsx`, `frontend/src/schemas/wizard.schemas.ts`, `ReviewStep.tsx`, `AdvancedStep.tsx` |
| Types | `frontend/src/types/stack.types.ts` |
| Platform API | `StackConfigurationDto.cs`, `ManagedStackEntity.cs`, `StackService.cs`, `DockerComposeOverrideGenerator.cs`, `ServiceEnvTemplateService.cs` |
| Stack settings | `StackDetailsPage.tsx`, new email config component |
| Armory runtime | `frontend-armory/src/armory/Config.ts`, `AccountController.ts`, new verification controllers/middleware |
| Armory templates | `login.hbs`, `register.hbs`, `verify-email-pending.hbs`, `verify-email.hbs`, `choose-username.hbs` |
| Migrations | Stack auth DB init for `armory_pending_registration` |

---

## Config flow (end-to-end)

```
Wizard: useEmailConfirmation toggle
  → optional Email delivery step (or skip)
  → StackConfigurationDto → POST /api/stacks → ManagedStackEntity

Stack start
  → DockerComposeOverrideGenerator injects ACORE_ARMORY_ACCOUNTS__EMAIL_CONFIRMATION_ENABLED
     and ACORE_ARMORY_EMAIL_* when configured
  → Armory Config.ts reads env

Armory (email confirmation ON)
  → Register: email + password → pending row → send email
  → Login: email + password → pending gate OR account session
  → Verify link → choose username → account row → full access

Armory (email confirmation OFF)
  → Register/login: account name + password (unchanged)
```
