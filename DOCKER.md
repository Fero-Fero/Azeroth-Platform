# Azeroth Platform - Docker Setup

This project can be run in Docker as a single container that includes both the frontend and backend.

> For install (local & external), getting started, and the full feature reference see the main
> **[README.md](./README.md)**. This document focuses on the container/volume internals.

## Quick Start

### 1. Configure Environment

```bash
# Copy the example and adjust as needed (optional — sensible defaults are provided):
cp .env.example .env
```

The manager stores all of its state in a Docker-managed named volume (`azeroth-platform-data`) and
pushes every per-stack artifact into per-stack named volumes on the same Docker daemon. There is no
host `data/` directory to create and no `HOST_DATA_PATH` to configure.

### 2. Build and run with Docker Compose

```bash
# Build and start the container
docker-compose up -d

# View logs
docker-compose logs -f

# Stop the container
docker-compose down
```

The application is served through the bundled **Caddy** reverse proxy (TLS + security headers) at
**https://localhost** (self-signed certificate by default — accept the warning, or set `SITE_ADDRESS`
+ `TLS_EMAIL` in `.env` for Let's Encrypt). The manager also listens on `http://localhost:8080` for
same-host access. Log in with the `ADMIN_PASSWORD` from your `.env`.

> **Compose is the supported deployment.** It wires up the Caddy proxy and the allowlisted
> `docker-socket-proxy` and runs the manager non-root — do not run the raw `docker run` command below
> against an untrusted network.

### 3. Build and run with Docker directly (development only)

```bash
# Build the image
docker build -t azeroth-platform:latest .

# Run the container bound to loopback (manager state lives in a Docker-managed named volume).
# NOTE: this dev shortcut mounts the raw Docker socket and skips the TLS proxy — use `docker compose`
# (which adds the socket proxy + Caddy + non-root user) for anything beyond local testing.
docker run -d \
  --name azeroth-platform \
  -p 127.0.0.1:8080:8080 \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v azeroth-platform-data:/app/data \
  azeroth-platform:latest
```

## Architecture

The Docker image uses a multi-stage build:

1. **Stage 1 (frontend-build)**: Builds the React frontend using Node.js
2. **Stage 2 (backend-build)**: Builds the .NET backend
3. **Stage 3 (runtime)**: Combines both into a minimal ASP.NET runtime image

The ASP.NET backend serves the frontend static files and provides the API.

### Stack container naming

Each stack's containers are named `acore-<stack-name-slug>-<stackId>-<service>` (e.g.
`acore-temp-server-43e700e4…-worldserver`) so they're easy to recognize in `docker ps`. The
Compose **project name** (and therefore named volumes, the network and all lifecycle commands)
stays id-only (`acore-<stackId>`) so renaming a stack never orphans its data volume. The stack name
slug is lowercased with non-alphanumeric characters collapsed to `-`; a stack with no usable name
falls back to the id-only prefix.

## Volumes

- `/var/run/docker.sock` - Mounted **read-only into the `docker-socket-proxy` service only** (not the
  manager). The manager reaches the daemon through the proxy over TCP, so it never holds the raw socket.
- `azeroth-platform-data` (mounted at `/app/data`) - Docker-managed named volume holding:
  - SQLite database (`azeroth-platform.db`)
  - Stack build directories (`stacks/`)
  - WoW client distribution (`client/`) served to the launcher (see below)

Per-stack runtime data (modules, config, logs, client base/overlay/cache, the `ac-client-data`
volume) lives in **per-stack named volumes** seeded from the manager's data volume. Nothing is
bind-mounted from a host filesystem path, so no `HOST_DATA_PATH` is required — the same model is used
for both local and external (remote-engine) stacks.

## Environment Variables

Configure the application via environment variables:

```yaml
environment:
  # ASP.NET Core
  - ASPNETCORE_ENVIRONMENT=Production
  
  # Paths
  - Docker__BuildsPath=/app/data/stacks
  - ConnectionStrings__DefaultConnection=Data Source=/app/data/azeroth-platform.db
  
  # Reach the daemon through the allowlisted socket proxy (not a raw socket)
  - DOCKER_HOST=tcp://docker-socket-proxy:2375        # docker CLI / compose
  - Docker__SocketPath=http://docker-socket-proxy:2375 # Docker.DotNet client
  - DOCKER_BUILDKIT=0                                  # classic builder (BuildKit isn't exposed by the proxy)

  # Docker Compose
  - Docker__ComposeCommand=plugin  # or "standalone" or "auto"

  # Admin login password (blank = generate + log one at startup)
  - Admin__Password=${ADMIN_PASSWORD:-}

  # Host interface stack ports publish on (loopback by default; see .env.example)
  - Docker__PublishBindAddress=${STACK_PUBLISH_BIND:-127.0.0.1}
  - Docker__DataPlaneBindAddress=${STACK_DATAPLANE_BIND:-127.0.0.1}

  # CORS (if needed)
  - Cors__AllowedOrigins__0=https://your-domain.com
```

### Docker-in-Docker (named volumes)

When the manager runs in a container it drives the host's Docker daemon through the allowlisted
`docker-socket-proxy` (over TCP on the internal network). It never bind-mounts host filesystem paths
into stack containers. Instead, per-stack data lives in
**named volumes** that the manager creates and seeds from its own data volume (`azeroth-platform-data`)
using short-lived helper containers — a volume-to-volume copy on the local daemon, or a `tar` stream
over SSH for external stacks. This removes the need for any host-path translation, so
`Docker__BuildsPath` is the only path setting required.

## WoW Client Distribution (Launcher)

The manager can serve a WoW 3.3.5a client to the desktop launcher. Drop your client
files under `data/client/` (mounted at `/app/data/client`, config key `Client:RootPath`):

```
data/client/
├── launcher.json     # optional overrides (branding, realmlist, executable, args)
├── game/             # files served 1:1 into the player's WoW install folder
└── settings/         # realmlist.wtf / Config.wtf templates (see client-example/README.md)
```

See [`client-example/`](client-example/) for a ready-to-copy layout and the settings
template naming convention.

Relevant environment variables:

```yaml
environment:
  - Client__RootPath=/app/data/client
  - Client__GameExecutable=Wow.exe
  - Client__ClientVersion=3.3.5a (12340)
  - Client__BrandingTitle=My AzerothCore Realm
  - Client__Realmlist__Host=play.myrealm.example   # public address in realmlist.wtf
  - Client__Realmlist__Port=3724
  # Files kept in sync + pruned when removed (defaults to Data/patch-):
  - Client__ManagedPrefixes__0=Data/patch-
```

The launcher consumes these endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/launcher/config` | Launcher config + rendered settings files |
| `GET /api/launcher/manifest` | File manifest (path, size, SHA-256, group) |
| `GET /api/launcher/files/{path}` | Download a file (HTTP range / resume supported) |
| `POST /api/launcher/rescan` | Rebuild the manifest after changing client files |

After adding or changing client files, restart the manager or call
`POST /api/launcher/rescan`. Hashes are cached by path + size + modified-time, so
unchanged multi-GB files are not re-hashed.

### Compiling the launcher (docker sidecar)

The **Launcher** page builds a distributable Windows launcher without a local toolchain: the manager
runs a docker sidecar that cross-publishes the launcher to a self-contained `win-x64` single-file
`.exe` with the website-configured identity baked into `launcher.settings.json`.

The launcher source is baked into the manager image (`COPY launcher/ /app/launcher-src/`). At build
time it is copied into the data volume (`/app/data/launcher-build/src`) and then seeded into a
throwaway `src` named volume; the sidecar publishes into an `out` named volume, and the produced
artifacts are fetched back to the manager (the same seed/run/fetch recipe used for external stacks,
just without a remote context). The sidecar runs, roughly:

```
docker run --rm -v {srcVolume}:/src -v {outVolume}:/out mcr.microsoft.com/dotnet/sdk:10.0 \
  sh -c "cd /src && dotnet publish AzerothPlatform.Launcher/AzerothPlatform.Launcher.csproj \
         -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -o /out"
```

The produced `AzerothPlatformLauncher.exe` and a `build.json` (version + timestamp + size) are stored in
`/app/data/launcher-dist/` and served by `GET /api/launcher-build/download`.

`Launcher:*` options:

```yaml
environment:
  - Launcher__SdkImage=mcr.microsoft.com/dotnet/sdk:10.0
  - Launcher__SourcePath=/app/launcher-src
  - Launcher__WorkPath=/app/data/launcher-build
  - Launcher__DistPath=/app/data/launcher-dist
  - Launcher__ExecutableName=AzerothPlatformLauncher.exe
```

Launcher-distribution endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/launcher/profiles` | Aggregated multi-profile document (global branding + visible stacks) |
| `GET /api/launcher/assets/{background\|logo\|news}` | Global default branding assets |
| `GET /api/stacks/{id}/launcher/profile-asset/{background\|logo\|news}` | Per-profile branding assets |
| `GET/PUT /api/launcher-admin/config` | Global launcher config (website) |
| `POST /api/launcher-admin/assets/{kind}` | Upload global background/logo |
| `GET/PUT /api/launcher-admin/stacks/{id}/profile` | Per-stack profile config |
| `POST /api/launcher-build` · `GET /api/launcher-build/status` · `GET /api/launcher-build/download` | Compile / poll / download |

Global launcher config is stored at `/app/data/launcher/` (JSON + assets). Per-stack branding assets
are stored under each stack's `client/launcher-profile/`. The shared base client is served from the
global client root; per-stack roots stay overlay-only, so switching profiles in the launcher never
re-downloads base MPQs. Per-stack profile metadata (visibility, display name, sort order, realmlist
host override) lives in new `ManagedStacks` columns (`AddLauncherProfileColumns` migration).

## Armory image & assets

Each stack can run an **armory** (character/guild browser + 3D model viewer) built from
[`frontend-armory/`](frontend-armory/). The static web bundle and the small DBC/progression datasets
are baked into the per-stack armory image; the heavy multi-GB model-viewer data (`meta`, `mo3`,
`bone`, `textures`) is kept out of the image and served from the stack's asset volume so images stay
small. On disk all armory assets live under one unified `static/` tree
(`static/data/{meta,mo3,bone,textures,dbc,progression}`).

Assets are uploaded per stack from **Client → Armory → Armory Assets**:

- `armory.data.zip` + `armory.textures.zip` — the model-viewer dataset, served live (takes effect
  immediately). Download it from the
  [Armory release](https://github.com/Fero-Fero/AzerothPlatform/releases/tag/Armory).
- `armory.static.zip` — static web assets baked into the image; changing them sets a rebuild marker
  and requires a **Rebuild armory image** (the marker is cleared once `ArmoryImageService` rebuilds).
- **Sync DBCs from server** extracts DBCs from the running server, converts them to CSVs, bakes them
  into the image, and reloads — this is what powers item tooltips, achievement titles, and icons.

See [README.md → Armory](./README.md#armory).

## Stack Migrations / Patches

Each stack has an incremental patch system managed from the **Patches** tab, stored under
`data/stacks/{stackId}/migrations/{level_name}/` with `sql/{world,auth,characters}`, `dbc`,
`map`, and `mpq` sub-folders. A cumulative DBC baseline lives at
`data/stacks/{stackId}/server_dbc/` and per-stack launcher content at
`data/stacks/{stackId}/client/game/Data/`.

Applying a patch runs SQL against the databases, imports DBC CSV edits via the WDBX sidecar,
overrides maps and DBC in the stack's `ac-client-data` volume, and publishes MPQ files to the
per-stack launcher. Because the manager talks to Docker via the socket, patch helper containers work
against named volumes (seeded/fetched from the manager's data volume) rather than host bind mounts,
so no `HOST_DATA_PATH` is involved.

Each SQL file is applied inside a single transaction (`START TRANSACTION` … `COMMIT`) with the
mysql client left to abort on the first error, so a failing statement rolls back that whole file
instead of leaving it half-applied. (MySQL auto-commits DDL, so `CREATE/ALTER/DROP` statements
cannot be rolled back — this protects the DML that AzerothCore patch SQL relies on.)

You can start **only the database** of a stack (leaving world/auth stopped) for patching or
maintenance via `POST /api/stacks/{id}/start-database` (surfaced as **DB Maintenance** on the stack
details page and **Start DB** on the stack list). It brings up `ac-database` and stops
`ac-worldserver`/`ac-authserver`, so it works from a stopped *or* a running stack; the stack is then
reported as `Degraded` until fully started or stopped.

### Revisions (DB + config snapshots)

Each stack keeps point-in-time **revisions** under `data/stacks/{stackId}/revisions/{revisionId}/`
(`world.sql`, `auth.sql`, `characters.sql`, `conf/`, `metadata.json`), indexed in the
`StackRevisions` table. A revision captures a `mysqldump` of the three AzerothCore databases plus a
copy of the server `.conf` files and metadata (core SHA, applied patch level).

- A `pre-update` revision is created automatically at the start of every **Update**.
- Restore drops/recreates the databases from the dumps, pipes each dump back through the `mysql`
  client, and restores the `.conf` files. Snapshot/restore run inside the DB container via the Docker
  socket (`docker exec … mysqldump` / `mysql`), so no extra host-path translation is needed.

Update flow: the **Update** action snapshots first, rebuilds the images, then — if any migration
patches are applied — runs the standard AzerothCore updates and reapplies every applied patch's SQL
in order before rebooting the stack. Plain **Rebuild** does none of this and leaves the stack
stopped.

| Endpoint | Purpose |
|----------|---------|
| `GET /api/stacks/{id}/revisions` | List revisions (newest first) |
| `POST /api/stacks/{id}/revisions` | Create a manual snapshot |
| `POST /api/stacks/{id}/revisions/{revId}/restore` | Restore databases + config from a revision |
| `DELETE /api/stacks/{id}/revisions/{revId}` | Delete a revision and its dump files |

### WDBX sidecar image (DBC editing)

DBC patching runs WDBXEditor (Windows-only) inside a Wine image. Build it once:

```bash
docker build -t azerothcore-wdbx:latest wdbx/
```

Configured via the `Migrations` section (env overrides shown):

```yaml
environment:
  - Migrations__WdbxImage=azerothcore-wdbx:latest
  - Migrations__VolumeToolImage=alpine:3.20
  - Migrations__WoWBuild=12340
  - Migrations__RealmlistHost=play.myrealm.example   # advertised to per-stack launchers
```

See [`wdbx/README.md`](wdbx/README.md) for details. SQL, map, and MPQ patches work without it.

### Migration & per-stack launcher endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /api/stacks/{id}/migrations` | List patches (status, per-category counts, current level) |
| `GET /api/stacks/{id}/migrations/{patchKey}` | Detailed file listing for a patch |
| `POST /api/stacks/{id}/migrations` | Create a patch folder |
| `POST /api/stacks/{id}/migrations/init-baseline` | Capture `server_dbc` from the data volume |
| `POST /api/stacks/{id}/migrations/{patchKey}/apply` | Apply the next incremental patch |
| `POST /api/stacks/{id}/migrations/{patchKey}/files/{category}` | Upload files (multipart; optional parallel `paths` field places files in one-level containers for `dbc`/`map`/`sql/*`) |
| `GET`/`PUT /api/stacks/{id}/migrations/{patchKey}/dbc/{file}` | Read / save a DBC CSV (path may include a container) |
| `DELETE /api/stacks/{id}/migrations/{patchKey}/files/{category}/{file}` | Delete a file (file part may include a container, e.g. `sql/world/quests/foo.sql`) |
| `GET /api/stacks/{id}/launcher/{config,manifest}` | Per-stack launcher config / manifest |
| `GET /api/stacks/{id}/launcher/files/{path}` | Download a per-stack client file |

### Addon endpoints

Addons are served through the client manifest (stored under `game/Interface/AddOns/`, managed files).

| Endpoint | Purpose |
| --- | --- |
| `GET /api/addons` | List global addons |
| `POST /api/addons` | Upload an addon `.zip` (multipart `file`) to the global client |
| `DELETE /api/addons/{name}` | Delete a global addon and rescan |
| `GET`/`POST`/`DELETE /api/stacks/{id}/addons[/{name}]` | Same, for a stack's client |

### Module catalog endpoints

The catalog combines built-in modules (defined in code) with custom modules persisted in the
manager's SQLite database (`CatalogModules` table). Custom modules are either **git** (cloned into a
build's `azerothcore-wotlk/modules/{id}` at build time, like built-ins) or **package** (an uploaded
`.zip` stored under `data/custom-modules/{id}` and copied into the build). Package files persist on
the same data volume as the SQLite DB and builds.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/modules[?serverType=]` | Wizard list (built-in + custom, filtered by server type) |
| `GET /api/modules/catalog` | Full catalog including built-ins, for administration |
| `POST /api/modules` | Add a git module (JSON: id, name, description, repository, branch, requiresPlayerbots) |
| `POST /api/modules/upload` | Add a package module (multipart: id, name, description, requiresPlayerbots, file) |
| `POST /api/modules/{id}/package` | Replace the stored package of a package module (multipart `file`) |
| `GET /api/modules/{id}/readme` | Module README markdown (git: raw host; package: from the .zip) |
| `PUT /api/modules/{id}` | Update a custom module's metadata (built-ins rejected with 409) |
| `DELETE /api/modules/{id}` | Delete a custom module + its stored package (built-ins rejected with 409) |

### Lua script endpoints

Lua scripts live under `data/stacks/{stackId}/lua_scripts/` in the manager's data volume and are seeded
into a per-stack `lua_scripts` named volume that the worldserver mounts at
`/azerothcore/env/dist/bin/lua_scripts` (Eluna's default `ScriptPath`); edits are re-seeded when the
stack restarts (via **Apply**). They require an Eluna module compiled into the image to actually run.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/stacks/{id}/lua` | List the script tree (+ `elunaPresent` flag) |
| `GET /api/stacks/{id}/lua/content?path=` | Read a script file |
| `PUT /api/stacks/{id}/lua/content` | Create/overwrite a script (JSON: path, content) |
| `POST /api/stacks/{id}/lua/upload` | Upload a `.zip` (folder structure) or a single file (multipart `file`, optional `path`) |
| `DELETE /api/stacks/{id}/lua/content?path=` | Delete a file or folder |
| `POST /api/stacks/{id}/lua/apply` | Restart the worldserver to load scripts |

### Server config endpoints

The editable `.conf` files (worldserver.conf, authserver.conf, `modules/*.conf`) live in the per-stack
`etc` named volume that the servers mount at `/azerothcore/env/dist/etc/`. The container entrypoint
seeds them into the volume on first start; the manager mirrors that volume back to
`data/stacks/{stackId}/azerothcore-wotlk/env/dist/etc/` on demand so the configs can be listed/edited
(and re-seeds edits when the stack restarts). The list reports `generated:false` until the stack has
started once. `.conf.dist` references are not editable.

Installed modules ship a `modules/<module>.conf.dist` but the container does not create the effective
`<module>.conf`. On each list, any `modules/*.conf.dist` without a sibling `.conf` is materialized
(copied) so the module's config is present and editable. Each file carries a `category`
(`server` or `modules`, the latter for anything under `modules/`) so the UI can group them; the config
editor renders separate **Server** and **Modules** sections.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/stacks/{id}/config` | List editable `.conf` files (+ `generated` flag, per-file `category`) |
| `GET /api/stacks/{id}/config/content?path=` | Read a config file |
| `PUT /api/stacks/{id}/config/content` | Save a config file (JSON: path, content) |
| `POST /api/stacks/{id}/config/apply` | Force-recreate worldserver + authserver to apply changes |

Both editors apply via `IStackService.RestartServerProcessesAsync`, which regenerates `.env` +
`docker-compose.override.yml` and runs `docker compose up -d --force-recreate ac-worldserver ac-authserver`.

## Network

All managed AzerothCore stacks are created on the `azerothcore-network` Docker network, allowing them to communicate with each other.

## Security Notes

See **[README.md → Security hardening](./README.md#security-hardening)** for the full hardening
walkthrough. In short, the default compose deployment already:

### Docker socket proxy (no raw socket in the manager)

Only the `docker-socket-proxy` service mounts `/var/run/docker.sock` (read-only), exposing an
**allowlisted** subset of the Docker API over TCP. The manager talks to it via `DOCKER_HOST` /
`Docker__SocketPath`, so a compromise of the manager cannot use the unrestricted socket to escape to
the host. Dangerous API groups (swarm, secrets, configs, …) are denied.

### Non-root manager + TLS

The manager runs as the image's non-root `app` user with `cap_drop: ALL` and
`no-new-privileges`, and binds to `127.0.0.1` only. All external traffic goes through the **Caddy**
reverse proxy (TLS + HSTS + security headers). Set `SITE_ADDRESS` + `TLS_EMAIL` for Let's Encrypt.

### File permissions

State lives in the Docker-managed `azeroth-platform-data` volume, so there is no host data directory
to chown. Signing keys and encrypted secrets are stored there with `0600` permissions.

## Updating

```bash
# Pull latest code
git pull

# Rebuild and restart
docker-compose down
docker-compose build --no-cache
docker-compose up -d
```

## Troubleshooting

### Docker socket / proxy

The manager reaches Docker through the `docker-socket-proxy` service. If stack actions fail with
connection errors, check the proxy is up and healthy:

```bash
docker compose ps docker-socket-proxy
docker compose logs docker-socket-proxy

# The proxy must be able to read the host socket:
ls -la /var/run/docker.sock   # srw-rw---- 1 root docker
```

If an operation is rejected, the manager may need an API group that isn't enabled — add it to the
`docker-socket-proxy` environment in `docker-compose.yml` (keep the allowlist as tight as possible).

### Inspecting the manager's data volume

Stack data lives in the `azeroth-platform-data` named volume (and per-stack volumes). To inspect it:

```bash
# List the manager's state
docker run --rm -v azeroth-platform-data:/data alpine ls -la /data

# List a stack's build tree
docker run --rm -v azeroth-platform-data:/data alpine ls -la /data/stacks
```

### Port already in use

Change the port mapping in `docker-compose.yml`:
```yaml
ports:
  - "8081:8080"  # Use port 8081 on host instead
```

## Development vs Production

For **development**, run frontend and backend separately:
```bash
# Terminal 1: Backend
cd backend && dotnet watch --project AzerothPlatform.Api

# Terminal 2: Frontend
cd frontend && npm run dev
```

For **production**, use Docker as described above.
