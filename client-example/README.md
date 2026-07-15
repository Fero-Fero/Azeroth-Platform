# Client distribution example

This directory shows the layout the manager expects for distributing a WoW 3.3.5a
client through the launcher. Copy its structure into your live client directory,
which defaults to `data/client/` (config key `Client:RootPath`, mounted into the
container at `/app/data/client`).

```
data/client/
├── launcher.json              # optional overrides for the launcher config
├── game/                      # files that map 1:1 into the player's WoW install folder
│   ├── Wow.exe
│   ├── Data/common.MPQ        # base client files (group: "base")
│   └── Data/patch-B.mpq       # custom content (group: "managed")
└── settings/
    └── WTF__Config.wtf.once.tmpl
```

## game/

Everything under `game/` is served to the launcher and written into the player's
WoW install folder at the same relative path. On first run the launcher downloads
all `base` files (the full client); afterwards it only downloads files whose hash
changed.

Files whose relative path starts with one of the configured `Client:ManagedPrefixes`
(default `Data/patch-`) are treated as **managed**: the launcher always keeps them in
sync and deletes them locally when you remove them here. Everything else is **base**
and is only re-verified during a full verify.

## settings/

Each `*.tmpl` file becomes a settings file the launcher writes into the client:

- `__` in the filename becomes a path separator.
- The trailing `.tmpl` is stripped.
- A `.once` marker (before `.tmpl`) means "write only if the file does not already
  exist" (so player-tweaked files like `Config.wtf` are not clobbered every launch).
- `{{HOST}}` and `{{PORT}}` are substituted with the configured realmlist values.

The 3.3.5a client reads its realm address from `WTF/Config.wtf`'s
`SET realmList "host:port"` line (the `Data/<locale>/realmlist.wtf` files are ignored),
so the realmlist — including the auth-server port — lives in `Config.wtf`.

`WTF/Config.wtf` is **merged**, not clobbered: every `SET key value` this template defines
wins (so editing the template here propagates to all clients on their next launch), while
keys only the player has — resolution, sound, gamma, ... — are preserved. The launcher's
editable realmlist takes final priority for the realmlist line. This is the source of
truth for the server's `Config.wtf` values; the `game/WTF/` folder is **never** distributed
(it holds per-player runtime state).

Examples:

| Template file | Destination | Behavior |
| --- | --- | --- |
| `WTF__Config.wtf.once.tmpl` | `WTF/Config.wtf` | merged every launch (server keys win, player keys kept) |

## Applying changes

After adding or changing files, either restart the manager or call
`POST /api/launcher/rescan` to regenerate the manifest. Hashes are cached by
path + size + modified-time, so unchanged multi-GB files are not re-hashed.
