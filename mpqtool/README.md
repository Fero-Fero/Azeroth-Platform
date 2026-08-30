# MPQ packaging sidecar (`azerothcore-mpqtool`)

A tiny image that packs files into a WoW 3.3.5a-compatible MPQ archive using
[SMPQ](https://launchpad.net/smpq) (the StormLib-based `smpq` CLI). The platform uses it to build the
generated client `patch-D.MPQ` from the compiled DBC files during a patch apply.

It is deliberately separate from the heavy Wine/.NET [`wdbx`](../wdbx) image so that changing MPQ
tooling never triggers a WDBX rebuild. The backend builds this image on demand (build-if-missing,
then cached) via `IMigrationImageService`, so an apply only ever runs a ready image.

## Usage

```bash
# Build (normally done automatically by the platform):
docker build -t azerothcore-mpqtool:latest mpqtool/

# Pack /work/DBFilesClient/*.dbc into /work/patch-D.MPQ (files stored under DBFilesClient/):
docker run --rm -v "$PWD/work":/work azerothcore-mpqtool:latest "patch-D.MPQ" DBFilesClient
```

The archive is created as MPQ format **version 1** (`smpq -M 1`) because the 3.3.5a client cannot read
the newer default format.
