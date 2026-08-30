# Multi-stage build for Azeroth Platform
# Stage 1: Build frontend
FROM node:22-alpine AS frontend-build

WORKDIR /app/frontend

# Copy package files
COPY frontend/package*.json ./

# Install dependencies
RUN npm ci

# Copy frontend source
COPY frontend/ ./

# Build frontend for production
RUN npm run build

# Stage 2: Build backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build

WORKDIR /app/backend

# Copy project files and restore dependencies
COPY backend/*.sln ./
COPY backend/AzerothPlatform.Api/*.csproj ./AzerothPlatform.Api/
COPY backend/AzerothPlatform.Core/*.csproj ./AzerothPlatform.Core/
COPY backend/AzerothPlatform.Infrastructure/*.csproj ./AzerothPlatform.Infrastructure/
COPY backend/AzerothPlatform.Tests/*.csproj ./AzerothPlatform.Tests/
COPY backend/AzerothPlatform.ClientManifest/*.csproj ./AzerothPlatform.ClientManifest/
COPY backend/AzerothPlatform.ClientServer/*.csproj ./AzerothPlatform.ClientServer/

RUN dotnet restore

# Copy all source code
COPY backend/ ./

# Build and publish the application
RUN dotnet publish AzerothPlatform.Api/AzerothPlatform.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Stage 3: Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

# Install git, curl, docker with compose v2 plugin, and the ssh client (needed for external stacks:
# docker contexts that reach a remote engine over ssh://).
RUN apt-get update && \
    apt-get install -y git curl docker.io docker-compose-v2 openssh-client && \
    rm -rf /var/lib/apt/lists/*

# Install the docker buildx plugin. The patch pipeline cross-builds the WDBX sidecar for linux/amd64
# (x86 Wine) on Apple Silicon hosts, which the legacy builder can't do; buildx (BuildKit + QEMU) can.
RUN mkdir -p /usr/local/lib/docker/cli-plugins && \
    BUILDX_ARCH="$(dpkg --print-architecture)" && \
    curl -SL "https://github.com/docker/buildx/releases/download/v0.19.3/buildx-v0.19.3.linux-${BUILDX_ARCH}" \
        -o /usr/local/lib/docker/cli-plugins/docker-buildx && \
    chmod +x /usr/local/lib/docker/cli-plugins/docker-buildx

# Create data directory with appropriate permissions
# Use the existing non-root user from the base image
RUN mkdir -p /app/data && \
    chown -R app:app /app

# Copy backend from build stage
COPY --from=backend-build --chown=app:app /app/publish ./

# Copy frontend static files to wwwroot
COPY --from=frontend-build --chown=app:app /app/frontend/dist ./wwwroot

# Bake launcher source into the image so the launcher-build sidecar can cross-publish it.
COPY --chown=app:app launcher/ ./launcher-src/

# Bake the armory source so the backend can build the shared per-stack armory image on demand.
COPY --chown=app:app frontend-armory/ ./armory-src/

# Bake the backend source so the backend can build the shared per-stack client-server image on demand
# (the azeroth-platform-client file server; needs the ClientServer + ClientManifest + Core projects).
COPY --chown=app:app backend/ ./backend-src/

# Bake the MPQ + WDBX sidecar sources so the backend can build those images on demand (build-if-missing,
# then cached) for the patch-D DBC pipeline. The MPQ image is lightweight; WDBX is a heavy one-time build.
COPY --chown=app:app mpqtool/ ./mpqtool-src/
COPY --chown=app:app wdbx/ ./wdbx-src/

# Bake the module-check toolchain Dockerfile so the manager can build the compile-gate image on demand.
COPY --chown=app:app docker/module-check/ ./module-check-src/

# Bake the client settings templates (realmlist.wtf.tmpl, Config.wtf.tmpl) so each stack's
# client/settings/ can be seeded on scaffold and the launcher always receives a realmlist.wtf.
COPY --chown=app:app client-example/ ./client-example/

# Switch to non-root user
USER app

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/api/health || exit 1

# Start the application
ENTRYPOINT ["dotnet", "AzerothPlatform.Api.dll"]
