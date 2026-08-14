# WoW Server VPC — Network Security Hardening Plan

**Status:** Planning (Aug 2026)  
**Implementation order:** **#10** — after **#09**. See [00-implementation-order.md](./00-implementation-order.md).

**Related:** [`09-cloud-security-group-providers.md`](./09-cloud-security-group-providers.md) (provider firewall automation), [`../plans/02-cloud-oauth-login-master-plan.md`](../plans/02-cloud-oauth-login-master-plan.md) (Part 2 automatic setup), SSH hardening in master plan Phase F.

---

## Security model — four layers

```
Internet
    │
    ▼
┌─────────────────────────────────────┐  Layer 1: Cloud edge firewall
│  SG / NSG / Cloud Firewall / Vultr FG │  (provider API — Part 2 automation)
└─────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────┐  Layer 2: Host firewall (ufw)
│  Linux ufw allow/deny                │  (First Time Setup — shipped)
└─────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────┐  Layer 3: Docker publish bind
│  Management → host IP / 127.0.0.1    │  (MySQL 3306, SOAP 7878 — not 0.0.0.0)
│  Player/web → 0.0.0.0 or host IP     │  (3724, 8085, armory, client)
└─────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────┐  Layer 4: Application / SSH hardening
│  sshd, fail2ban, TLS, operator user  │  (Phase F SSH plan)
└─────────────────────────────────────┘
```

**Rule source of truth:** `VpcSecurityCatalog.BuildProfile()` — same ports for ufw, cloud allow list, and UI guides.

| Role | Ports | Cloud inbound | Host ufw | Docker bind |
|------|-------|---------------|----------|-------------|
| Admin / SSH | 22 (or custom) | **Admin CIDR only** | Allow admin CIDR | N/A |
| Player / game | 3724, 8085 | Public (`0.0.0.0/0`) or dev CIDR | Allow public | `0.0.0.0` OK |
| Player / web | armory, client HTTP | Public or restricted | Allow public | Configurable |
| Management | 3306, 7878 | **Never allow** | **Never allow** | Host IP / loopback only |

---

## Additional security ideas (recommended backlog)

### Tier 1 — High value, fits current architecture

| Idea | Layer | Description | Status |
|------|-------|-------------|--------|
| **Automatic cloud firewall on setup** | 1 | Part 2 of cloud login — apply profile ingress on instance pick/launch (all 6 providers) | Planned |
| **SSH admin CIDR only** | 1 + 4 | Never leave SSH open to `0.0.0.0/0` after bootstrap; fix AWS launch SG (today opens 22 globally) | Planned |
| **Deny-rules UI callout** | UI | Highlight MySQL/SOAP must not be opened | Planned |
| **Dedicated operator SSH user** | 4 | Non-root user; final hardening locks root/default user | Planned |
| **Profile sync verification** | 1 + 2 | Job compares cloud rules + ufw + Docker publishes vs `VpcSecurityProfile` | Planned |
| **Admin CIDR refresh prompt** | UI | When operator IP changes, banner to update SG + ufw SSH rule | Planned |
| **Dev / staging lockdown mode** | 1 + 2 | Profile flag: game + web ports admin CIDR only (no public players) | Planned |
| **Default deny inbound at cloud edge** | 1 | Create firewall/SG with no rules, then add only profile allows (not “allow all + add”) | Planned |

### Tier 2 — Strong hardening, moderate effort

| Idea | Layer | Description |
|------|-------|-------------|
| **fail2ban (SSH)** | 4 | Install on First Time Setup; ban repeated SSH failures from non-admin IPs |
| **fail2ban (HTTP)** | 4 | Optional jail for armory/client brute-force (careful with legit players) |
| **Outbound egress rules** | 1 | Cloud firewall: allow HTTPS/DNS/NTP/apt outbound; deny rest (test carefully — Docker pulls, AC updates) |
| **IPv6 parity** | 1 + 2 | If instance has public IPv6, mirror rules on v6 or disable IPv6 on edge |
| **ICMP restriction** | 1 | Block inbound ping from internet (optional; breaks some monitoring) |
| **Non-standard SSH port** | 1 + 4 | Optional stack setting; update profile + SG + ufw together |
| **Rate-based SG / WAF signals** | 1 | AWS: note Shield Standard; HTTP: future reverse proxy + rate limit for armory |
| **TLS termination** | 4 | HTTPS for armory + client portal via Caddy/Traefik on host (only 443 public) |
| **Geo-IP restrict SSH** | 1 | Optional admin CIDR + country allowlist on cloud firewall (provider-dependent) |
| **Audit every firewall apply** | 1 | Extend `CloudAuditLogs` with before/after rule snapshot |

### Tier 3 — Advanced / optional

| Idea | Layer | Description |
|------|-------|-------------|
| **Private subnet + bastion** | 1 | Game VM no public IP; admin via SSM/bastion (heavy ops change) |
| **AWS Network ACLs** | 1 | Subnet NACL default deny as belt-and-suspenders below SG |
| **GCP VPC firewall priorities** | 1 | Explicit deny rules for 3306/7878 at high priority |
| **Azure Application Security Groups** | 1 | Tag NICs; NSG rules by ASG not IP |
| **DDoS alerting** | 1 | CloudWatch / provider metrics on SYN flood to game ports |
| **Honeypot port detection** | 2 | Scan host for unexpected listening ports; warn in VPC overview |
| **Manager-only SOAP tunnel** | 3 + 4 | SSH tunnel or WireGuard for SOAP/MySQL instead of any host exposure |
| **Automatic SG rule cleanup** | 1 | Remove `0.0.0.0/0` SSH rules created at launch after profile sync |

### WoW-specific notes

- **Game ports (3724/8085)** must stay **public** for a live server — DDoS risk is accepted; mitigate with provider DDoS protection and host connection limits where available.
- **Armory/client HTTP** benefit most from **TLS + rate limiting**; game protocol cannot sit behind typical HTTP CDNs.
- **SOAP (7878)** is full worldserver admin — treat like root access; never on cloud SG (already in `DeniedPorts`).
- **MySQL (3306)** — same; Docker should bind to host private IP only; platform already checks `0.0.0.0` publish warnings in stack discovery.

---

## Part 2 — Automatic cloud firewall setup (cloud login)

**Requirement:** When an operator links a cloud account and completes **Configure instance** (pick existing or launch new), the platform **automatically** creates or updates inbound firewall / security group / NSG rules from the stack profile — not a manual optional step.

### Default UX (setup dialog)

```
┌─ Configure VPC instance ─────────────────────────────────────┐
│  … instance picker / launch …                                 │
│  ☑ Apply platform network profile automatically (recommended) │
│     • SSH from your admin IP only                             │
│     • Game + web ports from profile                           │
│     • Never open MySQL (3306) or SOAP (7878)                │
│  Admin SSH source: [203.0.113.10/32]  [Use my IP]          │
│  [ Select instance ] → triggers sync after host is known      │
└───────────────────────────────────────────────────────────────┘
```

| When | Action |
|------|--------|
| **Launch new VM** | Create provider firewall resource at launch (or immediately post-create); attach to instance; rules from profile |
| **Pick existing VM** | Resolve instance → find/create firewall → apply ingress rules |
| **Stack ports change** | Re-sync cloud rules on save (idempotent) |
| **Admin IP changes** | Prompt + one-click update SSH rule across cloud + ufw |

Checkbox **on by default**; Advanced: “Configure manually in cloud console” skips API sync (guide dialog only).

### Per-provider automation (Part 2)

| Provider | Resource | Auto-create | Auto-attach | API phase |
|----------|----------|-------------|-------------|-----------|
| **AWS** | EC2 security group | ✅ `azeroth-platform-{stackId}` | ✅ on launch / sync | **Shipped** (extend launch SG: admin CIDR not 0.0.0.0/0) |
| **GCP** | VPC firewall rules | ✅ `azeroth-platform-*` + network tags | ✅ tag on instance | Phase 3 |
| **Azure** | NSG + rules | ✅ create or reuse | ✅ associate to NIC | Phase 3 |
| **DigitalOcean** | Cloud Firewall | ✅ create | ✅ assign droplet | Phase 3b |
| **Hetzner** | Cloud Firewall | ✅ create | ✅ apply to server | Phase 3b |
| **Vultr** | Firewall group | ✅ create | ✅ link instance | Phase 3b |

OAuth / IAM scopes must include firewall write — see [`09-cloud-security-group-providers.md`](./09-cloud-security-group-providers.md).

### Launch-time SSH gap (fix)

Today AWS launch creates SG with **SSH 0.0.0.0/0** (`AwsEc2Client`). Part 2 must:

1. Prompt for admin CIDR **before** launch (or use detected public IP)
2. Create launch SG with SSH **admin CIDR only**
3. Post-bootstrap: apply full profile (game + web ports)
4. Optional: remove overly broad rules from default VPC SG if attached

---

## Verification & ongoing health

### VPC security panel (target state)

| Check | Source |
|-------|--------|
| Cloud firewall matches profile | Provider API read |
| ufw active + rules match | SSH probe (`RemoteEngineService`) |
| Docker publishes: management not on 0.0.0.0 | Container inspect |
| SSH not root / default user | Optional sshd config probe |
| Admin CIDR stale | Compare last sync IP vs current |

Mark overall **healthy / action needed** with fix buttons: **Sync cloud firewall**, **Sync ufw**, **Update admin IP**.

### Scheduled audit (optional)

Nightly job for external stacks: diff profile vs cloud vs ufw; notify if drift.

---

## Implementation priority

| Order | Item |
|-------|------|
| 1 | Part 2 auto-firewall in setup dialog (AWS first — already has backend) |
| 2 | Fix AWS launch SSH 0.0.0.0/0 → admin CIDR |
| 3 | Remaining provider strategies (GCP, Azure, DO, Hetzner, Vultr) |
| 4 | Profile verification checks in VPC overview |
| 5 | fail2ban in First Time Setup |
| 6 | Dev lockdown mode + TLS for web |
| 7 | Egress rules + advanced items |

---

## Related plans

- [`09-cloud-security-group-providers.md`](./09-cloud-security-group-providers.md) — provider tabs, backend strategies, phases
- [`../plans/02-cloud-oauth-login-master-plan.md`](../plans/02-cloud-oauth-login-master-plan.md) — Part 2 in Phase E/G
- [`../plans/03-cloud-login-digitalocean.md`](../plans/03-cloud-login-digitalocean.md) … [`08-cloud-login-hetzner.md`](../plans/08-cloud-login-hetzner.md) — per-provider Part 2 details
- [`11-windows-os-support.md`](./11-windows-os-support.md) — Windows Firewall parity
