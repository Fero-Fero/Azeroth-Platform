# DigitalOcean — Login & Setup Strategy

**Provider enum:** `CloudProvider.DigitalOcean`  
**Implementation order:** **#03** — after [02-cloud-oauth-login-master-plan](./02-cloud-oauth-login-master-plan.md). See [00-implementation-order](../plans_support/00-implementation-order.md).  
**Current auth:** Personal Access Token (manual paste)

---

## Summary

DigitalOcean is the **best first OAuth target**. The platform already uses DO REST APIs for droplet list/create; OAuth tokens are interchangeable with PATs on the same `Authorization: Bearer` header.

---

## Login strategy

### OAuth 2.0 authorization code flow (server-side)

| Step | Action |
|------|--------|
| 1 | User clicks **Sign in with DigitalOcean** |
| 2 | Backend `POST /api/cloud/auth/digitalocean/start` builds URL: `https://cloud.digitalocean.com/v1/oauth/authorize` |
| 3 | Popup or redirect with `client_id`, `redirect_uri`, `response_type=code`, `scope`, `state` |
| 4 | User consents in DO Cloud Console |
| 5 | Callback `GET /api/cloud/auth/digitalocean/callback?code=&state=` |
| 6 | Backend `POST https://cloud.digitalocean.com/v1/oauth/token` with `grant_type=authorization_code`, `client_secret` (server only) |
| 7 | Validate: `GET https://api.digitalocean.com/v2/account` |
| 8 | Persist encrypted tokens → `CloudProviderConnection` with `AuthMethod=OAuth` |
| 9 | Frontend shows **✓ Login successful** + team/account name |

### Scopes (minimum for Azeroth Platform)

Request granular scopes where possible; fallback alias:

| Capability | Scope |
|------------|-------|
| List/create droplets | `droplet:read`, `droplet:create`, `droplet:update` |
| SSH keys on account | `ssh_key:read`, `ssh_key:create` |
| Read account | `account:read` |
| Firewalls (future) | `firewall:read`, `firewall:create`, `firewall:update` |

Or **`read write`** alias for MVP, then narrow after testing.

### Refresh tokens

DO OAuth returns refresh tokens on authorization code exchange. Implement:

- Proactive refresh when `expires_at - 5min`
- Store refreshed access token encrypted; rotate refresh token if DO returns new one

### Fallback: manual PAT

Keep **Advanced → Paste API token** (current UX). Stored as `AuthMethod=Manual`, same API client code path with different credential loader.

---

## Setup dialog strategy

After login, **Configure instance** dialog:

### Tab A — Use existing droplet

- Call existing `GET /api/cloud/connections/{id}/instances`
- Filter: running, public IPv4, Linux image slug
- On select: populate wizard `remoteHost`, `sshUser` (from image slug), `cloudConnectionId`, `cloudInstanceId`

### Tab B — Create new droplet

- Reuse `launch-catalog` + `launch` endpoints
- Catalog: regions, sizes, images from DO API
- Launch: `user_data` bootstrap + optional generated SSH key → vault
- Post-create: poll until active IP assigned

### Security profile opt-in

| Feature | DO API | Phase |
|---------|--------|-------|
| Attach Cloud Firewall | `POST /v2/firewalls` + droplet assignment | **Part 2** (auto, default-on) |
| Open SSH from admin CIDR | Firewall inbound rule | **Part 2** |
| Open game ports from profile | Same | **Part 2** |
| Host ufw | SSH via existing `provision-remote-host` | Today |

---

## Part 2 — Instance security (required with login)

After Connect + **Configure instance**, apply all three layers where this provider allows it. Shared specs — do not fork:

- **2A/2B** [`09-cloud-security-group-providers.md`](../plans_support/09-cloud-security-group-providers.md)
- **2C** [`10-wow-vpc-network-security.md`](../plans_support/10-wow-vpc-network-security.md)

| Part | Goal |
|------|------|
| **2A Cloud firewall** | Profile ingress; SSH admin CIDR only; never 3306/7878 |
| **2B API identity** | Least-privilege token/role — **no cloud-account root keys** for daily API |
| **2C Host SSH** | Operator user; **root / image-default cannot SSH from the internet**; break-glass only via **this provider’s console** |

### 2A — Automatic Cloud Firewall setup

When operator selects or launches a droplet with **Apply network profile automatically** (default **on**):

| Step | Action |
|------|--------|
| 1 | Resolve droplet id + public IPv4 |
| 2 | Find or create Cloud Firewall `azeroth-platform-{stackId}` |
| 3 | Set inbound: SSH ← admin CIDR; 3724/8085/armory/client ← profile; **no** 3306/7878 |
| 4 | `POST /v2/firewalls/{id}/droplets` attach droplet |
| 5 | Audit log + show ✓ in setup dialog |

**OAuth scopes:** `firewall:read`, `firewall:create`, `firewall:update` (or `read write` alias).

**Fallback:** Uncheck auto → operator uses guide dialog + manual DO console steps.

See [`../plans_support/09-cloud-security-group-providers.md`](../plans_support/09-cloud-security-group-providers.md).

### 2B — API identity (not the DO account root)

- Prefer OAuth with `firewall:*` scopes over a **root-team** personal access token
- Manual PAT fallback is allowed but document: create a **dedicated token**, not the account owner’s unrestricted key
- Platform never stores DO account password; break-glass for the droplet OS is console-only (2C)

### 2C — SSH: root locked out of the public internet

### During setup

- **user_data** creates operator user (not `root`); platform SSH key on operator user
- DO account SSH keys uploaded via API should target operator user in cloud-init, not only `root`
- Wizard `sshUser` = operator username; image slug hint (`ubuntu`/`debian`) is bootstrap **starting** user only until hardening migrates access

### Final step (after stack verified)

| Account | Remote SSH | Break-glass |
|---------|------------|-------------|
| **root** | ❌ | DO Recovery Console / reset root password (avoid) |
| **ubuntu** / **debian** (image default) | ❌ | DO Droplet → **Access** → **Launch Droplet Console** (web VNC) |
| **Operator user** | ✅ Admin CIDR | Normal SSH |

Host: `PermitRootLogin no`, `AllowUsers <operator-user>`, remove default-user authorized_keys, reload sshd.

**No EC2 Instance Connect equivalent** — break-glass is Droplet web console only.

---

## Isolation from other providers

- **`DigitalOceanAuthStrategy`** implements `ICloudProviderAuthStrategy` only
- OAuth client id/secret: `Cloud:DigitalOcean:OAuth:ClientId` in config — not shared
- Token payload stored as DO-specific JSON blob inside `ProtectedCredentials` with version discriminator
- `DigitalOceanClient` accepts `ICredentialSource` interface: `GetBearerTokenAsync()` — works for PAT or OAuth access token

---

## Implementation checklist

- [ ] Register OAuth app at https://cloud.digitalocean.com/account/api/applications/new
- [ ] Callback URL: `https://{platform-host}/api/cloud/auth/digitalocean/callback`
- [ ] `DigitalOceanAuthStrategy` (start, callback, refresh, revoke)
- [ ] Extend `DigitalOceanClient` token provider abstraction
- [ ] UI: Sign in button + checkmark state on provider tab
- [ ] `CloudInstanceSetupDialog` wired for DO
- [ ] Audit log events for OAuth link/refresh/revoke
- [ ] README: DO OAuth app setup for self-hosted operators (optional BYO client)
- [ ] Operator user in user_data + final SSH hardening step

---

## Risks & limitations

| Risk | Mitigation |
|------|------------|
| DO requires `client_secret` at token exchange | Always use backend callback; no pure SPA OAuth |
| Token tied to DO team | Show team name after login; document multi-team = multiple connections |
| Write scope is broad | Start with read+write MVP; migrate to granular scopes |
| No PKCE-only public clients | Acceptable — platform is server-hosted |

---

## References

- [DigitalOcean OAuth API](https://docs.digitalocean.com/reference/api/oauth/)
- [DigitalOcean API scopes](https://docs.digitalocean.com/reference/api/scopes/)
- Existing: `DigitalOceanClient.cs`, `CloudLaunchService.cs`
