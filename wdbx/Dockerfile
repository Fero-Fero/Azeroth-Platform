# WDBXEditor sidecar image.
#
# WDBXEditor is a .NET Framework 4.8 WinForms app, but its CLI (-import / -export) is pure managed
# code. We build AND run it with Mono, natively on the host arch (no Wine, no x86 emulation). This
# matters on Apple Silicon, where running the Windows .NET Framework installer under an x86 emulator
# crashes ("illegal instruction"). The upstream code already anticipates Mono (see the
# Mono.Runtime guard in InstanceManager.FlashWindow).
#
# The Azeroth Platform invokes this image per patch apply for DBC imports only (MPQ packaging is a
# separate lightweight sidecar), e.g.:
#
#   docker run --rm -v <workdir>:/work azerothcore-wdbx:latest \
#     -import -f "Spell.dbc" -b 12340 -c "Spell.txt" -h true -u Update -i TakeNewest
#
# Build with the wdbx/ folder as the build context (it contains the vendored WDBXEditor source):
#
#   docker build -t azerothcore-wdbx:latest wdbx/

# ---------------------------------------------------------------------------
# Stage 1: build WDBX Editor.exe with Mono's msbuild
# ---------------------------------------------------------------------------
FROM mono:6.12 AS build

WORKDIR /src
COPY WDBXEditor/ ./

# Restore NuGet packages and build the editor in Release. If you cannot build from source,
# drop a prebuilt bin/Release next to this Dockerfile and skip this stage (see wdbx/README.md).
RUN nuget restore WDBXEditor.sln \
    && msbuild WDBXEditor/WDBXEditor.csproj /p:Configuration=Release /p:Platform=AnyCPU

# ---------------------------------------------------------------------------
# Stage 2: Mono runtime (native arch)
# ---------------------------------------------------------------------------
FROM mono:6.12 AS runtime

# The mono image already bundles libgdiplus (WinForms/System.Drawing). Its base (Debian buster) is
# EOL with archived apt repos, so we deliberately don't apt-get here.

# Copy the built editor, definitions and native StormLib (unused by -import, copied for parity).
COPY --from=build "/src/WDBXEditor/bin/Release/" /opt/wdbx/

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN sed -i 's/\r$//' /usr/local/bin/entrypoint.sh \
    && chmod +x /usr/local/bin/entrypoint.sh

WORKDIR /work
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
