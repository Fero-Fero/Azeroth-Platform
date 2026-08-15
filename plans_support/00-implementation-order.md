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
| **09** | [cloud-security-group-providers](./09-cloud-security-group-providers.md) | `plans_support/` | 02 | **Cloud login Part 2A/2B:** auto firewall/SG + IAM/token scopes. Phase 1 guide tabs parallel OK |
| **10** | [wow-vpc-network-security](./10-wow-vpc-network-security.md) | `plans_support/` | 09 | **Cloud login Part 2C:** operator SSH user, root lockout, verification, fail2ban |
| **11** | [windows-os-support](./11-windows-os-support.md) | `plans_support/` | 02, 09 | Windows VPC — after Linux cloud + firewall patterns stable |
| **12** | [armory-styling-injection](./12-armory-styling-injection.md) | `plans_support/` | — | **Parallel OK** — independent of cloud/VPC work |

---

## Dependency sketch

```
01 (shipped reference)
 └── 02 OAuth master + setup dialog
      ├── 03–08 provider login (Part 1 connect + Part 2A/B/C security)
      ├── 09 Cloud firewall / SG + IAM (Part 2A/2B shared spec)
      │    └── 10 VPC hardening (Part 2C SSH/root lockout + extras)
      └── 11 Windows VPC (after 09 cloud rules pattern exists for Windows)

12 Armory styling — any time (no cloud dependency)
```

---

## Quick rules

1. **Do not** implement Part 2 automatic cloud firewall (09) before the **Configure instance** / setup dialog shell exists (02 Phase A).
2. **Do not** implement Windows cloud launch (11 Phase 3) before Linux OAuth + firewall paths (02 + 09) are stable.
3. **Do not** duplicate SSH hardening logic — shared host steps live in **#10**; provider plans **03–08 Part 2C** only add console break-glass (Instance Connect, Droplet Console, KVM, serial, Bastion).
4. Provider plans **03–08** add provider-specific login **and Part 2** (firewall + IAM + SSH). Shared UI stays in **02**.
5. **Root / default OS users** must not accept internet SSH after Part 2C. Break-glass is **provider-console only**, never a public root login.
