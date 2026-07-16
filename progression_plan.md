# Progression Plan

## Patches Tab Refactor

The Patches tab (Stack > Game > Patches) needs a refactor to improve how patch files are imported and managed.

### Manual Import (Existing)

The current approach — manually importing patch files — should remain supported for users who want full control over dynamic and custom worlds.

### Mod-Individual-Progression Sync

A new **Sync with mod-individual-progression** option should be added to the Patches tab. This option should only be visible when mod-individual-progression is installed on the server.

When enabled, patch files are automatically sourced from two locations:

1. The local `mod-individual-progression` module.
2. The remote repository [Azeroth-Platform-Progression](https://github.com/Fero-Fero/Azeroth-Platform-Progression), which follows the same directory structure as the manual import format.

#### Mapping File

A `mapping.json` file defines how SQL files from mod-individual-progression are mapped into the patch directory structure:

```json
{
    "mappings": [
        {
            "source": "mod-individual-progression/data/sql/world/base/*",
            "destination": "Classic/1.0 Start/sql/world/",
            "optional": false
        },
        {
            "source": "mod-individual-progression/optional/sql/world/zz_optional_ammo_stack_size.sql",
            "destination": "Classic/1.0 Start/sql/world/",
            "optional": true
        }
    ]
}
```

**Mapping rules:**

- **Wildcard sources** (`*`): Copy all files in the directory to the destination, preserving original filenames.
- **Specific file sources**: Move the individual file to the destination.

**Optional handling:**

- `"optional": false` — Always overwrite all files in the destination directory.
- `"optional": true` — Overwrite existing files, but if a file does not already exist in the destination, prompt the user to confirm whether they would like to add it. Persist the user's answer so they are not re-prompted on subsequent update pulls.

#### Optional Files Log

The sync process produces a log file on the stack that records the user's decisions for each optional file (accepted or ignored). When this log file exists:

- Display a **View Ignored Files** button on the Patches tab, allowing the admin to review which optional files were previously declined.
- Each ignored file in the list should have a **Re-prompt** action, enabling the admin to reconsider and include the file without triggering a full sync.

This ensures optional choices remain transparent and reversible at any time.

#### Update Workflow

An admin can request an update, which triggers the following background process:

1. Create a temporary working directory.
2. Fetch the latest version of `mod-individual-progression`.
3. Fetch the latest version of `Azeroth-Platform-Progression`.
4. Re-apply all mappings.
5. Clean up the temporary directory.
6. Recommend to the user that they re-apply all patches.

---

## MPQ Construction

The `mpq/` directory supports **two ways** to ship client MPQ changes:

| Approach | What you put in `mpq/` | What happens on apply |
|----------|------------------------|------------------------|
| **Pre-built archive** | A finished file such as `patch-k.mpq` | Published to clients unchanged |
| **Raw content** | The files that belong inside an MPQ (e.g. `Interface/…`) plus an `mpq.json` manifest | Platform builds the archive named in **`add`** from the raw files |

For each name in **`add`**, construction is skipped when a matching `.mpq` file already exists in the directory; otherwise the platform packs the raw content tree into a new archive with that name.

### MPQ Manifest

When using raw content, each MPQ directory should contain an `mpq.json` manifest with the following structure:

```json
{
    "add": [
        "patch-k.mpq"
    ],
    "remove": [
        "patch-w.mpq"
    ],
    "description": {
        "patch-k.mpq": "This is a new patch to do stuff"
    }
}
```

- **`add`** — `.mpq` file names this patch adds. Each entry is either a pre-built file already present in `mpq/`, or the name of an archive to build from raw content in that folder.
- **`remove`** — `.mpq` file names to delete from the client when the patch is applied.
- **`description`** — Optional human-readable notes for entries in **`add`** (shown in the Patches UI).

The `mpq.json` file is only a manifest — it is never included inside a constructed archive.

### Construction Logic

When re-applying all patches, the system should resolve the final set of `.mpq` files before performing any construction.

**Example:** Given five applied patches (1.0, 1.1, 1.2, 1.3, 1.4):

| Patch | Adds         | Removes      |
|-------|-------------|-------------|
| 1.0   | patch-k.mpq | —           |
| 1.3   | patch-e.mpq | —           |
| 1.4   | patch-s.mpq | patch-k.mpq |

The final result should only construct `patch-e.mpq` and `patch-s.mpq` from their respective patch directories. Construction of `patch-k.mpq` is skipped entirely since it would be removed by a later patch. This avoids wasting time building archives that will be discarded.

**Pre-built files:** If `mpq/` already contains a file whose name matches an entry in **`add`**, that archive is used as-is and construction is skipped for that name.

**Raw content:** If no matching `.mpq` exists, the platform builds one from the non-manifest files in `mpq/` (and subfolders), using the file name from **`add`**.

---

## Config Overrides

Each patch directory may contain a `config/` sub-directory holding JSON files that define server configuration overrides. When a patch is applied, the overrides are written to the corresponding `.conf` file in the stack's `etc/` directory before the server restarts.

### File Format

Each JSON file is named after the target config file. The file name determines which `.conf` file is updated:

- **Server configs:** `worldserver.json` → `etc/worldserver.conf`, `authserver.json` → `etc/authserver.conf`
- **Module configs:** `individualProgression.json` → `etc/modules/individualProgression.conf`, `mod_ahbot.json` → `etc/modules/mod_ahbot.conf`

Resolution order: the system first checks `etc/{name}.conf`, then `etc/modules/{name}.conf` (with a case-insensitive fallback).

Example `worldserver.json`:

```json
{
    "Rate.XP.Kill": "2",
    "Rate.XP.Quest": "3"
}
```

Example `individualProgression.json`:

```json
{
    "IndividualProgression.StartingProgression": "5",
    "IndividualProgression.ProgressionLimit": "5"
}
```

Each key corresponds to a config key in the `.conf` file. The key is always followed by an `=` sign in the conf file, potentially with surrounding whitespace (e.g. `Rate.XP.Kill         = 1`). The value in the JSON replaces the existing value.

### Apply Behaviour

- Config overrides are applied **after** SQL, DBC, maps, and MPQ stages but **before** the server restart, ensuring the updated config is read on the next startup.
- When re-applying all patches, config overrides from each patch are applied in order so later patches override earlier ones.
- If the target `.conf` file does not exist, the override is skipped with a log message.
- If a key does not exist in the `.conf` file, it is appended at the end.
