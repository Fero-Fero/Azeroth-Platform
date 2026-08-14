# Sensitive data audit

**Scan date:** 2026-08-14  
**Scope:** Tracked git source in this repository (not runtime volumes, local `.env`, or git history).

## Executive summary

**No leaked personal data was found in committed source.** The repository does not contain real names, email addresses, phone numbers, private keys, `.env` files, database dumps, or operator-specific infrastructure hostnames (e.g. EC2 DNS names).

What *is* present are **placeholder credentials in tests/migrations**, **example defaults in `.env.example`**, and extensive **documentation of where secrets live at runtime**. Operators should treat runtime data (Docker volumes, stack directories, browser storage) as highly sensitive even though it is not checked into git.

---

## Repository scan — no leaks found

| Category | Result |
|----------|--------|
| Email addresses (real) | None in tracked files (only `example.com` placeholders) |
| Phone numbers | None |
| Personal names | None |
| SSH/TLS private keys (`.pem`, `.key`) | None tracked |
| `.env` / `.env.local` | Gitignored; not in repo |
| SQLite / manager DB files | Not tracked (`/data/` gitignored) |
| AWS keys / API tokens / JWT secrets | None hardcoded in source |
| Operator VPC hostnames / stack IDs | None (only generic examples in comments/docs) |
| `bin/` / `obj/` build output | Gitignored |

Verified with pattern searches for emails, private-key headers, password assignments, cloud key formats, and `git ls-files` filters for sensitive extensions.

---

## Sensitive data the platform stores or generates (runtime)

These are **not in git** but exist on disk or in browser memory when the platform is used:

### Manager data volume (`/app/data` or `azeroth-platform-data`)

| Asset | Location / mechanism | Notes |
|-------|----------------------|-------|
| Manager SQLite DB | `azeroth-platform.db` | Per-stack DB root passwords, SOAP credentials, encrypted SSH keys, armory SMTP passwords, stack config |
| JWT signing key | `jwt-signing.key` (via `AdminAuthService`) | HS256 admin session tokens |
| Secret encryption key | `secret-protection.key` | AES-GCM key for SSH/SMTP ciphertext in SQLite |
| Generated admin password | `admin-password.txt` | Written when `ADMIN_PASSWORD` is unset at first startup |
| Stack build trees | `/app/data/stacks/{stackId}/` | May include `.env`, compose overrides, **`soap-credentials.txt`** plaintext backup |
| SSH key material | `~/.ssh/` inside manager container (`acore-ext-*.key`, `config`) | Decrypted VPC private keys written for docker-over-SSH |
| Armory / client build caches | `/app/data/armory-build`, etc. | Usually not secret; may contain operator paths |

### Browser (admin UI)

| Asset | Mechanism |
|-------|-----------|
| Admin JWT | `localStorage` key `azp_admin_token` (`frontend/src/services/api.ts`) |
| Revealed SOAP/DB passwords | Shown only after explicit UI action; copied to clipboard on demand |

### Game / armory player data

Managed in **stack MySQL** (accounts, characters, armory registration emails, etc.). Not part of this git repo; subject to your privacy policy and backup practices.

---

## Risky or weak defaults in source (not leaks, but important)

### 1. Example admin password in `.env.example`

```env
ADMIN_PASSWORD=password # change-me-to-a-strong-password
```

If copied to `.env` without change, the manager admin panel is trivially guessable.

**Mitigation:** Always set a strong unique `ADMIN_PASSWORD` before exposing the manager on a network.

### 2. Legacy SOAP default `admin` / `admin`

File: `backend/AzerothPlatform.Infrastructure/Migrations/20260503213705_AddSoapCredentials.cs`

Existing stacks at migration time were backfilled with `SoapUsername = 'admin'` and `SoapPassword = 'admin'`. New stacks use generated credentials, but imported/legacy rows may still have this pair until rotated.

### 3. Test fixture passwords (tests only)

| File | Value |
|------|-------|
| `backend/AzerothPlatform.Tests/ServerTypeRequiredModuleTests.cs` | `password123` |
| `backend/AzerothPlatform.Tests/ModuleDependencyValidationTests.cs` | `password123` |
| `backend/AzerothPlatform.Tests/SmokeTests.cs` | `SuperSecure123` |

Not used in production; safe in repo but should never be reused as real passwords.

### 4. Admin password may appear in logs (failure path only)

File: `backend/AzerothPlatform.Api/Services/AdminAuthService.cs`

If writing `admin-password.txt` fails, the generated password is logged once at **Warning** level. Avoid shipping logs to untrusted aggregators without scrubbing.

### 5. Plaintext SOAP credential backup files

File: `backend/AzerothPlatform.Infrastructure/Services/StackService.cs` → `WriteCredentialsFile`

Creates `{stackPath}/soap-credentials.txt` when initializing the in-game admin account. Restrict filesystem permissions on the data volume.

---

## Third-party disclosure (privacy)

### Public IP lookup (browser)

File: `frontend/src/lib/public-ip.ts`

When the deployment wizard or cloud SG helper resolves “your IP”, the **admin’s public IPv4** may be sent to:

- `https://api.ipify.org`
- `https://api64.ipify.org`
- `https://ifconfig.me/ip`

No other personal identifiers are sent by these calls, but the public IP itself is disclosed to those services. Use manual CIDR entry if that is unacceptable.

### External Git / package hosts

Module and core clones pull from configured GitHub URLs (operator-defined). No telemetry beyond normal git HTTPS traffic.

---

## Fields treated as secrets in code

| Data | Storage | Protection |
|------|---------|------------|
| `ExternalSshPrivateKey` | SQLite | AES-GCM via `SecretProtector` (`enc:v1:` prefix) |
| `ArmoryEmailSmtpPasswordProtected` | SQLite | Same encryptor |
| `DatabaseRootPassword` | SQLite | **Plaintext** in DB |
| `SoapPassword` | SQLite | **Plaintext** in DB |
| `ArmorySessionSecret` | SQLite | Plaintext (session signing) |
| `CLIENT_AUTH_TOKEN` / manifest keys | Per-stack Docker env | Generated at deploy time |
| Admin password | Env / generated file | Not stored in SQLite |

---

## Files and paths that must never be committed

Already gitignored or should stay local:

- `.env`, `.env.local`, `.env.*.local`
- `/data/` (entire manager data tree at repo root)
- `**/bin/`, `**/obj/`
- Any `*.pem`, `*.key`, `admin-password.txt`, `soap-credentials.txt`
- Docker volume exports, stack DB dumps, `azeroth-platform.db`
- VPC SSH private keys pasted into wizard (live in DB + `~/.ssh` in container only)

**Recommended `.gitignore` additions (optional hardening):**

```
*.pem
*.key
admin-password.txt
**/soap-credentials.txt
*.db
*.sqlite
```

---

## Recommendations

1. **Rotate** any credential that ever appeared in logs, chat, or an committed `.env` — even if this repo scan is clean.
2. **Never commit** `.env`; use `.env.example` as a template only.
3. **Restrict** access to the `azeroth-platform-data` Docker volume and manager host; it holds all stack secrets.
4. **Replace** legacy `admin`/`admin` SOAP credentials on old stacks via the UI or stack update flow.
5. **Use Caddy + TLS** and a strong `ADMIN_PASSWORD` when the manager is reachable beyond localhost.
6. **Review backups** of `/app/data` and stack MySQL — they contain full credential material.
7. **Re-scan after major changes** or before public release; this document is a point-in-time audit only (does not inspect git history or untracked working-tree files).

---

## How this scan was performed

- Ripgrep across `*.cs`, `*.ts`, `*.tsx`, `*.json`, `*.yml`, `*.md`, `*.env*`, SQL, and config patterns for emails, IPs, passwords, tokens, and private-key headers.
- Glob search for `*.pem`, `*.key`, `*.db`, `*.env`.
- `git ls-files` filter for sensitive extensions and credential-related filenames.
- Manual review of secret-handling services: `SecretProtector`, `AdminAuthService`, `MySqlConnectionFactory`, `RemoteEngineService`, `StackService.WriteCredentialsFile`, `public-ip.ts`.

For historical leaks, run separately:

```bash
git log -p --all -S '@' -- '*.env' '*.pem' '*.key'
# or use a secret scanner (gitleaks, trufflehog) on full history
```
