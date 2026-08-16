# Azeroth Platform Launcher

A cross-platform desktop launcher (Avalonia, .NET 10) that installs and keeps a WoW 3.3.5a
client in sync with an Azeroth Platform backend, then launches the game.

## What it does

On start the launcher:

1. Fetches the manifest and config from the selected stack's client-server portal (`/manifest`, `/portal`).
2. Compares the manifest against the local install:
   - **First run:** downloads the full client.
   - **Later runs:** downloads only changed/added files; prunes managed files removed server-side.
3. Writes the pre-defined settings (`realmlist.wtf`, `Config.wtf`, ...) with the server's
   realmlist substituted in.
4. Enables **Play** only once the client is verified up to date, then starts `Wow.exe`.

The UI has three tabs:

- **Play** – server (profile) dropdown, per-profile branding/news, status, progress, and the
  Verify / Update / Play actions.
- **Addons** – enable/disable the selected profile's addons (moves cached folders in/out of
  `Interface/AddOns/` with no re-download).
- **Settings** – the server URL and install folder. Editing these and clicking **Save** reloads the
  profile list and switches back to Play.

## Multi-profile mode

When built from the manager's **Launcher** page, the launcher runs in multi-profile mode: it talks to
each stack's own portal and shows every published stack as a selectable profile. New stacks appear
automatically without rebuilding the launcher.

One WoW install (`C:/Program Files/{AppName}`) is shared across profiles. The base client downloads
once; each profile only overlays its custom MPQs and addons:

- **Standard MPQs** (`common.mpq`, `common-2.mpq`, `expansion.mpq`, `lichking.mpq`, `patch.mpq`,
  `patch-2.mpq`, `patch-3.mpq`) are shared and never moved/deleted.
- A profile's custom MPQs live in `Data/{profile}/` while inactive and `Data/` while active.
- On switch: delete `Cache/`, move the old profile's MPQs back to `Data/{old}/`, move the new
  profile's MPQs into `Data/`, write the realmlist, and swap enabled addons.
- **Do not edit overlay MPQs directly under `Data/`** while using the launcher. Each server profile
  also keeps a canonical copy under `Data/{stackId}/` (the stash). If you replace `Data/patch-L.MPQ`
  manually but an older copy remains in `Data/{stackId}/patch-L.MPQ`, MPQ tools and the client can
  appear to show the wrong (empty) archive until you run **Update** or delete the stash copy. Renaming
  the live file sidesteps the name clash — that is a symptom of duplicate stash/live copies.
- Addons are cached per profile under `_acl/addons/{profile}/` and toggled without re-downloading.

The launcher also self-updates: it compares its baked build version against
`/api/launcher-build/latest` and, when newer, downloads the exe, **verifies its published SHA-256**,
and only then swaps its own exe (Windows).

### Addons

Server-provided addons are delivered automatically. The manager serves them under
`game/Interface/AddOns/` as **managed** files, so the launcher installs and updates them in sync with
the client and removes any the server later drops. Players don't manage these in the launcher, and
their own manually-installed addons are left alone. Admins add/remove them from the manager's
**Addons** tab (per-stack).

### Per-stack client

The launcher always downloads from a stack's client-server container (`/manifest`, `/files/*`). There
is no global manager client root. Each stack publishes its own patched client through the
migration/Patches system. The stack's realm name becomes the launcher branding and its auth port the
realmlist port.

Downloads are resumable (HTTP range), run in parallel, and SHA-256 hashes are cached locally
(`%APPDATA%/AzerothPlatformLauncher/launcher-state.json`) so the full client is not rehashed each launch.

## Project layout

```
AzerothPlatform.Launcher/
├── Program.cs / App.axaml        # Avalonia bootstrap
├── Models/                       # Manifest, config, and persisted state (mirror backend DTOs)
├── Services/
│   ├── ManifestClient.cs         # backend HTTP client (config, manifest, file download w/ resume)
│   ├── HashService.cs            # SHA-256 with local cache
│   ├── SyncService.cs            # diff + parallel download + prune + progress
│   ├── SettingsWriter.cs         # writes realmlist.wtf / Config.wtf
│   ├── GameLauncher.cs           # starts the game executable
│   └── LauncherStateStore.cs     # loads/saves launcher-state.json
├── ViewModels/MainWindowViewModel.cs
└── Views/MainWindow.axaml        # status, progress, Verify/Update/Play, first-run setup
```

This is a **separate solution** (`AzerothPlatform.Launcher.sln`) from the backend so the
manager's Docker `dotnet restore` is unaffected.

## Develop

```bash
cd launcher
dotnet run --project AzerothPlatform.Launcher
```

On first launch, enter the server URL and choose an install folder. Use `http://localhost:8080` for a
local dev manager; for a real/public server use the **`https://`** URL fronted by the reverse proxy
(the launcher requires HTTPS for non-loopback hosts so client files and updates can't be tampered
with in transit). The install folder defaults to **`C:\Program Files\Azeroth Platform`** on Windows;
friends can change it in the Settings tab.

## Publish (single self-contained executable)

Windows (the platform the 3.3.5a client runs on natively):

```bash
dotnet publish AzerothPlatform.Launcher/AzerothPlatform.Launcher.csproj \
  -c Release -r win-x64 -p:PublishSingleFile=true \
  -o publish/win-x64
```

Passing `-p:PublishSingleFile=true` automatically makes the build **self-contained** (bundles the
.NET runtime), extracts native libraries from the single file, compresses it, and strips loose
symbol files — the result is one standalone `AzerothPlatformLauncher.exe` (~47 MB) that a friend can run
without installing .NET. This cross-compiles fine from macOS/Linux.

Other runtime identifiers: `linux-x64`, `osx-x64`, `osx-arm64` (the client itself would run via Wine).

## Distributing to friends

The goal is: a friend downloads the launcher, and with one click it installs the client and
lets them play — no manual realmlist edits, no client hunting.

1. **Pre-set the download URL.** Copy [`AzerothPlatform.Launcher/launcher.settings.example.json`](AzerothPlatform.Launcher/launcher.settings.example.json)
   to `launcher.settings.json` and set your public server URL and branding:

   ```json
   {
     "serverUrl": "https://play.myrealm.example",
     "stackId": "",
     "brandingTitle": "My Realm Launcher",
     "defaultInstallSubfolder": "MyRealm-WoW"
   }
   ```

   Set `stackId` to distribute a specific stack's patched client; leave it empty for the global client.

   The launcher reads `launcher.settings.json` from **next to the executable** at startup and
   uses it to pre-fill the Settings tab (server URL, branding, and the suggested install folder).
   By default the install folder is `C:\Program Files\Azeroth Platform`; set
   `defaultInstallSubfolder` to change the folder name under `C:\Program Files`, or
   `defaultInstallDirectory` to hard-code a full absolute path. Friends can still override
   everything in the Settings tab.

   During development this file is also copied from the project folder to the build output if present.

2. **Publish a self-contained single-file build** (see above), then place `launcher.settings.json`
   next to the produced executable.

3. **Zip and share** the executable + `launcher.settings.json`. Your friend unzips, runs the
   launcher, clicks **Save** (URL is already filled in) then **Play** once the download finishes.

The launcher's per-user state (server URL, install folder, last synced version, hash cache) is
stored in `%APPDATA%/AzerothPlatformLauncher/launcher-state.json`, separate from the shipped defaults.
