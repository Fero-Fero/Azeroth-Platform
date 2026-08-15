# Cloud Security Group & Firewall — Multi-Provider Plan

**Status:** Planning (Aug 2026)  
**Implementation order:** **#09** — see [00-implementation-order.md](./00-implementation-order.md). Part 1 (guide tabs) can start early; Part 2 auto-firewall requires **#02** setup dialog.

**See also:** [`10-wow-vpc-network-security.md`](./10-wow-vpc-network-security.md) — defense-in-depth catalog, additional hardening ideas, verification.

---

## Problem statement

Operators must configure **two layers** of network access on external VPC stacks:

1. **Host firewall** — Linux `ufw` (automated via First Time Setup today)
2. **Cloud edge firewall** — provider-specific (security groups, VPC firewalls, NSGs, cloud firewalls)

Today the platform is **inconsistent across providers**:

| Surface | AWS | GCP | Azure | DigitalOcean | Hetzner | Vultr |
|---------|-----|-----|-------|--------------|---------|-------|
| **Configure cloud SG** dialog tabs (`CloudSecurityGroupGuideDialog`) | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ |
| **Wizard step** “Configure cloud security group” | Manual ack only | Same | Same | Same (no DO-specific guide) | Same | Same |
| **Stack VPC overview — apply rules via API** (`ExternalVpcSecurityPanel`) | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Backend** `CloudFirewallService` | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |

Users on DigitalOcean, Hetzner, or Vultr get **no provider-specific tab** in the guide dialog and **no optional sync** on the stack overview — even though those providers expose firewall APIs.

---

## Goals

1. **UI parity** — Six provider tabs in `CloudSecurityGroupGuideDialog` with console deep-links and step-by-step instructions.
2. **High-visibility deny guidance** — The **“Do not add inbound rules for”** block must be visually prominent so operators do not skim past MySQL/SOAP exposure risks (see [Deny-rules callout](#deny-rules-callout-high-visibility)).
3. **Detect linked provider** — Pre-select tab from stack’s `cloudConnectionId` / launch metadata when known.
4. **Optional automation** — Extend `ICloudFirewallService` with per-provider strategies (like AWS today).
5. **Automatic setup (Cloud login Part 2)** — On instance pick/launch, **default-on** apply of inbound rules from stack profile for **every** linked provider (see [Part 2 — Automatic cloud firewall](#part-2--automatic-cloud-firewall-cloud-login)).
6. **Single rule source** — Continue deriving inbound rules from `VpcSecurityCatalog` / stack profile (`CloudSecurityGroupRules`).
7. **Isolation** — Each provider strategy is independent; shared orchestrator only.

---

## Shared rule model (unchanged)

All providers consume the same logical rules from `VpcSecurityProfileDto.CloudSecurityGroupRules`:

| Rule type | Typical ports | Source |
|-----------|---------------|--------|
| SSH admin | stack SSH port (22) | Operator admin CIDR (`/32`), never `0.0.0.0/0` |
| Game / auth | 3724, 8085, … | `0.0.0.0/0` or restricted CIDR |
| Web (armory/client) | profile ports | `0.0.0.0/0` typically |
| **Deny by omission** | 3306 MySQL, 7878 SOAP | Must NOT appear in cloud allow list |

Template placeholder `your-ip/32` in catalog rules is replaced with operator-supplied admin CIDR at apply time (AWS pattern today).

---

## Cloud login Part 2 — contract (all providers)

Login plans **03–08** treat **Part 2** as required work after Connect + Configure instance — not a later optional extra.

| Part | What | Shared spec |
|------|------|-------------|
| **2A Cloud firewall** | Create/update SG / NSG / Cloud Firewall / firewall group from `VpcSecurityCatalog`; SSH admin CIDR only; never 3306/7878 | This file |
| **2B API identity** | Dedicated IAM user / role / scoped token — **never persist cloud-account root keys** for daily API | This file [IAM / token permissions](#iam--token-permissions-link-accounts) + README |
| **2C Host SSH** | Dedicated operator user; **root and image-default users cannot SSH from the internet**; break-glass only via **that provider’s console** (Instance Connect, Droplet Console, KVM, serial, Bastion) | [`10-wow-vpc-network-security.md`](./10-wow-vpc-network-security.md) |

Provider login plans hold **provider-specific APIs and console break-glass paths**. Do not fork the rule model.

---

## Part 2 — Automatic cloud firewall (Cloud login)

**This is required for Cloud login Part 2A** — not an optional overview-only feature.

When the operator completes **Configure instance** in the cloud login / wizard flow (existing VM or launch new), the platform **automatically**:

1. Resolves or creates the provider firewall resource (SG, NSG, Cloud Firewall, etc.)
2. Applies **inbound allow** rules from `VpcSecurityProfileDto.CloudSecurityGroupRules`
3. Sets SSH to **admin CIDR only** (never persist `0.0.0.0/0` for port 22 after setup)
4. **Does not** add rules for `DeniedPorts` (3306, 7878)
5. Attaches the resource to the selected instance

### Setup dialog integration

**File:** `CloudInstanceSetupDialog` (master plan) + `CloudLaunchPanel` / `CloudInstancePicker`

| Control | Default |
|---------|---------|
| ☑ Apply platform network profile automatically | **Checked** |
| Admin SSH source CIDR | Detected public IP `/32` |
| Manual-only path | Uncheck → cloud SG guide dialog only |

**Triggers:**

| Event | Auto-sync |
|-------|-----------|
| Instance selected (existing) | Immediate after host written to wizard |
| Launch completes | Post-create poll → sync when IP ready |
| Stack ports saved | Re-sync if linked connection + instance id known |
| Admin IP “Use my IP” | Update SSH rule only (cloud + ufw) |

### Per-provider Part 2 checklist

| Provider | Auto-create resource | Attach on launch | Sync endpoint | OAuth/IAM scope |
|----------|---------------------|------------------|---------------|-----------------|
| AWS | `azeroth-platform-launch` / `{stackId}` SG | ✅ RunInstances | `sync-cloud-security-group` (**shipped**) | `ec2:AuthorizeSecurityGroupIngress` |
| GCP | Firewall rules `azeroth-platform-*` | `instances.insert` tags | Same endpoint | `compute.firewalls.create` |
| Azure | NSG + inbound rules | NIC association | Same endpoint | NSG `securityRules/write` |
| DigitalOcean | Cloud Firewall | Droplet assignment | Same endpoint | `firewall:create`, `firewall:update` |
| Hetzner | Cloud Firewall | Server apply | Same endpoint | Firewalls write |
| Vultr | Firewall group | Instance link | Same endpoint | Firewall write scope |

### AWS launch fix (required)

Current `AwsEc2Client` launch SG opens SSH **0.0.0.0/0**. Part 2 must pass **admin CIDR** at launch and replace with full profile after bootstrap.

### Idempotency

Same as Phase 3: skip duplicate rules; additive ingress only in v1; audit log per apply.

---

## Phase 1 — Guide dialog tabs (manual, all providers)

**Goal:** Ship UI-only provider tabs with accurate console instructions — no new backend APIs.

### Frontend changes

**File:** `frontend/src/components/stacks/CloudSecurityGroupGuideDialog.tsx`

Extend `CloudProvider` type and tab list:

```typescript
type CloudProvider =
  | 'aws' | 'gcp' | 'azure'
  | 'digitalocean' | 'hetzner' | 'vultr'
```

Add `ProviderSteps` content per provider:

| Provider | Console path | Notes |
|----------|--------------|-------|
| **AWS EC2** | EC2 → Instance → Security → Inbound rules | Existing steps |
| **Google Cloud** | VPC network → Firewall | Existing steps |
| **Azure** | VM → Networking / NSG | Existing steps |
| **DigitalOcean** | Networking → Firewalls → attach to Droplet | Create firewall, add inbound rules, assign to droplet |
| **Hetzner** | Firewalls → apply to Server | Hetzner Cloud Console firewall resource |
| **Vultr** | Products → Firewall → Linked instances | Vultr firewall group |

Each tab includes:

- Link to provider console (where stable URL exists)
- Steps to match `CloudSecurityGroupRulesCard` table
- `AdminIpHint` for SSH source CIDR
- Explicit “do not open 3306 / 7878” reminder (also surfaced in the [deny-rules callout](#deny-rules-callout-high-visibility) — not only as a list bullet)

### Deny-rules callout (high visibility)

**Problem:** In `CloudSecurityGroupRulesCard` (`VpcSecurityRolesCard.tsx`), the **“Do not add inbound rules for”** table sits below the allow-rules table inside the same gray panel. It uses only a small gray title and red cell text — easy to miss when operators skim the dialog to copy ports.

**Risk:** Opening **3306 (MySQL)** or **7878 (SOAP)** to the internet exposes database and admin control plane; this is the most critical security guidance in the dialog.

**Requirement:** Make the deny section impossible to glance over. Ship with Phase 1 (can land independently of new provider tabs).

**File:** `frontend/src/components/stacks/VpcSecurityRolesCard.tsx` — `CloudSecurityGroupRulesCard` and shared `RuleTable` (`variant="deny"`).

**Proposed UI:**

```
┌─ Inbound allow (table) ─────────────────────────────────┐
│  SSH, 3724, 8085, …                                     │
└─────────────────────────────────────────────────────────┘

┌─ ⚠ Do not add inbound rules for ────────────────────────┐  ← NEW: full-width callout
│  These ports must stay closed at the cloud edge.        │
│  Opening them exposes your database or SOAP admin API.  │
│  ┌────────┬──────────────────────────────────────────┐  │
│  │ 3306   │ MySQL — never expose publicly            │  │
│  │ 7878   │ SOAP — never expose publicly             │  │
│  └────────┴──────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

| Element | Spec |
|---------|------|
| **Container** | Distinct from allow block: `border-amber-300 bg-amber-50` or `border-red-200 bg-red-50/80` (match existing warning patterns in `ExternalVpcSecurityPanel`) |
| **Icon** | `AlertTriangle` (lucide) beside title — same pattern as VPC security warnings |
| **Title** | `text-sm font-semibold text-amber-950` — larger than current `text-xs font-medium text-gray-700` |
| **Lead sentence** | One line above table: “Do not create allow rules for these ports in your cloud firewall.” |
| **Table styling** | Keep `variant="deny"` red port/description text; optional `border-amber-200` on table wrapper |
| **Placement** | **After** allow table (operators read allow first, then hit the warning before acknowledging) |
| **Empty profile fallback** | When `profile` is missing, the amber placeholder already mentions 3306/7878 — apply the same callout styling there for consistency |

**Also update:**

- `ProviderSteps` — Replace plain `<li>Do not add inbound rules for…</li>` bullets with “See the highlighted **Do not add inbound rules for** section below” so step-by-step and rule table reinforce each other.
- Acknowledgment checkbox copy (optional): “…including **not** opening MySQL (3306) or SOAP (7878).”

**Exit criteria:** Deny block is visually distinct from allow block at a glance; no functional change to rule data from `VpcSecurityCatalog`.

### Auto-select provider tab

When dialog opens:

- If stack/wizard has `deployment.cloudConnectionId`, load connection → set tab from `connection.provider`
- Else infer from host TLD / metadata if available
- Default remains AWS for backwards compatibility

### Wizard copy updates

**File:** `frontend/src/components/wizard/steps/DeploymentStep.tsx`

- Step title stays **Configure cloud security group**
- Subtext: mention all supported providers, not only AWS/GCP/Azure
- Optional: pass `cloudConnectionId` into dialog for tab pre-selection

**Exit criteria:** Operator on DO/Hetzner/Vultr sees provider-specific checklist; acknowledgment flow unchanged.

---

## Phase 2 — Stack overview: provider-aware sync panel

**Goal:** Replace AWS-only collapsible with **provider-aware** sync panel — same engine as Part 2 auto-setup, exposed for manual re-sync and drift repair.

**Note:** Part 2 auto-setup runs at instance configuration; this panel is for **re-apply**, admin IP updates, and health display when drift is detected.

### UI refactor

**File:** `frontend/src/components/stacks/ExternalVpcSecurityPanel.tsx`

Current: hard-coded “Apply AWS security group rules”.

Target:

```
┌─ Apply cloud firewall rules (optional) ─────────────────┐
│  Provider: [auto from linked connection or dropdown]     │
│  Linked account: [select connection matching provider]   │
│  Admin SSH CIDR: [203.0.113.10/32] [Use my IP]         │
│  Resource hint: instance id / droplet id / server id     │
│  [ Apply rules ]                                         │
└──────────────────────────────────────────────────────────┘
```

| Provider | Connection filter | Resource resolution |
|----------|-------------------|---------------------|
| AWS | `CloudProvider.Aws` | EC2 instance id or public IP match (today) |
| GCP | `CloudProvider.Gcp` | Instance name + zone or network tags |
| Azure | `CloudProvider.Azure` | VM name + resource group → NSG |
| DigitalOcean | `CloudProvider.DigitalOcean` | Droplet id or public IP → firewall assignment |
| Hetzner | `CloudProvider.Hetzner` | Server id → applied firewall |
| Vultr | `CloudProvider.Vultr` | Instance id → firewall group link |

When stack has no linked account for detected provider, show link to `/admin/cloud` and **Cloud SG guide** only.

Rename generic labels:

- “Cloud security group” → **Cloud firewall** (provider-neutral subtitle explains SG vs firewall vs NSG)

---

## Phase 3 — Backend: pluggable firewall service

**Goal:** Generalize `CloudFirewallService` beyond AWS.

### Architecture

```
ICloudFirewallService
└── CloudFirewallOrchestrator
    ├── AwsCloudFirewallStrategy      (existing logic extracted)
    ├── GcpCloudFirewallStrategy
    ├── AzureCloudFirewallStrategy
    ├── DigitalOceanCloudFirewallStrategy
    ├── HetznerCloudFirewallStrategy
    └── VultrCloudFirewallStrategy
```

**Endpoint (evolve existing):**

```
POST /api/stacks/{stackId}/sync-cloud-security-group
Body: SyncCloudSecurityGroupRequestDto
  - connectionId
  - adminSourceCidr
  - instanceId? / region? / resourceGroup?  (provider-specific optional fields)
```

Orchestrator resolves connection provider → delegates to strategy. Response stays `CloudFirewallApplyResultDto` with provider-specific details in message/metadata.

### Provider API mapping

#### AWS (done)

- **Resource:** EC2 security group(s) on instance
- **API:** `AuthorizeSecurityGroupIngress`
- **IAM:** `ec2:DescribeSecurityGroups`, `ec2:AuthorizeSecurityGroupIngress`
- **Client:** `AwsEc2Client.ApplySecurityGroupIngressRulesAsync`

#### Google Cloud

- **Resource:** VPC firewall rules (priority, target tags or service account)
- **API:** `compute.firewalls.insert` / `patch`; list by network tag on instance
- **Scopes:** `compute` write
- **Strategy:** Resolve instance network tags → create/update `azeroth-platform-*` firewall rules
- **Caveat:** GCP uses **deny-by-default** implied allow via firewall rules; document tag requirement on launch

#### Azure

- **Resource:** Network Security Group (NSG) attached to NIC or subnet
- **API:** ARM `securityRules` create/update on `networkSecurityGroups`
- **RBAC:** `Microsoft.Network/networkSecurityGroups/join/action`, `.../securityRules/write`
- **Strategy:** Resolve VM → primary NIC → NSG → add inbound rules

#### DigitalOcean

- **Resource:** [Cloud Firewalls](https://docs.digitalocean.com/products/networking/firewalls/) attached to droplet
- **API:** `POST /v2/firewalls`, `POST /v2/firewalls/{id}/droplets`
- **Token scope:** write
- **Strategy:** Find or create firewall `azeroth-platform-{stackId}`, set inbound rules, attach droplet

#### Hetzner Cloud

- **Resource:** [Firewalls](https://docs.hetzner.cloud/reference/cloud#firewalls)
- **API:** `POST /v1/firewalls`, apply to server
- **Strategy:** Create/update firewall, bind to server id

#### Vultr

- **Resource:** Firewall group linked to instance
- **API:** Vultr v2 firewall endpoints
- **Strategy:** Create group, add rules, link instance

### Audit & idempotency

- Reuse `CloudAuditEventTypes.CloudFirewallApplied` with `provider` in metadata
- Skip duplicate rules (AWS `Skipped` count pattern) for all providers
- Never remove existing operator rules — **additive ingress only** in v1

---

## Phase 4 — Launch-time firewall attachment

When **Launch via platform** creates a VM, attach baseline firewall/security group at create time with **admin CIDR SSH only** (not 0.0.0.0/0), then apply full profile ports immediately after IP assignment (Part 2).

| Provider | Launch hook |
|----------|-------------|
| AWS | Already creates SG — extend with profile ports post-launch or second sync call |
| GCP | Add network tags + firewall rules on insert |
| Azure | Create NSG at launch, associate to NIC |
| DigitalOcean | Create firewall + attach on droplet create |
| Hetzner | Create firewall + apply on server create |
| Vultr | Create firewall group + link on instance create |

Integrate with `CloudLaunchService` after successful create.

---

## Phase 5 — Status checks (optional)

Today `RemoteEngineService` cloud-SG check is manual message only. Future:

- **Probe APIs** read effective rules vs expected profile (where provider API allows)
- Surface in `ExternalVpcSecurityPanel` alongside ufw status
- Mark check pass/fail per provider

Lower priority than Phases 1–3.

---

## IAM / token permissions (link accounts)

Document per provider in README (extend cloud wizard IAM section):

| Provider | Minimum for sync |
|----------|-------------------|
| AWS | See existing README JSON |
| GCP | `compute.firewalls.create`, `compute.instances.list` |
| Azure | NSG read/write on VM resource group |
| DigitalOcean | Firewalls read/write |
| Hetzner | Firewalls read/write |
| Vultr | Firewall read/write scopes on OAuth app or API key |

OAuth / token login plans (`plans/03` … `plans/08`) **Part 2B** must request firewall scopes. **Part 2C** (root SSH lockout) is specified in [`10-wow-vpc-network-security.md`](./10-wow-vpc-network-security.md).

---

## Files to modify (implementation reference)

| Layer | Files |
|-------|-------|
| Guide UI | `CloudSecurityGroupGuideDialog.tsx`, `VpcSecurityRolesCard.tsx` (deny callout + `CloudSecurityGroupRulesCard`) |
| Stack overview | `ExternalVpcSecurityPanel.tsx` |
| Wizard | `DeploymentStep.tsx` |
| API | `StacksController.cs`, `CloudFirewallDtos.cs` |
| Services | `CloudFirewallService.cs`, new `Cloud/*Firewall*.cs` clients |
| Contracts | `ICloudFirewallService.cs`, optional provider-specific DTO fields |
| Docs | `README.md`, [`01-cloud-integration-completed.md`](./01-cloud-integration-completed.md) |

---

## Implementation order (recommended)

| Order | Phase | Effort | User impact |
|-------|-------|--------|-------------|
| 0 | **Deny-rules callout** in guide dialog | Small | Reduces MySQL/SOAP misconfiguration risk immediately |
| 1 | **Part 2 auto-firewall** — AWS in setup dialog + fix launch SSH CIDR | Medium | Automatic secure ingress on instance setup |
| 2 | Phase 1 — Guide tabs for DO, Hetzner, Vultr | Small | Manual fallback parity |
| 3 | Phase 2 — Provider-aware overview panel + drift checks | Medium | Re-sync + health |
| 4 | Phase 3a — GCP + Azure automation | Large | Part 2 for hyperscalers |
| 5 | Phase 3b — DO + Hetzner + Vultr automation | Medium | Part 2 complete all providers |
| 6 | Phase 4 — Launch-time attachment hardening | Medium | No transient open SSH |
| 7 | Phase 5 — Rule probe/status | Optional | Verification |
| 8 | Additional hardening — see [`10-wow-vpc-network-security.md`](./10-wow-vpc-network-security.md) | Varies | fail2ban, TLS, dev lockdown |

---

## Related plans

- [`01-cloud-integration-completed.md`](./01-cloud-integration-completed.md) — Phases 1–5 shipped; Phase 5e AWS SG baseline
- [`../plans/03-cloud-login-digitalocean.md`](../plans/03-cloud-login-digitalocean.md) … [`08-cloud-login-hetzner.md`](../plans/08-cloud-login-hetzner.md) — **Part 2** per provider (2A firewall, 2B IAM, 2C SSH/root lockout)
- [`10-wow-vpc-network-security.md`](./10-wow-vpc-network-security.md) — defense-in-depth, extra hardening ideas, verification
- [`../plans/02-cloud-oauth-login-master-plan.md`](../plans/02-cloud-oauth-login-master-plan.md) — OAuth + setup dialog shell
- [`11-windows-os-support.md`](./11-windows-os-support.md) — Windows Firewall on host + cloud NSG/SG for Windows VMs

---

## Resolved decisions (proposed)

| Question | Decision |
|----------|----------|
| Highlight “Do not add inbound rules for”? | **Yes** — amber/red callout with icon; not plain text under allow table |
| Rename “security group” in UI? | Keep wizard step name; use “cloud firewall” as neutral umbrella in overview |
| Delete cloud rules on stack delete? | **No** in v1 — leave provider rules; operator cleans up |
| Open RDP on Windows stacks? | Separate profile rules; SSH/admin CIDR only unless opted in |
| Bare metal / no cloud account? | Show generic tab “Other / manual” with rule table only |
| Auto firewall on instance setup? | **Yes, default-on** (Part 2); manual guide if unchecked or no linked account |
| SSH at launch | **Admin CIDR only** — never ship 0.0.0.0/0 SSH in automated paths |
