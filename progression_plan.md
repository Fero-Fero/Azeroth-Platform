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

The MPQ directory currently supports uploading pre-built `.mpq` files, which should continue to work. In addition, there should be support for uploading raw content files and having the `.mpq` archive constructed automatically when a patch is applied.

### MPQ Manifest

Each MPQ directory should contain an `mpq.json` manifest with the following structure:

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

- **`add`** — Lists `.mpq` files to be constructed from the raw content within the MPQ directory.
- **`remove`** — Lists `.mpq` files to be deleted when the patch is applied.
- **`description`** — Provides a human-readable description for each added `.mpq` file.

### Construction Logic

When re-applying all patches, the system should resolve the final set of `.mpq` files before performing any construction.

**Example:** Given five applied patches (1.0, 1.1, 1.2, 1.3, 1.4):

| Patch | Adds         | Removes      |
|-------|-------------|-------------|
| 1.0   | patch-k.mpq | —           |
| 1.3   | patch-e.mpq | —           |
| 1.4   | patch-s.mpq | patch-k.mpq |

The final result should only construct `patch-e.mpq` and `patch-s.mpq` from their respective patch directories. Construction of `patch-k.mpq` is skipped entirely since it would be removed by a later patch. This avoids wasting time building archives that will be discarded.

**Pre-built files:** If a patch directory already contains a pre-built `.mpq` file (rather than raw content), skip construction and apply the file directly.
