# External VPC — Quick Beginner Setup & Security Plan

This document describes how Azeroth Platform provisions and secures a **Linux VPC** that runs
stack containers in **Docker only** (no bare-metal AzerothCore install). It adapts common
private-server hardening guidance (e.g. [Rider Jensen's WoW server guide](https://riderjensen.com/blog/create-wow-server))
to a container-first model.

## Goals

1. **Guided first-time setup** in the create-stack wizard (SSH → Docker → host firewall → cloud checklist).
2. **Defense in depth** without duplicating what Docker already enforces (bind addresses, container isolation).
3. **Clear security roles** exposed to administrators so they know what is public, VPC-only, or manager-only.
4. **Linux first**; Windows remote hosts are listed in the UI but not automated yet.

## Architecture

```
Internet → Cloud security group → Host ufw → Docker published ports → Stack containers
Manager  → SSH (admin role) + MySQL/SOAP (management role, VPC bind only)
Players  → Auth/World ports (player role)
Browsers → Armory/Client HTTP (web role)
```

Docker provides **container isolation** and **per-port bind addresses** for the management/data plane.
The platform configures those at deploy time; administrators do **not** change worldserver config for
firewall rules.

## VPC security roles

These roles are exposed via `GET /api/system/vpc-security-roles` and in the admin UI.

| Role ID | Name | Purpose | Internet exposure | Host `ufw` | Cloud SG | Docker bind | Admin can change |
|---------|------|---------|-------------------|------------|----------|-------------|------------------|
| `admin` | Admin / SSH | Platform reaches the VPC over SSH | Restrict to your IP | Allow OpenSSH | Allow 22 from admin IP | N/A (host OS) | SSH credentials in wizard |
| `player` | Player / Game | WoW auth + world protocol | **Yes** (players) | Allow TCP ports | Allow TCP ports | Always `0.0.0.0` for external stacks | **Ports** step (auth/world) |
| `web` | Player / Web | Armory website + client file server | **Yes** (browsers) | Allow TCP ports | Allow TCP ports | `PublishBindAddress` (default all IF on external) | **Armory & client web access** on stack Overview |
| `management` | Management / Data plane | MySQL + worldserver SOAP (manager automation) | **No** | Deny (no rule) | **Do not open** | Pinned to `ExternalHost` / VPC IP | Automatic for external stacks; not worldserver env vars |

### What administrators should **not** use for security

- **Worldserver environment variables** — gameplay/config only; they do not control host or Docker networking.
- **Opening MySQL/SOAP in a cloud security group** — the manager connects over the VPC; Docker binds these ports to the host's reachable address, not the public internet.

### What Docker already handles

- Containers do not run as root on the host (stack images use service users).
- Management ports can be published on a specific host IP instead of `0.0.0.0` via generated compose `.env`.
- No need for a separate “worldserver firewall role” in game config.

## Wizard flow (External VPC)

1. Choose **Linux** (Windows = coming soon).
2. Enter SSH host, user, PEM key → **Test connection**.
3. **First Time Setup → Setup Now** runs remotely:
   - Install/start Docker + Compose
   - Install `ufw`, deny-by-default, allow SSH + player ports
   - Optional: unattended security upgrades
   - Verify Docker Engine + Compose
4. Acknowledge **cloud security group** checklist (AWS/GCP inbound rules).
5. Continue wizard; on stack **create**, platform syncs firewall rules for armory/client ports when known.

## Port defaults (wizard)

| Role | Default ports | Notes |
|------|---------------|-------|
| Player | 3724 (auth), 8085 (world) | From Ports step defaults |
| Web | 8100 (armory), 8101 (client) | Default for first stack; launcher uses these |
| Management | 3306 (MySQL), 7878 (SOAP) | From Database / Ports steps; never opened on `ufw`/SG |

## Automated host commands (Linux / Ubuntu / Debian)

Setup runs over SSH with passwordless `sudo` (typical EC2 `ubuntu` user):

1. `apt-get update` + install `docker.io`, `docker-compose-v2`
2. `systemctl enable --now docker`
3. `usermod -aG docker <ssh-user>`
4. `apt-get install -y ufw`
5. `ufw default deny incoming` / `allow outgoing`
6. `ufw allow OpenSSH`
7. `ufw allow <authPort>/tcp`, `ufw allow <worldPort>/tcp` (+ armory/client when known)
8. `ufw enable`
9. Optional: `unattended-upgrades`

## Cloud security group checklist (manual v1)

For **AWS EC2** (adjust for GCP/Azure):

**Inbound — allow**

| Port | Role | Source |
|------|------|--------|
| 22 | Admin | Your IP only |
| 3724 | Player | `0.0.0.0/0` or player CIDR |
| 8085 | Player | `0.0.0.0/0` or player CIDR |
| Armory port | Web | `0.0.0.0/0` or player CIDR |
| Client port | Web | Same as armory if used |

**Inbound — deny / omit**

| Port | Role |
|------|------|
| 3306 | Management (MySQL) |
| 7878 | Management (SOAP) |

## Implementation phases

### Phase 1 (current)

- [x] Plan document (this file)
- [x] Security role catalog API + admin UI
- [x] Linux OS selector in wizard
- [x] Automated Docker + `ufw` provisioning
- [x] Cloud SG acknowledgment in wizard
- [x] External stack data-plane bind defaults to remote host IP
- [x] Post-create firewall sync for armory/client ports
- [x] Per-stack **VPC Security** panel on Overview

### Phase 2 (future)

- Restrict SSH `ufw`/SG to detected manager IP
- HTTPS for armory (Caddy on VPC or cloud proxy)
- Cloud API integration (AWS SG sync)
- Windows remote host automation

## References

- [Creating and Hosting A World of Warcraft Private Server — Rider Jensen](https://riderjensen.com/blog/create-wow-server) — `ufw` for 3724/8085, non-root SSH user, initial server setup
- Platform README — [External setup](./README.md#external-setup--servers-on-a-remote-host), [Security hardening](./README.md#security-hardening)
- `DockerOptions.ExternalDataPlaneBindAddress` — global override for management bind on external engines
