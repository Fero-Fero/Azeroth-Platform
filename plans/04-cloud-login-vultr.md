# Vultr — Login & Setup Strategy

**Provider enum:** `CloudProvider.Vultr`  
**Implementation order:** **#04** — after [02-cloud-oauth-login-master-plan](./02-cloud-oauth-login-master-plan.md). See [00-implementation-order](../plans_support/00-implementation-order.md).  
**Current auth:** API key (manual paste)

---

## Summary

Vultr added a formal OAuth identity provider (2025–2026) with scoped JWT access tokens and refresh tokens. This is preferable to root API keys for third-party integrations.

---

## Login strategy

### OAuth 2.0 authorization code flow

Vultr OAuth is **organization-centric**: the platform registers an OAuth application; end users authorize it.

| Step | Action |
|------|--------|
| 1 | Platform registers OAuth app via Vultr API (one-time, operator or vendor) |
| 2 | User clicks **Sign in with Vultr** |
| 3 | Redirect to Vultr consent / authorize URL with `client_id`, `redirect_uri`, `scope`, `state`, optional PKCE |
| 4 | User approves in Vultr Console |
| 5 | Callback with authorization code |
| 6 | `POST /v2/oidc/provider/{provider-id}/token` — exchange code (+ PKCE verifier if used) |
| 7 | Validate: `GET /v2/account` with Bearer JWT |
| 8 | Store encrypted `{ access_token, refresh_token, expires_at, scopes }` |
| 9 | UI: **✓ Login successful** |

### Scopes (IAM policies attached to OAuth app)

Define scopes matching existing API key capabilities:

| Capability | Scope intent |
|------------|--------------|
| List instances | Read instances |
| Create instance | Write instances |
| SSH keys | Read/write SSH keys |
| Firewall groups (future) | Read/write firewall |

Use Vultr **Attach OAuth App Scope** API during app registration. Map scope names in platform config.

### Token lifetime

- Access token: **1 hour** (JWT RS256)
- Refresh token: use before expiry; no re-consent if refresh valid
- On 401/403: refresh once → mark `NeedsReauth` if failed

### PKCE

Supported for public clients. Use PKCE if ever doing browser-only flow; for server callback, standard code + client secret is sufficient.

### Fallback: manual API key

Keep **Advanced → Paste API key** as `AuthMethod=Manual`.

---

## Setup dialog strategy

### Tab A — Existing instance

- `GET /v2/instances` — filter active + public IP
- Select → fill wizard host fields

### Tab B — Create instance

- Reuse `launch-catalog` (regions, plans, OS)
- `POST /v2/instances` with `user_data`, SSH key id
- Poll until `server_status` ready

### Security automation

| Feature | Vultr API | Part 2 |
|---------|-----------|--------|
| Firewall group attach | Firewall API v2 | **Auto on instance pick/launch** |
| SSH CIDR restriction | Firewall rules | **Part 2** |
| Host ufw | SSH provision | Today |

OAuth tokens **cannot manage API keys** (by Vultr design) — appropriate for end-user safety.

---

## Part 2 — Automatic firewall group setup

| Step | Action |
|------|--------|
| 1 | Create firewall group `azeroth-platform-{stackId}` |
| 2 | Add inbound rules from profile (SSH admin CIDR, game/web public) |
| 3 | Link firewall group to instance id |
| 4 | OAuth scope must include firewall write |

---

## SSH hardening (operator user + final lockdown)

See master plan: [SSH access model](./02-cloud-oauth-login-master-plan.md#ssh-access-model--dedicated-operator-user--final-hardening).

### During setup

- **user_data** creates operator user; Vultr SSH key id injected for operator user via cloud-init
- Platform SSH as operator user from wizard onward

### Final step (after stack verified)

| Account | Remote SSH | Break-glass |
|---------|------------|-------------|
| **root** | ❌ | Vultr Portal → instance → **View Console** |
| **linuxuser** / image default | ❌ | Same web console |
| **Operator user** | ✅ Admin CIDR + firewall group | Normal SSH |

Firewall group: SSH restricted to admin CIDR. Host: `PermitRootLogin no`, `AllowUsers <operator-user>`.

---

## Isolation from other providers

- **`VultrAuthStrategy`** — separate OAuth client, provider-id config
- JWT validation optional on callback (verify RS256 if Vultr publishes JWKS)
- `VultrClient` uses `IBearerTokenProvider` — OAuth JWT or static API key
- Draft-mode allowlist for development before app review for public listing

---

## Implementation checklist

- [ ] Create Vultr OAuth provider + application (vendor account)
- [ ] Submit app for review when ready for public use (requires funded account, HTTPS callback)
- [ ] `VultrAuthStrategy` with refresh
- [ ] Scope definitions aligned with launch/list operations
- [ ] UI login button + setup dialog
- [ ] Document draft-user allowlist for dev/testing
- [ ] Operator user in user_data + final SSH hardening step

---

## Risks & limitations

| Risk | Mitigation |
|------|------------|
| App review delay | Manual API key fallback; draft allowlist for dev |
| 1h token expiry | Aggressive refresh before API calls |
| Scoped token can't do everything API key can | Document gaps; use manual key for admin tasks |
| OAuth app registration requires root user API key | One-time platform setup, not per end user |

---

## References

- [Vultr OAuth overview](https://docs.vultr.com/platform/iam/oauth)
- [Integrate third-party app](https://docs.vultr.com/platform/iam/oauth/access-tokens/how-to-integrate-a-third-party-app-with-vultr-oauth)
- Existing: `VultrClient.cs`
