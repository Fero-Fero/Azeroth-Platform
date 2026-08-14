# Lightweight MPQ packaging sidecar.
#
# Packs the compiled DBC files into a client patch MPQ (patch-D). We build a tiny tool (mkmpq)
# against a current StormLib built from source, instead of Debian's `smpq` (StormLib 9.22), which
# aborts with an assertion (`sizeof(hf->hctx) >= sizeof(hash_state)`) while generating the
# (attributes) MD5 table. mkmpq creates an MPQ v1 archive (what the 3.3.5a client reads) with a
# (listfile) and no (attributes).
#
# The Azeroth Platform invokes this image per patch apply:
#
#   docker run --rm -v <workdir>:/work azerothcore-mpqtool:latest "patch-D.MPQ" DBFilesClient
#
# which creates /work/patch-D.MPQ containing /work/DBFilesClient/*.dbc under DBFilesClient/.

# ---------------------------------------------------------------------------
# Stage 1: build StormLib + mkmpq
# ---------------------------------------------------------------------------
FROM debian:bookworm-slim AS build

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates zlib1g-dev libbz2-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
RUN git clone --depth 1 --branch v9.25 https://github.com/ladislav-zezula/StormLib.git
RUN cmake -S StormLib -B StormLib/build -DBUILD_SHARED_LIBS=OFF -DCMAKE_BUILD_TYPE=Release \
    && cmake --build StormLib/build -j"$(nproc)"

COPY mkmpq.cpp .
# Link the static StormLib plus zlib/bzip2 (StormLib compresses DBC sectors with zlib).
RUN g++ -O2 -std=c++17 -o mkmpq mkmpq.cpp -I StormLib/src \
        StormLib/build/libstorm.a -lz -lbz2 -lpthread

# ---------------------------------------------------------------------------
# Stage 2: runtime
# ---------------------------------------------------------------------------
FROM debian:bookworm-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends zlib1g libbz2-1.0 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /src/mkmpq /usr/local/bin/mkmpq
COPY create-patch-mpq.sh /usr/local/bin/create-patch-mpq.sh
RUN sed -i 's/\r$//' /usr/local/bin/create-patch-mpq.sh \
    && chmod +x /usr/local/bin/create-patch-mpq.sh

WORKDIR /work
ENTRYPOINT ["/usr/local/bin/create-patch-mpq.sh"]
