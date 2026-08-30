# Self-contained WoW client file server (azeroth-platform-client).
# Built once by the manager (ClientServerImageService) and run as one container per stack. It scans a
# read-only shared base volume + a per-stack read-write overlay volume and serves its own SHA-256
# manifest and files (with range support), so the manager never has to serve client files itself.
#
# Build context is the backend source directory (baked into the manager image at /app/backend-src),
# so the ClientManifest + Core project references resolve.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AzerothPlatform.Core/ AzerothPlatform.Core/
COPY AzerothPlatform.ClientManifest/ AzerothPlatform.ClientManifest/
COPY AzerothPlatform.ClientServer/ AzerothPlatform.ClientServer/

RUN dotnet publish AzerothPlatform.ClientServer/AzerothPlatform.ClientServer.csproj \
    -c Release \
    -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /out ./

# The base (ro) + overlay (rw) client volumes and the writable cache are mounted at runtime.
ENV ASPNETCORE_URLS=http://+:8090 \
    ASPNETCORE_ENVIRONMENT=Production \
    CLIENT_BASE_ROOT=/client/base \
    CLIENT_OVERLAY_ROOT=/client/overlay \
    CLIENT_CACHE_DIR=/client/cache

EXPOSE 8090

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8090/health || exit 1

ENTRYPOINT ["dotnet", "AzerothPlatform.ClientServer.dll"]
