#!/bin/sh
# Creates a WoW 3.3.5a-compatible MPQ from a directory of files, using the mkmpq tool (StormLib).
# Files are stored under the directory name as the internal path prefix (e.g. DBFilesClient/Spell.dbc
# -> DBFilesClient\Spell.dbc).
#
# Usage: create-patch-mpq.sh <output.mpq> [source-dir]
#   <output.mpq>  Name of the MPQ to create under /work (e.g. "patch-D.MPQ").
#   [source-dir]  Directory under /work whose files are packed (default: DBFilesClient).
set -eu

OUT="${1:?output MPQ name required}"
SRC="${2:-DBFilesClient}"

cd /work

if [ ! -d "$SRC" ]; then
    echo "Source directory '/work/$SRC' does not exist." >&2
    exit 2
fi

if [ -z "$(ls -A "$SRC" 2>/dev/null)" ]; then
    echo "Source directory '/work/$SRC' is empty; nothing to package." >&2
    exit 3
fi

# mkmpq stores files under the prefix (the source dir's base name), matching the client's
# DBFilesClient\ layout.
mkmpq "$OUT" "$SRC" "$SRC"
