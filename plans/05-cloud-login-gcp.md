# Google Cloud Platform — Login & Setup Strategy

**Provider enum:** `CloudProvider.Gcp`  
**Implementation order:** **#05** — after [02-cloud-oauth-login-master-plan](./02-cloud-oauth-login-master-plan.md). See [00-implementation-order](../plans_support/00-implementation-order.md).  
**Current auth:** Service account JSON key (manual paste)

---

## Summary

GCP supports **user OAuth** with Cloud Platform scopes — the operator signs in with their Google account and grants the platform permission to manage Compute resources in selected projects. This replaces pasting service account keys for most solo/small-team operators.

Service account JSON remains the **Advanced** path for automation and CI.

---

## Login strategy

### OAuth 2.0 authorization code + PKCE (recommended)

| Step | Action |
|------|--------|
| 1 | User clicks **Sign in with Google Cloud** |
| 2 | Backend generates PKCE verifier/challenge + `state` |
| 3 | Redirect to Google OAuth: `https://accounts.google.com/o/oauth2/v2/auth` |
| 4 | Params: `client_id`, `redirect_uri`, `response_type=code`, `scope`, `access_type=offline`, `prompt=consent` (first time for refresh token) |
| 5 | Callback with `code` |
| 6 | Exchange at `https://oauth2.googleapis.com/token` |
| 7 | Validate: call `compute.projects.list` or Cloud Resource Manager `projects.list` |
| 8 | Store refresh token + access token encrypted |
| 9 | UI: **✓ Login successful** + primary project picker if multiple projects |

### Scopes (least privilege)

| Capability | OAuth scope |
|------------|-------------|
| List VMs | `https://www.googleapis.com/auth/compute.readonly` |
| Create VMs + metadata | `https://www.googleapis.com/auth/compute` |
| List projects | `https://www.googleapis.com/auth/cloudplatformprojects.readonly` |
| Firewall rules (future) | Included in `compute` |

Avoid `cloud-platform` full scope unless necessary — triggers sensitive scope verification for public apps.

### Project selection

After OAuth, user may have access to many projects:

1. Show **project dropdown** in setup dialog (from `projects.list`)
2. Store `defaultProjectId` on `CloudProviderConnection`
3. All Compute calls use `/projects/{project}/zones/...`

### Refresh tokens

- `access_type=offline` + `prompt=consent` on first link
- Refresh via `grant_type=refresh_token`
- Google may revoke refresh if unused 6 months — surface `NeedsReauth`

### Fallback: service account JSON

Keep current paste flow (`AuthMethod=Manual`). Uses `GoogleCredential.FromJson` as today.

---

## Setup dialog strategy

### Tab A — Existing VM

- `compute.instances.aggregatedList` for selected project
- Filter: RUNNING, external NAT IP, Linux
- Map zone + instance name → wizard fields

### Tab B — Create VM

- Catalog: zones, machine types, images (existing `GcpComputeClient`)
- `instances.insert` + startup-script metadata
- Optional: generate SSH key → metadata `ssh-keys`

### Security automation (future → Part 2)

| Feature | API | Part 2 |
|---------|-----|--------|
| VPC firewall rules | `compute.firewalls.insert/patch` | **Auto on instance pick/launch** |
| Tag-based rules | Firewall target tags on instance | Required at launch |
| OS Login / IAP SSH | Optional alternative to raw SSH | Future |

Requires `compute` scope + IAM roles on user (`compute.admin` or custom role).

---

## Part 2 — Instance security (required with login)

After Connect + **Configure instance**. Shared specs: [`09`](../plans_support/09-cloud-security-group-providers.md) (2A/2B), [`10`](../plans_support/10-wow-vpc-network-security.md) (2C).

| Part | Goal |
|------|------|
| **2A VPC firewall** | Tag-based rules; SSH admin CIDR only |
| **2B API identity** | User OAuth or dedicated SA — **not** the Google Cloud org super-admin / owner key |
| **2C Host SSH** | Operator user; root/`ubuntu` internet-SSH ❌; serial / IAP for break-glass |

### 2A — Automatic VPC firewall setup

| Step | Action |
|------|--------|
| 1 | Ensure instance has network tag `azeroth-platform` (set at launch) |
| 2 | Create/update firewall rules `azeroth-platform-{stackId}-*` with profile ingress |
| 3 | SSH ← admin CIDR; game/web ← profile; deny-by-omission for 3306/7878 |
| 4 | Optional explicit **deny** rules for management ports at high priority |

**Scopes:** `compute.firewalls.create`, `compute.instances.setTags`.

---

### 2B — API identity (not the GCP org owner)

- User OAuth with `compute` scope **or** a dedicated service account JSON
- Do not store the Google account owner’s user password; SA keys must be least-privilege (`compute.admin` or custom)
- Firewall write: `compute.firewalls.create`, `compute.instances.setTags`

### 2C — SSH: root locked out of the public internet

### During setup

- **startup-script** metadata creates operator user; SSH keys in instance metadata (`ssh-keys`) list `operator-user:ssh-rsa …` not only `ubuntu:…`
- Wizard stores operator user for platform SSH

### Final step (after stack verified)

| Account | Remote SSH | Break-glass |
|---------|------------|-------------|
| **root** | ❌ | Serial port / OS Login admin (if enabled) |
| **ubuntu** (default) | ❌ | GCP Console → VM → **SSH** drop-down → **View serial port** or **Connect via IAP** (optional future) |
| **Operator user** | ✅ Admin CIDR + firewall rule | Normal SSH |

Host: same hardening as master plan. GCP VPC firewall should allow SSH only from admin CIDR to instance tag.

**Future:** [OS Login](https://cloud.google.com/compute/docs/oslogin) or IAP TCP forwarding as alternative to raw port 22 — operator user remains primary for platform automation.

---

## Isolation from other providers

- **`GcpUserAuthStrategy`** separate from service-account loader
- OAuth client: `Cloud:Gcp:OAuth:ClientId` — Google Cloud Console project dedicated to Azeroth Platform
- Token storage version: `{ "type": "oauth_user", "refresh_token", ... }` vs `{ "type": "service_account", "json" }`
- `GcpComputeClient` factory picks credential source by `AuthMethod`

---

## Google Cloud Console setup (platform operator)

1. Create GCP project for the OAuth app (not customer project)
2. OAuth consent screen (External → Testing → Production verification)
3. OAuth client: Web application, authorized redirect URI
4. Enable Compute Engine API on **customer** projects is customer's responsibility — detect and show helpful error

---

## Implementation checklist

- [ ] OAuth consent screen + client in platform GCP project
- [ ] `GcpUserAuthStrategy` (start, callback, refresh)
- [ ] Project picker after login
- [ ] Refactor `GcpComputeClient` for user credentials (`UserCredential` + refresh)
- [ ] Setup dialog integration
- [ ] Firewall automation plan (Phase E)
- [ ] Document verification requirements for sensitive scopes
- [ ] Operator user in startup-script + final SSH hardening step

---

## Risks & limitations

| Risk | Mitigation |
|------|------------|
| Google app verification for `compute` scope | Start Internal/testing mode; manual SA for early adopters |
| User IAM may lack compute permissions | Validate on link; show clear RBAC error |
| Org policies block VM create | Surface API error; link to IAM docs |
| Refresh token revocation | NeedsReauth UI + manual SA fallback |

---

## References

- [Google OAuth 2.0 scopes](https://developers.google.com/identity/protocols/oauth2/scopes)
- [Compute Engine access control](https://cloud.google.com/compute/docs/access)
- Existing: `GcpComputeClient.cs`
