# Azeroth Platform

[![CI](https://github.com/Fero-Fero/AzerothPlatform/actions/workflows/ci.yml/badge.svg)](https://github.com/Fero-Fero/AzerothPlatform/actions/workflows/ci.yml)

A web-based control panel for private World of Warcraft 3.3.5a (Wrath of the Lich King) servers
built on [AzerothCore](https://www.azerothcore.org/). It deploys, builds, configures, and operates
your servers in Docker, and it distributes a branded launcher and game client to players — all from
one dashboard.

You only need **Docker**. The .NET and Node toolchains run inside containers. No prior AzerothCore
or Docker experience is required to get started.

---

## What it does

Azeroth Platform turns the AzerothCore lifecycle — clone, compile, configure, patch, update,
monitor, and ship to players — into point-and-click operations.

- **Deploy servers** — a guided wizard builds isolated **stacks** (auth + world + database) from
  source. Run them on this machine or ship them to a remote Linux host over SSH. Cloud launch is
  available for DigitalOcean, Hetzner, Vultr, AWS, GCP, and Azure.
- **Operate them** — start, stop, restart, and maintain databases as background jobs. Watch live
  status, health, and container logs. Detect updates hourly and apply them with a snapshot-first
  rebuild that reapplies your patches.
- **Customize gameplay** — browse community modules, apply ordered patches (SQL, DBC, maps, MPQ,
  config, Lua), and host a per-stack armory with equipment tooltips and a 3D model viewer.
- **Reach players** — manage accounts and characters, serve a WoW client and addons, compile a
  branded Windows launcher with signed manifests, and publish news into that launcher.

The default deployment sits behind TLS (Caddy), never mounts the raw Docker socket, and encrypts
secrets at rest.

---

## Get started

```bash
git clone https://github.com/Fero-Fero/AzerothPlatform.git
cd AzerothPlatform
cp .env.example .env
docker compose up -d --build
```

Set `ADMIN_PASSWORD` in `.env` (or leave it blank and the platform generates one). Open
**https://localhost/admin**, accept the self-signed certificate, and create your first stack.

Full install (local and remote), first-tour walkthrough, configuration, and troubleshooting:
**[Technical README](./README.technical.md)**.

---

## Documentation

| Document | What's in it |
| --- | --- |
| **[Technical README](./README.technical.md)** | Features, architecture, install, getting started, configuration, development, troubleshooting |
| **[DOCKER.md](./DOCKER.md)** | Container layout, named volumes, and endpoint tables |

---

## License & disclaimer

MIT — see [`LICENSE`](./LICENSE).

This project is not affiliated with or endorsed by Blizzard Entertainment or World of Warcraft. It is
intended for educational purposes and private server testing only.

Azeroth Platform builds on [AzerothCore](https://github.com/azerothcore/azerothcore-wotlk),
[Witte1985's AzerothCore Manager](https://github.com/Witte1985/AzerothCoreManager), and
[r-o-b-o-t-o's azerothcore-armory](https://github.com/r-o-b-o-t-o/azerothcore-armory). Full credits
are in the [Technical README](./README.technical.md#10-credits).
