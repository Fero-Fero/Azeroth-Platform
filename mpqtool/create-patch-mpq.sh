#!/bin/sh
# Packs a directory into a WoW 3.3.5a-compatible MPQ, or extracts one.
#
# Usage:
#   create-patch-mpq.sh <output.mpq> [source-dir]
#   create-patch-mpq.sh extract <archive.mpq> <outdir>
set -eu

if [ "${1:-}" = "extract" ]; then
  ARCHIVE="${2:?archive.mpq required}"
  OUT="${3:?outdir required}"
  exec /usr/local/bin/exmpq "$ARCHIVE" "$OUT"
fi

OUT="${1:?output MPQ name required}"
SRC="${2:-DBFilesClient}"

cd /work

if [ ! -d "$SRC" ]; then
    echo "Source directory '/work/$SRC' does not exist." >&2
    exit 2
fi

if [ -z "$(find "$SRC" -type f 2>/dev/null | head -n 1)" ]; then
    echo "Source directory '/work/$SRC' is empty; nothing to package." >&2
    exit 3
fi

# Empty prefix packs files under their relative paths (used when stripping DBC from overlay MPQs).
if [ "${3:-}" = "--preserve-paths" ]; then
  mkmpq "$OUT" "$SRC" ""
else
  mkmpq "$OUT" "$SRC" "$SRC"
fi
