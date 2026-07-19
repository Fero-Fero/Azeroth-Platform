# Azeroth Platform

[![CI](https://github.com/Fero-Fero/AzerothPlatform/actions/workflows/ci.yml/badge.svg)](https://github.com/Fero-Fero/AzerothPlatform/actions/workflows/ci.yml)

A modern, web-based control panel for private World of Warcraft 3.3.5a (Wrath of the Lich King)
servers built on [AzerothCore](https://www.azerothcore.org/). It deploys, builds, configures, and
manages your servers in Docker — and it distributes the game client and a branded launcher to your
players — all from one dashboard. No prior AzerothCore or Docker experience required to get started.

You only need **Docker** on your machine. The .NET and Node toolchains run inside containers.

---

## Table of Contents

1. [Overview](#1-overview)
   - [What it can do (fully functional)](#what-it-can-do-fully-functional)
   - [Planned](#planned)
   - [About AzerothCore](#about-azerothcore)
2. [Architecture](#2-architecture)
   - [Technology stack](#technology-stack)
   - [How it works](#how-it-works)
   - [Data model (named volumes)](#data-model-named-volumes)
3. [Installation & Setup](#3-installation--setup)
   - [Prerequisites](#prerequisites)
   - [Local setup — everything on this machine](#local-setup--everything-on-this-machine)
   - [External setup — servers on a remote host](#external-setup--servers-on-a-remote-host)
   - [Security hardening](#security-hardening)
   - [Everyday commands](#everyday-commands)
4. [Getting Started (first tour)](#4-getting-started-first-tour)
   - [Log in](#log-in)
   - [Dashboard layout: global vs. per-stack](#dashboard-layout-global-vs-per-stack)
   - [Create & start your first server](#create--start-your-first-server)
   - [Create an account & connect a client](#create-an-account--connect-a-client)
5. [Configuration Reference](#5-configuration-reference)
   - [Environment variables (.env)](#environment-variables-env)
   - [Where data lives](#where-data-lives)
6. [Development](#6-development)
7. [Troubleshooting](#7-troubleshooting)
8. [Feature Reference](#8-feature-reference)
   - [Your servers (stacks)](#your-servers-stacks)
   - [Players: accounts & characters](#players-accounts--characters)
   - [Getting the game to players](#getting-the-game-to-players)
   - [Gameplay content & customization](#gameplay-content--customization)
   - [Configuration (Configs & Environment Variables)](#configuration-configs--environment-variables)
   - [Keeping it running](#keeping-it-running)
9. [Contributing](#9-contributing)
10. [Credits](#10-credits)
11. [License & disclaimer](#11-license--disclaimer)

---

## 1. Overview

Azeroth Platform turns the whole lifecycle of an AzerothCore server — cloning, compiling,
configuring, patching, updating, monitoring, and distributing to players — into point-and-click
operations. You create **stacks** (each one a full server: auth + world + database), run them locally
or on remote hosts, and manage everything through a React + ASP.NET Core dashboard that talks to
Docker through an allowlisted socket proxy.

### What it can do (fully functional)

**Deploy & build**
- 🧙 **6-step guided setup wizard** with real-time validation (port conflicts, name uniqueness).
- 🔨 **Automated builds** — clones, compiles, and containerizes AzerothCore from source with live
  progress via SignalR, plus streaming build logs.
- 🏗️ **Multi-stack support** — run unlimited isolated servers side by side.
- 🖥️☁️ **Local or external deployment** — run a stack on this machine, or ship it to a remote host
  over SSH (SSH keys encrypted at rest).
- 💾 **Draft persistence** — the wizard saves progress to your browser.

**Run & operate**
- 🎛️ **Lifecycle control** — Start / Stop / Restart run as **background jobs** (safe to navigate away).
- 🩺 **DB Maintenance** — bring up only the database for patching/maintenance while the game servers
  stay down (`Degraded` state).
- 📊 **Real-time monitoring** — live container status, health checks, and uptime.
- 📋 **Container log viewing** — per-container streaming logs with filter/search/auto-scroll.
- 🔄 **Automatic update detection** (hourly) + 🔁 **one-click Update** that snapshots first, rebuilds,
  and reapplies your patch SQL in order.
- 🕓 **Revisions** — point-in-time DB + config snapshots with one-click restore.

**Content & customization**
- 📦 **Module system** — browse/select community modules in the wizard, and manage the catalog (add
  your own from a git repo or an uploaded `.zip`, read each module's README).
- ⚙️ **Guided module configuration** for AH Bot, AutoBalance, Playerbots, and Transmog (others fall
  back to generic `.conf`/env editing).
- 🧩 **Stack patches/migrations** — incremental SQL, DBC edits (via the WDBX sidecar), map overrides,
  client MPQ patches, **config overrides** (`config/*.json` → server and module `.conf` files), and
  **patch Lua scripts** (`lua/` → worldserver), applied strictly in order with per-file SQL transactions
  and tracing.
- 🌙 **Lua scripts** — ship Lua scripts (via the AzerothCore Lua Engine, `mod-ale`) to the worldserver
  from the **Lua Scripts** tab or from a patch's `lua/` folder on apply.
- 🛡️ **Armory** — per-stack character/guild site with equipment tooltips and a 3D model viewer;
  upload the model-viewer assets and one-click **DBC sync** for rich tooltips/titles.

**Players & distribution**
- 👥 **Account management** (SOAP) — create, set GM level, ban/unban, reset password, delete.
- 🧑‍🤝‍🧑 **Character management** — kick, ban, mute, revive, set level, rename, customize, send
  messages/items/money, add items, view inventory.
- 🤖 **AH Bot setup** — one-click Auction House Bot account with Alliance & Horde characters.
- 🚀 **Player launcher** — compile a branded, self-contained Windows launcher (in a Docker sidecar),
  with per-stack profiles, shared base client + per-profile overlays, and verified self-update.
- 📡 **Launcher propagation check** — ping every stack to confirm the built launcher reached it, with
  a re-send button per stack.
- 🧾 **Signed client manifests** — the launcher rejects tampered files and only enables **Play** when
  the client is verified and up to date.
- 🧰 **Client & addon distribution** — serve a WoW client and managed addons (global and per-stack).
- 📰 **News / patch notes** — global default plus per-stack overrides, delivered into the launcher.

**Configuration**
- 🔧 **Server Config editor** — edit real `worldserver.conf`, `authserver.conf`, and module `.conf`
  files, then apply & restart.
- 🧪 **Environment Variables editor** — searchable, per-container env-var overrides.
- ⚙️ **Edit Configuration** — change ports, passwords, max players, and realm name.

**Security (default deployment)**
- 🔐 TLS reverse proxy (Caddy) + HSTS + security headers; the manager binds to loopback only.
- 🚫 No raw Docker socket — an allowlisted socket proxy, manager runs non-root with dropped caps.
- 🛑 Deny-by-default auth on every endpoint; rate-limited login/registration; encrypted secrets at rest.

### Planned

- 📢 Server-wide announcements.
- 💽 Automated database backup/restore and **scheduled backups**.
- 📈 Performance metrics & analytics dashboard.
- 🧭 Configuration presets/templates for common server profiles.
- 🔔 Health notifications (email/Discord) when a server goes down or a build fails.
- 🧪 Expanded automated testing (unit, integration, E2E).
- 🏛️ Multi-architecture images (ARM64).
- 🗄️ Full Item DB, including custom items.
- 🐯 3D view for pets & mounts
- 📈 Patches/progression templates

### About AzerothCore

[AzerothCore](https://github.com/azerothcore/azerothcore-wotlk) is a production-ready,
community-driven WotLK server emulator: stable (rigorous CI), blizzlike, modular, open source
(GNU GPLv2), and built on the foundations of MaNGOS, TrinityCore, and SunwellCore.

---

## 2. Architecture

Azeroth Platform runs as a containerized web app and manages AzerothCore stacks by talking to the
Docker daemon through an allowlisted socket proxy.

### Technology stack

**Frontend:** React 19 + TypeScript, Vite, TailwindCSS v4, React Router v7, React Query, SignalR
client, React Hook Form + Zod, Axios.

**Backend:** ASP.NET Core 10 Web API, EF Core + SQLite (manager metadata), Docker.DotNet, SignalR,
Serilog.

**Infrastructure:** Docker & Docker Compose, MySQL 8.4 (AzerothCore databases), Caddy reverse proxy
(TLS + HSTS + security headers), and an allowlisted `docker-socket-proxy` (the manager never mounts
the raw Docker socket).

### How it works

1. **Manager container** runs the React frontend + .NET backend as a non-root user.
2. **Docker socket proxy** lets the manager drive the host Docker daemon through an allowlisted API.
3. **Wizard flow** collects configuration across 6 steps (type, database, ports, modules, advanced,
   review).
4. **Build orchestration** clones AzerothCore, installs modules, generates docker-compose, and builds
   images.
5. **Real-time updates** stream build progress, stack lifecycle jobs, and container status over
   SignalR.
6. **Stack management** provides lifecycle control and configuration editing.
7. **Update tracking** checks Git hourly for new commits and flags outdated stacks.

### Data model (named volumes)

The manager drives the host Docker daemon through the allowlisted proxy and keeps everything in
**Docker-managed named volumes** — there are no host bind mounts:

- Manager state (SQLite DB, builds, client base, launcher builds/branding, custom modules) lives in
  the `azeroth-platform-data` volume at `/app/data`.
- Each stack's runtime data (databases, config, logs, client base/overlay/cache, `ac-client-data`,
  `lua_scripts`, armory assets) lives in **per-stack named volumes**.
- The manager seeds those volumes from its own data volume via short-lived helper containers — a
  volume-to-volume copy locally, or a `tar` stream over SSH for external stacks.

See [DOCKER.md](./DOCKER.md) for the full container/volume reference and endpoint tables.

---

## 3. Installation & Setup

### Prerequisites

- **Docker** 20.10+ with **Docker Compose v2** ([install Docker](https://docs.docker.com/get-docker/))
- **OS**: Linux, macOS, or Windows with WSL2
- **RAM**: 8 GB minimum (16 GB+ recommended — each running server uses ~2–4 GB)
- **Disk**: 20 GB+ free per server (compiling produces large build artifacts)
- **Free ports**: `80` and `443` on the machine
- **Internet** access (to download the game server source and Docker images)

### Local setup — everything on this machine

Run the dashboard **and** your servers on the same computer. Ideal for a home server, a spare PC, or
just trying things out.

```bash
# 1. Get the code
git clone https://github.com/Fero-Fero/AzerothPlatform.git
cd AzerothPlatform

# 2. Create your config file
cp .env.example .env
```

Open `.env` and set two things:

1. **`ADMIN_PASSWORD`** — your dashboard login. Pick something strong. (Leave it blank and the
   platform generates one and logs where it saved it on first startup.)
2. **`HOST_LAN_IP`** — this machine's LAN IP so friends on your network can connect. Skip it and only
   *this* machine can connect:
   ```bash
   # macOS
   echo "HOST_LAN_IP=$(ipconfig getifaddr en0)" >> .env
   # Linux
   echo "HOST_LAN_IP=$(hostname -I | awk '{print $1}')" >> .env
   ```

Then start it (no folders to create — all state lives in Docker):

```bash
docker compose up -d --build
```

Open **https://localhost/admin** (accept the self-signed certificate warning) and log in with your
`ADMIN_PASSWORD`. Continue to [Getting Started](#4-getting-started-first-tour).

### External setup — servers on a remote host

The **dashboard still runs on your machine**, but a stack whose deployment target is **External** is
built locally and shipped to a remote Docker host (a cloud VPS/droplet) over **SSH**, where it
actually runs.

- Containers and all per-stack data volumes live **on the remote host**. Your machine only stores that
  stack's config row and its (encrypted) SSH key — no game data. Turning your laptop off doesn't stop
  a remote server.
- You can mix local and external stacks freely from the same dashboard.

**Remote host requirements:** reachable over SSH from the manager, Docker installed, and the SSH user
able to run it (e.g. a non-root user in the `docker` group).

**Steps:**

1. Install and launch the platform (see [Local setup](#local-setup--everything-on-this-machine)).
2. Click **Create Stack** and go through the wizard.
3. On the **Advanced** step choose the **External** target and provide the host, SSH port, username,
   and private key.
4. Build. Players connect to the remote host's address (used automatically as the realmlist).

> The SSH key that reaches a remote host is powerful. Keep the manager machine and its
> `azeroth-platform-data` volume secure, use a dedicated least-privilege key, and prefer a non-root
> SSH user in the `docker` group.

### Security hardening

The default `docker compose` deployment is safe to expose to an untrusted network. Do the essentials
before letting anyone else reach it:

1. **Set a strong `ADMIN_PASSWORD`** — never leave the default.
2. **Keep it behind the reverse proxy** — only the `caddy` service should be reachable from other
   machines; the manager binds to `127.0.0.1` only. Don't publish its internal port publicly.
3. **Use real TLS for a public server** — point a domain at this host, open `80`/`443`, and set:
   ```bash
   SITE_ADDRESS=play.yourdomain.com
   TLS_EMAIL=you@yourdomain.com
   ```
   Caddy obtains and renews a Let's Encrypt certificate automatically.

**Stack exposure:** game ports (auth `3724`, world `8085`) are published on all interfaces so players
can connect; the armory/client HTTP and MySQL/SOAP data plane default to **loopback**
(`STACK_PUBLISH_BIND` / `STACK_DATAPLANE_BIND`). Only widen these behind a firewall, and **never**
expose MySQL/SOAP to the internet. On a Linux host set `STACK_DATAPLANE_BIND` to the Docker bridge
gateway (e.g. `172.17.0.1`).

**Handled for you by default:** no raw Docker socket (allowlisted proxy; non-root manager with dropped
caps), deny-by-default auth with rate-limited credentials, signed client manifests + verified launcher
self-update, and encrypted secrets at rest. More in [DOCKER.md → Security Notes](./DOCKER.md#security-notes).

### Everyday commands

```bash
docker compose logs -f azeroth-platform    # watch the dashboard's logs
docker compose restart                      # restart everything
docker compose down                         # stop everything
docker compose up -d --build                # apply an update after `git pull`
```

---

## 4. Getting Started (first tour)

You've installed the platform and can log in. Here's the shortest path from an empty dashboard to a
server you can log into.

### Log in

Open **https://localhost/admin** (or your `SITE_ADDRESS`) and sign in with your `ADMIN_PASSWORD`. The
entire dashboard is the admin panel — players never see it.

### Dashboard layout: global vs. per-stack

There are **two levels** in the app, and understanding them prevents confusion later:

- **Global** (the top navigation bar) = platform-wide settings and content that are **pushed out to all
  stacks** (e.g. build the base launcher and send it to every stack; post news articles that are pushed
  to every stack and shown in every launcher).
- **Per-stack** (tabs inside a single server) = settings and content for that one server (e.g. its own
  launcher profile and its own news feed).

Think *"global applies to every stack; per-stack is just that one server."*

**Global (top bar):**

| Menu item | What it's for |
| --- | --- |
| **Stacks** | The list of all your servers ("My Stacks"). Your home base. |
| **Launcher** | Build & brand the base player launcher and **push it to all stacks**; verify propagation and re-send. |
| **Global News** | Post news articles that are **pushed to every stack** and shown in every launcher. |
| **Create Stack** | Start the wizard to build a new server. |

**Per-stack (open a stack, then its grouped tabs):**

| Group | Tabs |
| --- | --- |
| **Overview** | Status, health, and Start / Stop / Restart / DB Maintenance |
| **Client** | Accounts · Characters · Realms · Addons · Client · Armory · Launcher |
| **Game** | Modules · Patches · Lua Scripts |
| **News** | This stack's launcher news |
| **Server Config** | Edit the server's `.conf` files |
| **Advanced** | Environment Variables · Revisions · Logs |

### Create & start your first server

1. Click **Create Stack** and follow the 6-step wizard. Defaults are fine for a first server; just
   give it a name.
2. On the **Advanced** step pick where it runs — **Local** or **External**.
3. Finish. The **first build takes ~15–30 minutes** (it compiles the game server once); progress
   streams live.
4. Open the stack and press **Start** on the **Overview** tab.

### Create an account & connect a client

1. First, **initialize the SOAP account** by pressing the big button on the stack's **Overview** page.
   Account management uses SOAP, so this must be done once before you can create accounts.
2. Stack → **Accounts** tab → **Create Account** (username/password). Set **GM level** 3 to be an
   in-game admin.
3. Provide a WoW 3.3.5a client (**you supply the client files** — the platform doesn't ship them) and
   upload them from the stack → **Client** tab.
4. Build the shareable launcher from the top-bar **Launcher** page, or point players at your realmlist
   address directly, and log in.
5. Once built, the launcher is available for players to download via the armory's **How to connect** page or from the global **Launcher** page (admin only).

Everything else is explained in the [Feature Reference](#8-feature-reference).

---

## 5. Configuration Reference

### Environment variables (.env)

The root `.env` (copied from `.env.example`) configures the **platform/manager**:

| Variable | Purpose |
| --- | --- |
| `ADMIN_PASSWORD` | Dashboard login. Blank = a random one is generated and its location logged at startup. |
| `HOST_LAN_IP` | LAN IP used as the default realmlist host for **local** stacks. Blank = `127.0.0.1` (same machine only). External stacks default to the remote host. |
| `SITE_ADDRESS` | Public domain to serve. Blank = `https://localhost` with a self-signed certificate. |
| `TLS_EMAIL` | Contact email for the automatic Let's Encrypt certificate. |
| `STACK_PUBLISH_BIND` | Host interface the player-facing HTTP (armory + client file server) binds to. Default loopback. |
| `STACK_DATAPLANE_BIND` | Host interface MySQL + SOAP bind to (management only). Default loopback; set to the Docker bridge gateway on a Linux host. |

Game protocol ports (auth `3724`, world `8085`) are always published on all interfaces. The
Per-stack armory settings (`ACORE_ARMORY_*`) are documented in
[`frontend-armory/.env.example`](./frontend-armory/.env.example) and managed from each stack's
[Environment Variables](#configuration-configs--environment-variables) tab — a different scope from the
platform `.env`.

### Where data lives

There is no host `data/` directory to create. All manager state is in the `azeroth-platform-data`
named volume, and each server's runtime data is in per-stack named volumes seeded from it — for local
*and* external stacks. See [Data model](#data-model-named-volumes) and
[DOCKER.md → Volumes](./DOCKER.md#volumes).

---

## 6. Development

**Prerequisites:** .NET SDK 10.0.100+ (see `global.json`), Node.js 18+ and npm, Docker & Docker
Compose, Git.

**Backend:**

```bash
cd backend
dotnet restore
dotnet watch --project AzerothPlatform.Api        # hot reload (HTTP :5000, HTTPS :5001, Swagger /swagger)
dotnet test
# EF Core migration:
dotnet ef migrations add MigrationName \
  --project AzerothPlatform.Infrastructure \
  --startup-project AzerothPlatform.Api
```

**Frontend:**

```bash
cd frontend
npm install
npm run dev     # http://localhost:5173 with API proxy to the backend
npm run build
npm run lint
```

**Project structure:**

```
├── backend/
│   ├── AzerothPlatform.Api/              # Web API + Controllers + SignalR Hubs
│   ├── AzerothPlatform.Core/             # Domain contracts, DTOs, interfaces
│   └── AzerothPlatform.Infrastructure/   # Services, EF Core, Docker integration
├── frontend/                             # React admin dashboard
├── frontend-armory/                      # Character/3D-model-viewer armory served per stack
├── launcher/                             # Windows player launcher (compiled in a Docker sidecar)
├── wdbx/                                 # WDBX sidecar image (DBC editing)
├── client-example/                       # Reference layout for a distributable client
└── docker-compose.yml                    # Manager + socket proxy + Caddy
```

**Clean architecture:** `Api → Infrastructure → Core` and `Api → Core`. Api = HTTP/SignalR/DI, Core =
domain models/DTOs/interfaces (no dependencies), Infrastructure = service implementations, EF Core,
Docker.DotNet. See `.github/copilot-instructions.md` for coding standards.

---

## 7. Troubleshooting

- **Port already in use** — free `80`/`443`/`8080`, or change the published ports in
  `docker-compose.yml`.
- **Certificate warning on `https://localhost`** — expected with the self-signed cert; set
  `SITE_ADDRESS` + `TLS_EMAIL` for a trusted certificate.
- **Forgot/blank admin password** — check `docker compose logs azeroth-platform` for the generated
  one's location.
- **Manager crash-loops with `SQLite Error 8: attempt to write a readonly database`** — the
  `azeroth-platform-data` volume predates the non-root switch, so files are root-owned. Fix once (the
  manager runs as uid `1654`/`app`):
  ```bash
  docker run --rm -u 0 --entrypoint sh -v azeroth-platform-data:/data azeroth-platform:latest \
    -c 'chown -R app:app /data'
  docker restart azeroth-platform
  ```
- **Stack actions fail with Docker errors** — check the proxy: `docker compose ps docker-socket-proxy`
  and `docker compose logs docker-socket-proxy`. If an operation is rejected, the manager may need an
  API group that isn't enabled — add it to the `docker-socket-proxy` environment (keep the allowlist
  tight).
- **External stack won't start/build** — verify the remote host is reachable over SSH, the key/user
  are correct, and Docker is installed with the SSH user able to run it.
- **Players on other machines can't connect** — set `HOST_LAN_IP` (local stacks) and restart the
  stack; external stacks default their realmlist to the remote host.
- **Stack build fails** — usually out of disk (~5–10 GB per build), out of memory (~4 GB free during
  compilation), or a git-clone network hiccup. Check `docker logs azeroth-platform`.
- **Armory has no item icons / achievement titles** — run **Sync DBCs from server** (they come from
  DBC data, not the model-viewer download).
- **Armory 3D models don't load** — upload `armory.data.zip` (+ `armory.textures.zip`) from the
  [Armory release](https://github.com/Fero-Fero/AzerothPlatform/releases/tag/Armory).

---

## 8. Feature Reference

A plain-language breakdown of **every feature**, roughly in the order you'll use it. Each entry gives
**what it is**, **where to find it**, and **beginner** vs **advanced** usage. Several features exist
both **globally** (a top-bar default) and **per-stack** (an override for one server) — those are
flagged inline. For API endpoints and internals, see [DOCKER.md](./DOCKER.md).

### Your servers (stacks)

A **stack** is one complete game server: authentication + world + database, built and run for you.

#### Stacks overview ("My Stacks")
- **What:** the list of every server you've created, with live status (Running, Stopped, Building,
  Failed) and quick actions.
- **Where:** top bar → **Stacks**.
- **Beginner:** watch the status dots, press **Start** on a stopped stack, click a stack to open it.
  An "updates available" badge appears when new code exists.
- **Advanced:** start just the database ("Start DB") for maintenance; the list auto-refreshes while
  any stack is transitioning.

#### Create Stack wizard
- **What:** a guided 6-step form that builds a new server from source.
- **Where:** top bar → **Create Stack**.
- **Beginner:** name it and click through the defaults (type → database → ports → modules → advanced →
  review). First build ~15–30 minutes.
- **Advanced:** choose the Playerbots variant or a custom fork, select modules, pick the
  **Local/External** deployment target, set max players/realm name, add custom env vars. Validates
  port conflicts and name uniqueness; saves a browser draft.

#### Lifecycle: Start / Stop / Restart / DB Maintenance
- **What:** the power controls for a server.
- **Where:** open a stack → **Overview**.
- **Beginner:** Start/Stop/Restart run as background jobs, so you can navigate away — the button shows
  "Starting…/Stopping…" while it works.
- **Advanced:** **DB Maintenance** brings up *only* the database (world/auth stopped) for patches/SQL,
  from a stopped or running stack; the stack reports `Degraded` until fully started.

#### Edit Configuration, Rebuild & Delete
- **What:** structural changes to a stack.
- **Where:** open a stack → **Overview** actions.
- **Beginner:** **Edit Configuration** changes ports, passwords, max players, and realm name.
- **Advanced:** **Rebuild** forces a full rebuild (leaves the stack stopped, no snapshot); **Delete**
  removes the stack and all its data. Prefer **Update Stack** (below) for routine updates.

### Players: accounts & characters

#### Accounts
- **What:** the game login accounts (`acore_auth`), managed over SOAP.
- **Where:** stack → **Client** group → **Accounts**.
- **Beginner:** **Create Account** so someone can log in; set **GM level** 3 for an in-game admin.
- **Advanced:** ban/unban (duration + reason), reset passwords, delete accounts, see online status and
  character counts. Also hosts one-click **AH Bot** setup (creates the Auction House Bot account with
  Alliance & Horde characters). Requires the worldserver running (SOAP).

#### Characters
- **What:** every character and the GM tools to manage them.
- **Where:** stack → **Client** group → **Characters**.
- **Beginner:** find a player and kick, revive, or send a message.
- **Advanced:** ban/unban, mute/unmute, set level, rename, customize, send items/money, add items,
  view inventory.

#### Realms
- **What:** the realm entries clients see (name + connection address) from the `realmlist` table.
- **Where:** stack → **Client** group → **Realms**.
- **Beginner:** confirm your realm's name and address look right.
- **Advanced:** the address comes from `HOST_LAN_IP` (local) or the remote host (external); adjust it
  here if your network needs a different advertised address.

### Getting the game to players

#### Client
- **What:** the WoW 3.3.5a client files the launcher distributes. **You provide the client yourself.**
- **Where:** stack → **Client** group → **Client** (a shared global base client also exists).
- **Beginner:** upload your client's `game/` files; the launcher serves them and keeps them in sync.
- **Advanced:** `settings/` templates (`realmlist.wtf`/`Config.wtf` with `{{HOST}}`/`{{PORT}}`) are
  applied per launch; files under `Client:ManagedPrefixes` (default `Data/patch-`) are kept in sync
  and pruned when removed. See [`client-example/`](./client-example/).

#### Launcher — global & stack
- **What:** a branded Windows launcher you compile and hand to players; it downloads/updates the
  client, lets players pick a server profile, and only enables **Play** when files verify.
- **Where:**
  - **Global** — top bar → **Launcher**: app-wide identity/branding, the **Build launcher** button
    (which compiles the **base launcher and pushes it to every stack**), and a **propagation check**
    that pings all stacks to confirm they got the current build, with a per-stack **re-send**.
  - **Per-stack** — stack → **Client** group → **Launcher**: that server's profile (display name, sort
    order, realmlist override, its own background/logo), shown when **Show in launcher** is on.
- **Global vs. per-stack:** the global page builds and **distributes one base launcher to all stacks**
  and sets the shared branding; each opted-in stack then appears as a **profile** inside that launcher.
  New stacks show up automatically — no recompile needed.
- **Beginner:** set an app name/branding globally and press **Build launcher**; it's pushed to your
  stacks and you share the **Download exe** link.
- **Advanced:** per-profile overlays (custom MPQs + addons) layer on the shared base client so
  switching profiles never re-downloads the base; the launcher self-updates by verifying a published
  SHA-256 before replacing itself. See [DOCKER.md](./DOCKER.md#compiling-the-launcher-docker-sidecar).

#### News — global & stack
- **What:** the patch-notes/news feed shown inside the launcher.
- **Where:** **Global** — top bar → **Global News**; **Per-stack** — stack → **News** tab.
- **Global vs. per-stack:** saving **Global News pushes the articles to every launcher-visible stack**,
  so one set of news articles shows up in every launcher (re-push on demand at any time). A stack's
  **News** tab manages that one server's own articles for realm-specific news.
- **Beginner:** write a news article in **Global News** and save — it's pushed to all your stacks and
  shown in their launchers.
- **Advanced:** global news articles are pushed into each stack's own news store; per-stack news is
  delivered as patch-notes XML for that profile, so each realm can also publish its own articles.

#### Addons — global & stack
- **What:** WoW addons the launcher installs for players automatically (via the client manifest, under
  `Interface/AddOns/`).
- **Where:** stack → **Client** group → **Addons** (a global client has addons too).
- **Global vs. per-stack:** global addons ship with the shared base client for everyone; per-stack
  addons are added for that server's profile only.
- **Beginner:** upload an addon `.zip`; the launcher installs, updates, and removes it when you delete
  it here.
- **Advanced:** only launcher-installed files are pruned, so players' own addons are never touched;
  the enabled set is remembered per profile.

### Gameplay content & customization

#### Modules
- **What:** community add-ons that change gameplay (AH Bot, AutoBalance, Transmog, Playerbots, the
  Lua engine `mod-ale`, and any you add).
- **Where:** stack → **Game** group → **Modules** (the shared catalog; you *select* modules for a
  server during the wizard).
- **Beginner:** browse the catalog and open any module's **README**.
- **Advanced:** add your own modules from a **git repo** or an uploaded **`.zip`**; built-ins with
  dedicated parsers (AH Bot, AutoBalance, Playerbots, Transmog) get a guided settings UI, others fall
  back to generic `.conf`/env. Modules compile in at build time, so adding one to an existing server
  needs a rebuild.

#### Armory
- **What:** a per-stack character & guild website with equipment tooltips and a 3D model viewer.
- **Where:** stack → **Client** group → **Armory**.
- **Beginner:** works out of the box with a baseline dataset. For the full 3D viewer, download the
  assets from the
  **[Armory release](https://github.com/Fero-Fero/AzerothPlatform/releases/tag/Armory)** and
  upload `armory.data.zip` + `armory.textures.zip` in **Armory Assets** (they take effect immediately).
- **Advanced:** upload `armory.static.zip` to override the site's web assets (baked into the image →
  press **Rebuild armory image**); run **Sync DBCs from server** for rich item tooltips, achievement
  titles, and spell/talent icons (these come from DBC data, not the model download). The `Use ZAM CDN`
  toggle only affects the 3D viewer's asset source. See [DOCKER.md](./DOCKER.md#armory-image--assets).

#### Lua Scripts
- **What:** Lua scripts for custom behavior, powered by the [AzerothCore Lua Engine (`mod-ale`)](https://github.com/azerothcore/mod-ale).
- **Where:** stack → **Game** group → **Lua Scripts** (live scripts), or a patch's **`lua/`** folder (versioned with patch apply).
- **Beginner:** upload a `.zip` of scripts or edit `.lua` files inline, then **Apply & reload**. Patch
  `lua/` files are copied into the same live folder when you **Apply** that patch.
- **Advanced:** scripts run only if a **Lua engine** is compiled into the worldserver — add
  `mod-ale` from the catalog, select it for the stack, and rebuild; the tab warns when no Lua
  engine is detected. Re-applying patches redeploys each patch's `lua/` tree in order (later files
  overwrite earlier ones).

#### Patches
- **What:** incremental content layered onto a server over time — SQL, DBC edits, map overrides, client
  MPQ patches, **config overrides**, and **Lua scripts** — applied strictly in order.
- **Where:** stack → **Game** group → **Patches**.
- **Beginner:** create a numbered patch folder (e.g. `patch 1.1 my_patch`), drop files into its
  `sql/`, `dbc/`, `map/`, `mpq/`, `config/`, and/or `lua/` sections, and **Apply**.
- **Advanced:** organize files into one-level "containers", capture a cumulative DBC baseline from the
  server with **Init Baseline** (DBC editing needs the WDBX sidecar image), and rely on per-file SQL
  transactions and OpenTelemetry-traced applies. **`config/*.json`** files map to live server `.conf`
  files (`worldserver.json` → `worldserver.conf`, `individualProgression.json` →
  `modules/individualProgression.conf`, and other module configs by base name). Use **Preview changes**
  on a patch to compare live config values with what apply will write. Servers with
  **mod-individual-progression** can **Sync with mod-individual-progression** to seed patches from
  [Azeroth-Platform-Progression](https://github.com/Fero-Fero/Azeroth-Platform-Progression) — see that
  repository's README for the reference patch layout. See
  [DOCKER.md → Stack Migrations / Patches](./DOCKER.md#stack-migrations--patches).

### Configuration (Configs & Environment Variables)

#### Server Config (Configs)
- **What:** direct editing of the real config files — `worldserver.conf`, `authserver.conf`, and each
  installed module's `.conf`.
- **Where:** stack → **Server Config** tab.
- **Beginner:** start the stack once (files are created on first start), edit a setting, then **Apply &
  restart**.
- **Advanced:** module configs are auto-materialized from their `.conf.dist` so they're editable; use
  this for bulk changes and the guided **Modules** UI or **Environment Variables** for one-off
  overrides. `.conf.dist` reference files are hidden.

#### Environment Variables
- **What:** container environment variables that override server/module behavior.
- **Where:** **Per-stack** — stack → **Advanced** group → **Environment Variables** (searchable,
  per-container). **Global** — the root `.env` configures the *manager* itself.
- **Global vs. per-stack:** the root `.env` configures the **platform**; the tab configures a **single
  server's containers** — different scopes, not duplicates.
- **Beginner:** search for a variable, set its value, **Save** (applied on the next restart).
- **Advanced:** edit per container (worldserver, authserver, database, armory, …); the worldserver
  bucket doubles as the legacy flat override the backend reads.

### Keeping it running

#### Outdated & Update Stack
- **What:** detection of new AzerothCore/module commits, and a safe one-click update.
- **Where:** stack → **Overview** ("Updates Available"; **Check for Updates** forces a check — it also
  runs hourly).
- **Beginner:** click **Update Stack** — it stops the server, updates the code, rebuilds, and reboots;
  the notice clears when done.
- **Advanced:** Update snapshots a `pre-update` **revision** first, then reapplies every applied
  **patch**'s SQL in order after the core update so it can't clobber your custom SQL. Plain **Rebuild**
  does not snapshot or reapply patches.

#### Revisions
- **What:** point-in-time snapshots of a server's three databases plus its config files — your undo
  button.
- **Where:** stack → **Advanced** group → **Revisions**.
- **Beginner:** **Create snapshot** before risky changes; **Restore** rolls the databases and config
  back.
- **Advanced:** one is captured automatically before every **Update** (`pre-update`); restore
  drops/recreates the databases from dumps and restores the `.conf` files. Snapshots capture
  data + config, not the multi-GB client volume — restoring old *code* still needs a rebuild at the SHA
  recorded in the revision.

#### Logs
- **What:** live container logs for debugging.
- **Where:** stack → **Advanced** group → **Logs** (pick a container).
- **Beginner:** open the worldserver log to watch it boot or see errors in real time.
- **Advanced:** each container has its own streaming page with filter/search/auto-scroll (the stack
  must be running); for the manager's own logs use `docker compose logs -f azeroth-platform`.

---

## 9. Contributing

Contributions are welcome. Follow .NET and React best practices, reuse existing patterns (Clean
Architecture, React Query), write Conventional-Commits messages, update docs when changing
APIs/features, and see `.github/copilot-instructions.md` for coding standards.

When reporting bugs, include your OS and Docker version, the manager version (image tag), reproduction
steps, relevant `docker logs azeroth-platform` output, and screenshots if applicable.

---

## 10. Credits

> **🙏 Azeroth Platform stands on the shoulders of giants.**
>
> This project would not exist without the groundwork, tools, and open-source generosity of the
> people and projects below. Thank you.

### Groundwork this project is built on

- **[Witte1985 — AzerothCore Manager](https://github.com/Witte1985/AzerothCoreManager)** — Azeroth
  Platform grew directly out of Witte's AzerothCore Manager. It provided the original architecture and
  feature foundation (the guided wizard, Docker stack orchestration, build pipeline, account/character
  management, and more) that everything here builds upon. **Huge thanks, Witte — this is your
  groundwork extended.**
- **[r-o-b-o-t-o — AzerothCore Armory](https://github.com/r-o-b-o-t-o/azerothcore-armory)** — the
  entire armory feature is built on r-o-b-o-t-o's (Axel Cocat) azerothcore-armory. It laid the
  groundwork for the per-stack character & guild pages, equipment tooltips, and the 3D model viewer
  that Azeroth Platform serves. **Thank you for the armory groundwork.** (Used under its MIT license —
  see [`frontend-armory/LICENSE`](./frontend-armory/LICENSE).)

### AzerothCore & its community

- **[AzerothCore and all of its contributors](https://github.com/azerothcore/azerothcore-wotlk)** —
  the open-source WotLK server emulator at the heart of everything Azeroth Platform does. Thank you to
  the entire AzerothCore team and **every single contributor** who keeps it alive.
- **[AzerothCore Playermap](https://github.com/azerothcore/playermap)** — AzerothCore's live player
  world-map project. Thanks to AzerothCore and its maintainers (originally by Dmitry Koterov,
  maintained by Helias).
- **MaNGOS, TrinityCore & SunwellCore** — the foundational code AzerothCore itself builds upon.

### Tools & integrations

- **[WowDevTools — WDBX Editor](https://github.com/WowDevTools/WDBXEditor)** — the communal
  DBC/DB2/WDB editor that powers Azeroth Platform's DBC patching (the WDBX sidecar image). Thank you to
  **WowDevTools** and everyone who contributes to it (and to Ladislav Zezula for StormLib and the
  WoWDev wiki community behind it).

### …and everyone else

- The **Docker** community, the **WoW private-server** community, and **anyone we may have
  forgotten.** If your work belongs here and isn't listed, please open an issue or PR — you deserve
  the credit. 💙

---

## 11. License & disclaimer

This project is licensed under the **MIT** license — see the [`LICENSE`](./LICENSE) file.

This project is not affiliated with or endorsed by Blizzard Entertainment or World of Warcraft. It is
intended for educational purposes and private server testing only; the authors do not support or
sponsor illegal public servers.
