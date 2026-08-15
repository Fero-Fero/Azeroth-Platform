# Hetzner Cloud — Login & Setup Strategy

**Provider enum:** `CloudProvider.Hetzner`  
**Implementation order:** **#08** — after [02-cloud-oauth-login-master-plan](./02-cloud-oauth-login-master-plan.md). See [00-implementation-order](../plans_support/00-implementation-order.md).  
**Current auth:** API token manual paste

---

## Summary

Hetzner Cloud **does not provide a public OAuth 2.0 flow** for third-party applications to obtain scoped tokens on behalf of users. Authentication is exclusively via **project API tokens** generated in the Hetzner Console.

The platform should implement a **guided “Connect Hetzner project” flow** that mimics login UX (steps, validation, checkmark) without OAuth — isolated from OAuth providers to avoid conflating strategies.

---

## “Connect” strategy (OAuth-equivalent UX)

### Guided connection wizard (not OAuth)

| Step | Action |
|------|--------|
| 1 | User clicks **Connect Hetzner project** |
| 2 | Inline guide: Security → API tokens → Generate → **Read & Write** |
| 3 | User pastes token once into secure field |
| 4 | Backend validates: `GET https://api.hetzner.cloud/v1/servers` (or `/locations`) |
| 5 | Optional: `GET /projects` if available to show project name |
| 6 | Encrypt token → `CloudProviderConnection` with `AuthMethod=Manual` (subtype: `HetznerProjectToken`) |
| 7 | UI: **✓ Project connected** (checkmark — same visual language as OAuth success) |

### Why not fake OAuth?

- No authorization server to redirect to
- Scraping Hetzner Console login is fragile and violates ToS
- Token paste with excellent UX is the industry norm for Hetzner (Terraform, hcloud CLI)

### Token permissions

| Platform need | Hetzner permission |
|---------------|-------------------|
| List servers | Read |
| Create server + SSH keys | **Read & Write** |

Warn if validation succeeds but create probe fails (read-only token detected).

### Token rotation

- No refresh token — user must regenerate in Console and **Reconnect**
- UI: “Token invalid” → Reconnect button with same guided flow

---

## Setup dialog strategy

Identical structure to OAuth providers for UI consistency.

### Tab A — Existing server

- `GET /v1/servers` — filter running + public IPv4
- Map `ubuntu`/`debian` image → SSH user hint
- Select → wizard host fields

### Tab B — Create server

- Catalog: locations, server types, images (existing `HetznerCloudClient`)
- `POST /v1/servers` with cloud-init `user_data`
- Upload SSH key via `POST /v1/ssh_keys`

### Security automation

| Feature | Hetzner API | Part 2 |
|---------|-------------|--------|
| Cloud firewall | Firewalls API | **Auto on server pick/launch** |
| Host ufw | SSH provision | Today |

Hetzner Firewalls are separate resources — Part 2 creates `azeroth-platform-{stackId}`, applies rules, binds server.

---

## Part 2 — Instance security (required with login)

After Connect + **Configure instance**. Shared specs: [`09`](../plans_support/09-cloud-security-group-providers.md) (2A/2B), [`10`](../plans_support/10-wow-vpc-network-security.md) (2C).

| Part | Goal |
|------|------|
| **2A Cloud Firewall** | Create/bind firewall; SSH admin CIDR only |
| **2B API identity** | Project API token (Read & Write) — **not** the Hetzner account password; no OAuth exists |
| **2C Host SSH** | Operator user; root/default internet-SSH ❌; **Hetzner KVM Console** for break-glass |

### 2A — Automatic Cloud Firewall setup

| Step | Action |
|------|--------|
| 1 | `POST /v1/firewalls` with inbound rules from profile |
| 2 | SSH ← admin CIDR; game/web ← profile |
| 3 | Apply firewall to server id |
| 4 | Token must be **Read & Write** |

---

### 2B — API identity (project token, not account root login)

- Hetzner has no OAuth — a **project** Read & Write token is the least-privilege option they offer
- Do not store the Hetzner Console account password
- Token must include Firewalls write for 2A

### 2C — SSH: root locked out of the public internet

### During setup

- **cloud-init user_data** creates operator user; Hetzner SSH key resource attached via cloud-init to operator user
- Map image → initial bootstrap user hint, but wizard stores **operator user** as long-term SSH identity

### Final step (after stack verified)

| Account | Remote SSH | Break-glass |
|---------|------------|-------------|
| **root** | ❌ | Hetzner Console → server → **Console** (KVM) |
| **ubuntu** / **debian** | ❌ | Same KVM console |
| **Operator user** | ✅ Admin CIDR + Hetzner Firewall | Normal SSH |

Apply Hetzner Cloud Firewall: SSH from admin CIDR only. Host hardening matches master plan.

---

## Isolation from other providers

- **`HetznerTokenAuthStrategy`** implements `ICloudProviderAuthStrategy` but:
  - `StartLogin()` → opens guided paste modal (no redirect URL)
  - `CompleteLogin()` → validate token API call
  - No OAuth callback route registered
- Never store Hetzner tokens in OAuth token table/format
- `HetznerCloudClient` unchanged — Bearer token header

---

## UI copy guidelines

| Avoid | Use |
|-------|-----|
| “Sign in with Hetzner” (implies OAuth) | **Connect Hetzner project** |
| “Login failed” | **Connection failed — check token** |
| OAuth terminology in docs | **API token connection** |

Checkmark on success is still appropriate — connection established.

---

## Implementation checklist

- [ ] `HetznerTokenAuthStrategy` (validate-only connect flow)
- [ ] Step-by-step token creation guide with screenshots/links to Hetzner docs
- [ ] Read vs Read&Write detection (probe create permission or document)
- [ ] Reconnect / rotate token UX on Cloud settings page
- [ ] `CloudInstanceSetupDialog` integration
- [ ] Future: Hetzner Firewall sync service (Phase E)
- [ ] Operator user in cloud-init + final SSH hardening step

---

## Risks & limitations

| Risk | Mitigation |
|------|------------|
| No OAuth / no scoped fine-grained tokens | Document Read&Write requirement; least-privilege not available |
| Token tied to single project | One connection per project; label clearly |
| User loses token (Hetzner shows once) | Reconnect flow; cannot recover old token |
| Community desire for OAuth | Monitor Hetzner roadmap; strategy slot ready if they ship OAuth |

---

## References

- [Hetzner API token generation](https://docs.hetzner.com/cloud/api/getting-started/generating-api-token/)
- [Using the Hetzner API](https://docs.hetzner.com/cloud/api/getting-started/using-api/)
- Existing: `HetznerCloudClient.cs`
