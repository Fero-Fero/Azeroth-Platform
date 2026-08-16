# Cloud follow-ups and live testing

**Status:** Open  
**Folder:** `plans/`  
**Depends on:** shipped Linux cloud login (Connect → launch/pick firewall → Verify VPC → `SetupIncomplete` drafts)

Cloud login for DigitalOcean, Vultr, GCP, Azure, AWS, and Hetzner is **shipped**. This plan is only leftover product, deferred identity options, remaining host-hardening extras, and a live test matrix. Do not re-introduce Configure vs Skip. Do not call AWS or Hetzner Connect “OAuth.”

See also: [Windows VPC](../plans_support/11-windows-os-support.md), [armory styling](../plans_support/12-armory-styling-injection.md), [module DBC/MPQ aggregation](./14-module-dbc-mpq-aggregation.md).

---

## Left unchecked (from shipped login plans)

| Item | Why it is still open | Suggested work |
|------|----------------------|----------------|
| **Azure create VM** | Explicit separate track. Pick + Run Command + NSG is shipped; Create tab is Coming soon. | New Linux VM via ARM (cloud-init + NSG at create). Keep pick path working. |
| **Hetzner OAuth** | Hetzner has no public OAuth API. | Monitor their roadmap. Strategy slot is `HetznerTokenAuthStrategy`. Do not fake a redirect. |
| **AWS Identity Center OIDC** | Deferred until enterprise demand. AssumeRole + pasted keys are the product. | Doc stub only until a customer asks. |
| **Entra app in the customer tenant** | Operator config, not platform code: Web redirect URI, optional public client (device code), Azure Service Management delegated permission, admin consent, multi-tenant. | Document in README; verify on a real tenant during the test matrix below. |
| **fail2ban (SSH, optional HTTP)** | Part 2C extras. Operator user + Finalize SSH is shipped. | Optional host install after Finalize; do not claim it on Verify VPC. |
| **SSH admin CIDR after bootstrap** | Empty CIDR still uses `0.0.0.0/0` so launch does not crash. | If a CIDR is known at launch/pick, do not leave 22 world-open. Prompt when the operator IP changes. |
| **Admin CIDR refresh** | No banner when the operator’s public IP changes. | One-click update of cloud SSH rule + ufw. |
| **Ongoing profile drift job** | Verify probes at wizard time; stack overview can re-apply. | Optional scheduled compare of cloud + ufw vs `VpcSecurityCatalog`. |
| **Staging lockdown** | Game/web ports are public by default. | Profile flag: player ports admin-CIDR-only. |
| **Outbound egress / IPv6 / non-standard SSH port** | Hardening backlog. | Only if a real operator need shows up. |

Do **not** persist cloud-account root keys. Dedicated role / scoped token / project token only. Never open MySQL 3306 or SOAP 7878.

---

## Live test matrix

Automated tests cover auth DTOs and firewall helpers. The gaps below need a real provider account. Restart/rebuild the API after backend changes; frontend `npx tsc -b` after UI changes.

### Shared (every provider)

- [ ] Connect stores `cloudConnectionId` on the wizard form; Back does not wipe it.
- [ ] Advanced paste still works (CI / air-gapped).
- [ ] **Launch** applies host user-data (Docker `docker.io` alone, full `!use_pty` sudoers, ufw) **and** the provider edge firewall.
- [ ] **Pick existing** applies the same edge firewall (AWS/Azure also bootstrap the host).
- [ ] Empty admin CIDR does not crash; SSH may be `0.0.0.0/0` only as bootstrap.
- [ ] Ports: SSH 22, auth 3724, world 8085, armory 8100, client 8101. **Not** 3306 or 7878.
- [ ] Generated `.pem` downloads; Verify certificate then Verify VPC.
- [ ] Verify VPC checks host **and** cloud firewall. Wait 1–2 minutes after launch (user-data is async).
- [ ] Repair host setup still works on a brownfield VM; then Verify again.
- [ ] Unfinished stack: `SetupIncomplete`, Finish setup, no PEM in draft JSON, tags show provider + instance type.
- [ ] Finalize SSH on the stack VPC panel; break-glass is console-only (see table).
- [ ] Overview “Apply … firewall” re-syncs without wiping extra operator rules (merge/additive).

| Provider | Connect | Launch / pick | Break-glass after Finalize |
|----------|---------|---------------|----------------------------|
| AWS | Connect AWS account (IAM role) + Advanced keys | Create EC2 + SG; pick via SSM | Instance Connect as `ubuntu` |
| DigitalOcean | Sign in with DigitalOcean (read+write) | Create droplet + Cloud Firewall; pick applies firewall | Droplet Console |
| Vultr | Sign in with Vultr (firewall write scope) | Create instance + firewall group; pick applies group | View Console |
| GCP | Sign in with Google Cloud + project picker | Create VM + tag `azeroth-platform` + VPC firewall; pick tags + rules | Serial / IAP |
| Azure | Sign in with Microsoft (PKCE) or device code + subscription | Pick + Run Command + NSG. Create VM **not shipped** | Bastion / serial / Run Command |
| Hetzner | **Connect Hetzner project** (Read & Write token; write probe). Not OAuth. | Create server + Cloud Firewall; pick applies firewall | KVM Console |

### Provider-specific checks

- [ ] **AWS:** AssumeRole External ID + CloudFormation; Reconnect rotates the role; SG probe on Verify.
- [ ] **DigitalOcean:** OAuth refresh; read-only token cannot attach firewall.
- [ ] **Vultr:** Token refresh before launch/Verify; draft-app allowlist documented.
- [ ] **GCP:** `access_type=offline`; NeedsReauth after revoked refresh; Compute Engine enabled on the project.
- [ ] **Azure:** Device code path; subscription stored on `DefaultProjectId`; Create tab stays Coming soon until Azure create ships.
- [ ] **Hetzner:** Read-only token fails **at connect** (firewall probe), not at Verify. Reconnect is the only recovery. Copy is “Connection failed — check token,” never “Sign in with Hetzner.”

### Guide / copy

- [ ] Cloud security-group guide has six provider tabs and pre-selects from the stack’s provider.
- [ ] Deny callout for 3306/7878 is visually distinct from the allow table.

---

## Implementation order

1. Run the live test matrix on at least AWS + one OAuth provider + Hetzner.
2. Azure create VM (if needed).
3. SSH CIDR tightening + IP-change prompt.
4. fail2ban and other extras only after 1–3.
5. Identity Center / Hetzner OAuth only if the vendor or a customer requires it.
