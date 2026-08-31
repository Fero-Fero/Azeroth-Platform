# WDBXEditor sidecar image

The migration system edits DBC files by importing CSV (`.txt`) data into `.dbc` files using
[WDBXEditor](WDBXEditor) (vendored here alongside the Dockerfile). WDBXEditor is a Windows-only
.NET Framework 4.8 WinForms app, so it runs here as a **Wine-based sidecar image** that the manager
invokes with `docker run` during a patch apply.

## Build

Build with the `wdbx/` folder as the build context (run from the repository root):

```bash
docker build -t azerothcore-wdbx:latest wdbx/
```

The default image name/tag is `azerothcore-wdbx:latest`, configurable via
`Migrations:WdbxImage` (env `Migrations__WdbxImage`).

This image bundles Wine + .NET Framework 4.8 and is large (~2-3 GB) and slow to build the first
time (winetricks downloads the .NET installer). It is only needed for DBC patching; SQL, map and
MPQ patches work without it.

## How the manager calls it

For each `dbc/*.txt` file in a patch, the manager:

1. Copies the cumulative baseline `server_dbc/<Name>.dbc` and the patch `<Name>.txt` (normalized
   to CRLF) into a temp work directory.
2. Runs:

   ```bash
   docker run --rm -v <workdir>:/work azerothcore-wdbx:latest \
     -import -f "<Name>.dbc" -b 12340 -c "<Name>.txt" -h true -u Update -i TakeNewest
   ```

   - `-u Update` = "Update Existing"
   - `-i TakeNewest` = colliding IDs keep the CSV row
   - `-b 12340` = WotLK 3.3.5a build

   `-import` overwrites `<Name>.dbc` in place.
3. Copies the updated `.dbc` back over `server_dbc/<Name>.dbc` (keeping the baseline cumulative)
   and into the running stack's `ac-client-data` volume under `dbc/`.

## Building from a prebuilt editor

If you cannot build WDBXEditor from source with Mono, build `WDBX Editor.exe` on Windows (or grab
a release), then replace the build stage: copy your `bin/Release` output into the image at
`/opt/wdbx/` (it must contain `WDBX Editor.exe`, the `Definitions/` folder, and `x64/StormLib.dll`).

## CSV format

WDBXEditor CSV import expects comma-delimited, double-quoted fields, a header row (`-h true`), and
**columns identical to the definition** for the target build. Rows must use CRLF line endings; the
manager normalizes `.txt` files to CRLF before import.
