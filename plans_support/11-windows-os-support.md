# Windows Operating System Support — Plan

**Status:** Planning (Aug 2026)  
**Implementation order:** **#11** — after Linux cloud login is stable. See [00-implementation-order.md](./00-implementation-order.md).

---

## Executive summary

**Yes, Windows VPC support is possible**, but it is a **substantial parallel track** to today's Linux path — not a small toggle. AzerothCore stacks run as **Linux Docker containers**. A Windows remote host must therefore run Docker with **Linux container mode** (Docker Engine on Windows Server + WSL2, or an equivalent supported configuration).

Today the platform:

- Defines `RemoteHostOs.Windows` in the API and wizard schema
- Shows **Windows** in the OS selector as **Coming soon** (disabled)
- Rejects Windows in `RemoteEngineService.ProvisionRemoteHostAsync` and host-firewall sync
- Ships Linux-only bootstrap via `VpcBootstrapUserData` (bash / cloud-init)
- Assumes SSH user names, `ufw`, `apt`, and `systemctl` everywhere in first-time setup

Windows support means adding **PowerShell bootstrap**, **Windows Firewall** automation, **OpenSSH-based Docker contexts** (or a vetted alternative), and **cloud launch paths for Windows images** where providers offer them — while keeping Linux and Windows flows **isolated** so neither breaks the other.

---

## Target scenarios

| Scenario | Example | Priority |
|----------|---------|----------|
| **Cloud Windows Server VM** | AWS EC2 Windows Server 2022, Azure Windows VM, GCP Windows Server | High |
| **Dedicated Windows host** | Bare-metal or rented Windows Server with RDP + OpenSSH | Medium |
| **Home lab Windows** | Windows 11 Pro with Docker Desktop + WSL2 | Low (document only; not officially supported for production stacks) |

**Out of scope (initial release):**

- Running AzerothCore as **native Windows processes** (no Docker)
- **Windows containers** (AC images are Linux)
- Replacing SSH with **WinRM** as the primary remote engine transport (possible future phase; SSH-first matches existing architecture)
- Windows support for **local** stacks (manager-hosted Docker on Windows/WSL is a separate README topic)

---

## Architecture constraint: Linux containers on Windows hosts

AzerothCore platform stacks use Linux-based Docker images. On a Windows Server host:

```
┌─────────────────────────────────────────────────────────┐
│  Windows Server (remote VPC)                            │
│  ┌───────────────────────────────────────────────────┐  │
│  │  Docker Engine (Linux container mode / WSL2)      │  │
│  │  ┌─────────────┐ ┌─────────────┐ ┌──────────────┐ │  │
│  │  │ ac-database │ │ worldserver │ │ authserver … │ │  │
│  │  │  (Linux)    │ │  (Linux)    │ │   (Linux)    │ │  │
│  │  └─────────────┘ └─────────────┘ └──────────────┘ │  │
│  └───────────────────────────────────────────────────┘  │
│  OpenSSH Server ──► docker context over SSH (platform)    │
│  Windows Firewall ──► port allow rules (platform sync)  │
└─────────────────────────────────────────────────────────┘
```

**Minimum host requirements (proposed):**

- Windows Server 2019+ or Windows Server 2022 (Desktop Experience or Core with OpenSSH)
- OpenSSH Server installed and running
- Docker Engine with **Linux containers** (WSL2 backend on Server 2022 is the documented Microsoft path)
- Administrator or delegated user in `docker-users` group (Windows equivalent of `docker` group)
- Sufficient RAM for WSL2 + game stack (document 16 GB+ for small stacks)

---

## Current codebase gaps

| Area | Linux today | Windows gap |
|------|-------------|-------------|
| `VpcBootstrapUserData` | bash cloud-init script | No PowerShell user-data / first-logon script |
| `RemoteEngineService.ProvisionRemoteHostAsync` | Docker install, ufw, baselines | Early exit with “not supported” at line ~685 |
| `ApplyHostFirewallAsync` | `ufw` | Early exit at line ~833 |
| `CloudTerminalPanel` | SSH terminal (works if OpenSSH on Windows) | No Windows-specific UX hints |
| `CloudLaunchPanel` / catalogs | Ubuntu/Debian/Amazon Linux images | No Windows Server AMIs/images in catalog |
| `CloudLaunchService` | user_data bash | Needs PowerShell `<powershell>` wrapper for EC2, GCP metadata, Azure |
| `DeploymentStep.tsx` | Full 4-step flow | Windows radio disabled; terminal hidden |
| `ExternalVpcSecurityPanel` | ufw + AWS SG | Copy mentions Windows Firewall but no automation |
| `StackService` / persistence | `remoteOs` stored | Always forced to Linux in some code paths (~381, ~5176) |
| Cloud OAuth plans (`plans/`) | Linux bootstrap in launch | Windows bootstrap scripts per provider |

---

## Isolation strategy (Linux vs Windows)

Mirror the cloud OAuth pattern: **separate strategies**, shared orchestration.

```
IRemoteHostSetupStrategy
├── LinuxRemoteSetupStrategy    (existing logic extracted)
└── WindowsRemoteSetupStrategy  (new)
```

| Concern | Isolation rule |
|---------|----------------|
| Bootstrap script | `VpcBootstrapUserData` (Linux) vs `VpcBootstrapWindowsUserData` (PowerShell) |
| First-time setup | Strategy picked by `RemoteSetupOptionsDto.RemoteOs` |
| Firewall sync | `LinuxHostFirewallSync` (ufw) vs `WindowsHostFirewallSync` (netsh / `New-NetFirewallRule`) |
| SSH user | Linux: `ubuntu` / `debian` / `root` — Windows: `Administrator` or custom OpenSSH user |
| Cloud launch metadata | Provider client selects script encoder by target OS |
| Tests | Separate integration test fixtures per OS |

**Do not** branch on `RemoteHostOs` inside low-level SSH helpers except where command syntax differs (`cmd.exe` vs `/bin/sh` already partially exists for **local manager** shell, not remote VPC).

---

## Phase 1 — Foundation & manual Windows VPC (MVP)

**Goal:** Enable Windows OS selection and connect to an **already prepared** Windows Server (Docker + OpenSSH installed manually).

### Backend

- [ ] Remove hard-coded `RemoteHostOs.Linux` overrides in `StackService` when user selects Windows
- [ ] `WindowsRemoteSetupStrategy.ProbeAsync` — SSH echo, `docker version`, Linux container check (`docker run --rm hello-world` or inspect `OsType`)
- [ ] `TestRemoteConnection` — allow Windows path without ufw/sudo checks
- [ ] Docker context over SSH: verify `docker context create` against Windows OpenSSH (document default shell = PowerShell vs cmd pitfalls)
- [ ] Persist `remoteOs` on stack entity and honor in all external stack operations

### Frontend

- [ ] Enable **Windows** radio in `DeploymentStep` (remove `supported: false`)
- [ ] Windows-specific copy: OpenSSH user (often `Administrator`), port 22, `.pem` or key format notes
- [ ] Hide Linux-only panels when `remoteOs === Windows` (ufw checkbox → “Configure Windows Firewall”)
- [ ] Show terminal panel for Windows (SSH works the same in browser hub)

### Documentation

- [ ] README section: manual Windows Server prep checklist (OpenSSH, Docker, WSL2, firewall ports)

**Exit criteria:** Create external stack on pre-configured Windows Server; test connection passes; build/start stack over remote Docker context.

---

## Phase 2 — Windows bootstrap script & wizard First Time Setup

**Goal:** Automate Docker + OpenSSH + firewall prep on Windows like Linux First Time Setup.

### New contract: `VpcBootstrapWindowsUserData`

PowerShell script (EC2 `<powershell>` / GCP `windows-startup-script-ps1` / Azure Custom Script Extension):

1. Enable OpenSSH Server (Windows Capability)
2. Configure `sshd` (key auth, firewall rule for SSH)
3. Install WSL2 + Docker Engine (Linux containers) — or invoke documented Microsoft install path
4. Add SSH user to `docker-users`
5. Write marker file `C:\ProgramData\AzerothPlatform\bootstrap-ready`
6. Open required ports in Windows Firewall (game ports from wizard profile)

Expose via `GET /api/system/vpc-launch-user-data?remoteOs=Windows`.

### `ProvisionRemoteHostAsync` (Windows branch)

Replace early exit with:

| Step | Action |
|------|--------|
| Verify SSH | Same as Linux |
| Skip sudo | Windows: verify admin or docker-users membership |
| Docker | Install/configure if missing (PowerShell helpers) |
| Security baselines | Windows Update policy (optional, lighter than Linux unattended-upgrades) |
| Firewall | `WindowsHostFirewallSync` — allow TCP ports from stack profile + admin RDP/SSH CIDR |
| Re-test | `docker info` + Linux container smoke test |

### Frontend

- [ ] First Time Setup enabled for Windows (remove `handleSetupNow` early return)
- [ ] Step status rows for Windows (mirror Linux setup section UI)
- [ ] Bootstrap sticky panel shows PowerShell script when OS = Windows

**Exit criteria:** Fresh Windows Server VM + bootstrap script or Setup Now → test connection → create stack.

---

## Phase 3 — Cloud launch & provider Windows images

**Goal:** Launch or pick **Windows Server** instances from linked cloud accounts.

| Provider | Windows VMs | Launch approach |
|----------|-------------|-----------------|
| **AWS** | Yes (Windows AMIs) | Extend `AwsEc2Client` catalog with Windows Server AMIs; `user_data` in `<powershell>` |
| **Azure** | Yes | Add VM create (today: list + Run Command only); PowerShell bootstrap via extension |
| **GCP** | Yes | Windows Server images in catalog; `windows-startup-script-ps1` metadata |
| **DigitalOcean** | **No** (Linux only) | Hide Windows launch; manual external host only |
| **Hetzner** | **No** | Manual only |
| **Vultr** | Limited / check API | Filter catalog; document if unavailable |

### CloudLaunchService changes

- `LaunchRequest.TargetOs` = Linux | Windows
- Provider clients filter images by OS
- Bootstrap payload encoder per provider + OS
- Default SSH user: `Administrator` (AWS) / `azureuser` (Azure) / GCP metadata

### Security groups / NSG / firewall rules

| Provider | Automation |
|----------|------------|
| AWS | Extend `CloudFirewallService` — same SG sync; ensure RDP not opened unless opted in |
| Azure | NSG rule sync (new service, parallel to AWS) |
| GCP | Firewall rule sync (future) |

**Exit criteria:** Launch Windows Server EC2 from wizard; bootstrap runs; instance appears in picker flow.

---

## Phase 4 — Windows Firewall & VPC security panel

**Goal:** Parity with Linux ufw sync and `ExternalVpcSecurityPanel` checks.

- [ ] `GetWindowsFirewallStatusAsync` — parse `Get-NetFirewallRule` / port probes
- [ ] Sync allow rules for auth/world/client/armory ports from `VpcSecurityCatalog`
- [ ] UI: replace “ufw” labels with OS-aware text
- [ ] Cloud checklist dialog: Windows Server + AWS SG + Windows Firewall sections

---

## Phase 5 — Polish & edge cases

- [ ] WinRM fallback research (optional; only if OpenSSH proves insufficient for Docker CLI)
- [ ] Docker Desktop on Windows 11 — best-effort detection; warn for production
- [ ] RDP vs SSH guidance for initial break-glass access
- [ ] CRLF / PowerShell execution policy handling in cloud-init
- [ ] Remote path separators for volume/file operations over SSH docker context
- [ ] Performance tuning notes (WSL2 memory, antivirus exclusions for Docker)

---

## Remote access model

### Primary: OpenSSH Server (recommended)

- Aligns with existing `RemoteEngineService` SSH config blocks and `CloudTerminalHub`
- AWS EC2 Windows AMIs often ship with OpenSSH optional; bootstrap enables it
- Default port 22; support custom port like Linux

### Credentials

- Same SSH private key vault as Linux
- Windows OpenSSH accepts standard PEM keys when `administrators_authorized_keys` or user `.ssh/authorized_keys` configured in bootstrap

### Not in MVP: WinRM

- Would require new transport layer (`IRemoteShell` abstraction)
- Defer unless OpenSSH + Docker context proves unreliable on Server Core

---

## Frontend wizard flow (target)

| Step | Linux (today) | Windows (target) |
|------|---------------|------------------|
| 1 OS | Ubuntu/Debian | Windows Server 2019/2022 |
| 2 Connect | Cloud + SSH + bash bootstrap | Cloud (where available) + SSH + PowerShell bootstrap |
| 3 Verify | SSH + Docker | SSH + Docker (Linux mode) |
| 4 Setup | ufw + apt Docker | Windows Firewall + Docker/WSL2 install |

Cloud account tab: when OS = Windows, filter providers that support Windows launch; show notice for DO/Hetzner (“Linux only — use manual host IP”).

---

## Testing plan

| Test | Environment |
|------|-------------|
| SSH probe | Windows Server 2022 on AWS t3.large |
| Bootstrap script | Fresh EC2 Windows without Docker |
| First Time Setup | Azure Windows VM |
| Full stack lifecycle | build → start → SOAP on Windows VPC |
| Firewall sync | Open game ports; verify external connectivity |
| Regression | Existing Linux VPC E2E unchanged |

Automated CI for Windows hosts is expensive; recommend **manual QA matrix** + one optional scheduled AWS Windows integration job.

---

## Risks & mitigations

| Risk | Mitigation |
|------|------------|
| Docker on Windows is fragile vs Linux | Document supported SKUs; probe Linux container mode in test connection |
| WSL2 resource usage | Minimum RAM guidance; WSL `.wslconfig` template in bootstrap |
| Long bootstrap times | Progress steps in UI; async setup with polling |
| Provider has no Windows images | Manual host path only; clear UI messaging |
| OpenSSH + Docker context bugs | Pin tested Windows builds; community feedback loop |
| Security: opening RDP by mistake | Default deny RDP; SSH-only; explicit opt-in for RDP rule |

---

## Dependencies & related plans

- [`../plans/13-cloud-followups-and-test.md`](../plans/13-cloud-followups-and-test.md) — leftover Linux cloud work; Windows launch is a separate OS track
- Player **launcher** remains a Windows **client** artifact — unchanged by this plan

---

## Recommended implementation order

1. **Phase 1** — Manual Windows VPC (unlock OS + probe + docker context)
2. **Phase 2** — Bootstrap + First Time Setup (PowerShell)
3. **Phase 4** — Windows Firewall sync (security parity)
4. **Phase 3** — Cloud Windows launch (AWS first, then Azure/GCP)
5. **Phase 5** — Polish

**Estimated relative effort:** Phase 1–2 are the critical path; Phase 3 is largest per-provider; Phase 4 medium.

---

## Key files to touch (implementation reference)

| Layer | Files |
|-------|-------|
| Contracts | `RemoteHostOs.cs`, `VpcBootstrapUserData.cs` (+ new Windows variant), `RemoteSetupOptionsDto.cs` |
| Remote engine | `RemoteEngineService.cs`, `IRemoteEngineService.cs` |
| Cloud | `CloudLaunchService.cs`, `AwsEc2Client.cs`, `AzureComputeClient.cs`, `GcpComputeClient.cs` |
| Frontend | `DeploymentStep.tsx`, `VpcConnectionMethodTabs.tsx`, `ExternalVpcSecurityPanel.tsx`, `wizard.schemas.ts` |
| Docs | `README.md` |
