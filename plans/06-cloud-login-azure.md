# Microsoft Azure — Login & Setup Strategy

**Provider enum:** `CloudProvider.Azure`  
**Implementation order:** **#06** — after [02-cloud-oauth-login-master-plan](./02-cloud-oauth-login-master-plan.md). See [00-implementation-order](../plans_support/00-implementation-order.md).  
**Current auth:** Service principal (tenant + client ID + secret + subscription) manual paste

---

## Summary

Azure VM list and Run Command bootstrap already work with a service principal. For interactive operators, **Entra ID user login** (authorization code or device code) is more natural than pasting SP secrets.

**VM create from platform is not implemented yet** — OAuth login still helps instance pick + Run Command bootstrap + future NSG automation.

---

## Login strategy (choose per deployment context)

### Option A — Authorization code flow (default for web UI)

Best when operator uses browser on same machine as Azeroth Platform.

| Step | Action |
|------|--------|
| 1 | **Sign in with Microsoft** |
| 2 | Redirect to `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize` |
| 3 | Scopes: `openid profile offline_access` + **`https://management.azure.com/.default`** (ARM) |
| 4 | Callback → exchange code at `/oauth2/v2.0/token` |
| 5 | Validate: `GET https://management.azure.com/subscriptions?api-version=2020-01-01` |
| 6 | Subscription picker if multiple |
| 7 | Store encrypted refresh + access tokens |
| 8 | UI: **✓ Login successful** |

Use **multi-tenant** app registration (`organizations` or `common` endpoint) so customers use their own Entra tenant.

### Option B — Device code flow (headless / SSH-only operators)

When platform runs on a server without convenient browser redirect:

| Step | Action |
|------|--------|
| 1 | **Sign in with device code** |
| 2 | Backend `POST .../oauth2/v2.0/devicecode` |
| 3 | UI shows `verification_uri` + `user_code` |
| 4 | User completes login on phone/PC |
| 5 | Backend polls `/token` until success |
| 6 | Same storage + checkmark UX |

Register app with **Allow public client flows = Yes** for device code.

### Option C — Service principal (Advanced, current)

Keep manual paste for automation. `AuthMethod=Manual`.

---

## RBAC requirements (user or SP)

Minimum for current features:

| Action | ARM permission |
|--------|----------------|
| List VMs | `Microsoft.Compute/virtualMachines/read` |
| Read NIC / public IP | `Microsoft.Network/*/read` |
| Run Command bootstrap | `Microsoft.Compute/virtualMachines/runCommand/action` |

Future NSG automation:

| Action | Permission |
|--------|------------|
| NSG rules | `Microsoft.Network/networkSecurityGroups/write` |

Assign **Virtual Machine Contributor** + **Network Contributor** (scoped to resource group) as documented minimum, or custom role.

---

## Setup dialog strategy

### Tab A — Existing VM

- List Linux VMs with public IP (existing `AzureComputeClient`)
- Select → populate host, SSH user, connection metadata

### Tab B — Create VM

- **Not implemented today** — show disabled tab with “Coming soon” or link to Azure Portal
- Future: `Microsoft.Compute/virtualMachines/write` + cloud-init / Run Command post-create

### Bootstrap existing (already shipped)

- Run Command with bootstrap script when user picks existing VM without Docker
- OAuth token works same as SP token for `ComputeManagementClient`

### Security automation (Part 2)

| Feature | API | When |
|---------|-----|------|
| NSG inbound rules | ARM `networkSecurityGroups/securityRules` | **Auto on VM select** (create VM: future) |
| Azure Firewall | Separate API | Out of scope |

---

## Part 2 — Instance security (required with login)

After Connect + **Configure instance**. Shared specs: [`09`](../plans_support/09-cloud-security-group-providers.md) (2A/2B), [`10`](../plans_support/10-wow-vpc-network-security.md) (2C).

| Part | Goal |
|------|------|
| **2A NSG** | Inbound from profile; SSH admin CIDR only |
| **2B API identity** | Entra user or dedicated service principal — **not** the tenant Global Admin secret |
| **2C Host SSH** | Operator user; root/`azureuser` internet-SSH ❌; Bastion / serial / Run Command for break-glass |

### 2A — Automatic NSG setup

| Step | Action |
|------|--------|
| 1 | Resolve VM → primary NIC → NSG (create if missing) |
| 2 | Add inbound rules from stack profile |
| 3 | SSH priority rule ← admin CIDR only |
| 4 | Associate NSG to NIC if newly created |

**RBAC:** `Microsoft.Network/networkSecurityGroups/write`, `.../securityRules/write`, `.../networkInterfaces/join/action`.

**Device code / OAuth:** same token as VM list + Run Command.

---

### 2B — API identity (not the Entra Global Admin)

- Delegated ARM token or dedicated app registration / service principal
- Do not persist the tenant Global Admin password
- NSG write: `Microsoft.Network/networkSecurityGroups/write`, `.../securityRules/write`

### 2C — SSH: root locked out of the public internet

### During setup

- **Run Command bootstrap** (existing) must create operator user when run as root/`azureuser` — extend script like `VpcBootstrapUserData`
- New VM create (future): cloud-init creates operator user + key

### Final step (after stack verified)

| Account | Remote SSH | Break-glass |
|---------|------------|-------------|
| **root** | ❌ | **Run Command** (platform or portal), Serial console, Azure Bastion (if deployed) |
| **azureuser** / image default | ❌ | Azure Portal → VM → **Connect** → **Bastion** or serial console |
| **Operator user** | ✅ Admin CIDR via NSG | Normal SSH |

NSG: SSH inbound only from admin CIDR. Final hardening: `PermitRootLogin no`, `AllowUsers <operator-user>`, disable default-user static keys.

---

## Isolation from other providers

- **`AzureEntraAuthStrategy`** — Entra app registration separate from AWS/GCP
- Token audience: `https://management.azure.com/` — not Graph (unless needed for profile)
- Store `{ tenantId, subscriptionId, refresh_token, ... }` per connection
- `AzureComputeClient` accepts `TokenCredential` from OAuth or `ClientSecretCredential` from manual SP

---

## App registration checklist

- [ ] Entra app: Web redirect URI + optional public client (device code)
- [ ] API permissions: Azure Service Management → user delegated permissions
- [ ] Admin consent may be required in customer tenant
- [ ] Multi-tenant supported

---

## Implementation checklist

- [ ] `AzureEntraAuthStrategy` (auth code + device code variants)
- [ ] Subscription picker post-login
- [ ] Refactor `AzureComputeClient` credential injection
- [ ] Login button + checkmark + setup dialog
- [ ] Azure VM create (separate feature track)
- [ ] NSG sync service (Phase E)
- [ ] Operator user in bootstrap + final SSH hardening step

---

## Risks & limitations

| Risk | Mitigation |
|------|------------|
| Tenant admin consent required | Document; fallback to SP paste |
| Conditional Access / MFA | Supported in interactive flows |
| Device code phishing concerns | Use only when redirect impractical; show clear URI |
| No VM create yet | Setup dialog labels create tab accurately |

---

## References

- [Microsoft identity platform device code flow](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
- [Authorize access to ARM](https://learn.microsoft.com/en-us/azure/active-directory/managed-identities-azure-resources/overview)
- Existing: `AzureComputeClient.cs`
