# Implementation Order — Platform Plans

**Purpose:** Numbered plans avoid parallel work that conflicts (OAuth setup dialog vs firewall sync vs SSH hardening vs Windows bootstrap).

Implement in this order unless a row says **parallel OK**.

| # | Plan | Folder | Depends on | Notes |
|---|------|--------|------------|-------|
| **01** | [cloud-integration-completed](./01-cloud-integration-completed.md) | `plans_support/` | — | **Reference only** — Phases 1–5 already shipped |
| **02** | [cloud-oauth-login-master-plan](../plans/02-cloud-oauth-login-master-plan.md) | `plans/` | 01 | OAuth architecture, setup dialog, Part 2 shell, SSH hardening Phase F |
| **03** | [cloud-login-digitalocean](../plans/03-cloud-login-digitalocean.md) | `plans/` | 02 | First OAuth provider (Phase B) |
| **04** | [cloud-login-vultr](../plans/04-cloud-login-vultr.md) | `plans/` | 02 | OAuth provider |
| **05** | [cloud-login-gcp](../plans/05-cloud-login-gcp.md) | `plans/` | 02 | OAuth provider |
| **06** | [cloud-login-azure](../plans/06-cloud-login-azure.md) | `plans/` | 02 | OAuth provider |
| **07** | [cloud-login-aws](../plans/07-cloud-login-aws.md) | `plans/` | 02 | Cross-account IAM (Phase C) — not classic OAuth |
| **08** | [cloud-login-hetzner](../plans/08-cloud-login-hetzner.md) | `plans/` | 02 | Guided token connect (Phase D) |
| **09** | [cloud-security-group-providers](./09-cloud-security-group-providers.md) | `plans_support/` | 02 | **Parallel OK:** Phase 1 guide tabs + deny callout before OAuth. **Part 2 auto-firewall** after setup dialog (02) exists |
| **10** | [wow-vpc-network-security](./10-wow-vpc-network-security.md) | `plans_support/` | 09 | Extra hardening, verification, fail2ban — after firewall automation baseline |
| **11** | [windows-os-support](./11-windows-os-support.md) | `plans_support/` | 02, 09 | Windows VPC — after Linux cloud + firewall patterns stable |
| **12** | [armory-styling-injection](./12-armory-styling-injection.md) | `plans_support/` | — | **Parallel OK** — independent of cloud/VPC work |

---

## Dependency sketch

```
01 (shipped reference)
 └── 02 OAuth master + setup dialog + SSH hardening
      ├── 03–08 provider login plans (order among providers flexible after 02 Phase A)
      ├── 09 Cloud firewall / SG (Part 2 needs 02; Phase 1 UI can start early)
      │    └── 10 VPC network hardening extras
      └── 11 Windows VPC (after 09 cloud rules pattern exists for Windows)

12 Armory styling — any time (no cloud dependency)
```

---

## Quick rules

1. **Do not** implement Part 2 automatic cloud firewall (09) before the **Configure instance** / setup dialog shell exists (02 Phase A).
2. **Do not** implement Windows cloud launch (11 Phase 3) before Linux OAuth + firewall paths (02 + 09) are stable.
3. **Do not** duplicate SSH hardening in provider plans — follow **02 Phase F** once per stack lifecycle.
4. Provider plans **03–08** only add provider-specific OAuth/token/scopes; shared UI stays in **02**.
