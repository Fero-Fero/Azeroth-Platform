# Copilot Instructions

## Repository Overview

Azeroth Platform is a web-based management tool for private World of Warcraft servers based on [AzerothCore](https://www.azerothcore.org/). AzerothCore is an open-source MMORPG server emulator that recreates the World of Warcraft 3.3.5a (Wrath of the Lich King) experience.

### Project Purpose

This tool simplifies the deployment, configuration, and administration of AzerothCore servers running in Docker containers by providing:
- Guided setup wizard for deploying new AzerothCore servers
- Docker stack creation and management via Docker.DotNet
- Real-time build progress and log streaming via SignalR
- Container lifecycle control (start, stop, restart)
- Configuration management for server settings
- Module installation and management

## Development Environment

**CRITICAL: This project is developed on Fedora Linux 44 with .NET 10**

### Operating System
- **OS**: Fedora Linux 44 (NOT Ubuntu, macOS, or Windows)
- **Package Manager**: `dnf` (NOT apt, brew, or chocolatey)
- **Python Command**: `python` (NOT `python3`)

### .NET Version
- **.NET SDK**: 10.0.100 (specified in `global.json`)
- **Target Framework**: `net10.0` (NOT net8.0 or net9.0)
- **All projects** target .NET 10 - do not reference .NET 8 or 9

### Common Mistakes to Avoid
- ❌ DO NOT try to install homebrew on Fedora
- ❌ DO NOT use `apt` or `apt-get` (use `dnf` instead)
- ❌ DO NOT reference .NET 8 in docs, configs, or code
- ❌ DO NOT use `python3` command (use `python`)
- ❌ DO NOT suggest Ubuntu/Debian-specific commands

### Installing Dependencies on Fedora

```bash
# .NET SDK
sudo dnf install dotnet-sdk-10.0

# Docker
sudo dnf install docker docker-compose
sudo systemctl enable --now docker
sudo usermod -aG docker $USER

# Node.js (for frontend)
sudo dnf install nodejs npm
```

## Build, Test, and Lint Commands

### Backend (.NET)

```bash
# Restore dependencies
cd backend
dotnet restore

# Build solution
dotnet build

# Run API (dev mode with hot reload)
dotnet watch --project AzerothPlatform.Api

# Run API (production mode)
dotnet run --project AzerothPlatform.Api

# Run tests (when implemented)
dotnet test

# Create EF Core migration
dotnet ef migrations add MigrationName \
  --project AzerothPlatform.Infrastructure \
  --startup-project AzerothPlatform.Api

# Update database
dotnet ef database update \
  --project AzerothPlatform.Infrastructure \
  --startup-project AzerothPlatform.Api
```

The API runs on:
- HTTP: http://localhost:5000
- HTTPS: https://localhost:5001
- Swagger: http://localhost:5000/swagger

### Frontend (React)

```bash
cd frontend

# Install dependencies
npm install

# Run dev server (with hot reload)
npm run dev

# Build for production
npm run build

# Preview production build
npm run preview

# Lint
npm run lint
```

The frontend dev server runs on http://localhost:5173

### Running Both Simultaneously

Open two terminals:

```bash
# Terminal 1: Backend
cd backend && dotnet watch --project AzerothPlatform.Api

# Terminal 2: Frontend
cd frontend && npm run dev
```

## High-Level Architecture

### Technology Stack

**Frontend:**
- React 19 with TypeScript
- Vite (build tool)
- TailwindCSS v4 (styling)
- React Router (routing)
- React Query (server state)
- SignalR client (real-time updates)
- React Hook Form + Zod (form validation)
- Axios (HTTP client)

**Backend:**
- ASP.NET Core 10 Web API
- Entity Framework Core (ORM)
- SQLite (manager metadata database)
- Docker.DotNet (Docker API client)
- SignalR (real-time communication)
- Serilog (structured logging)

**Infrastructure:**
- Docker & Docker Compose
- MySQL 8.4 (for AzerothCore databases)
- Allowlisted docker-socket-proxy (manager talks to Docker over TCP; no raw socket mount)
- Caddy reverse proxy (TLS + HSTS + security headers)

### Project Structure

```
backend/
├── AzerothPlatform.Api/          # Web API + Controllers + Hubs
├── AzerothPlatform.Core/         # Contracts, interfaces, DTOs
└── AzerothPlatform.Infrastructure/ # Services, EF Core, Docker integration

frontend/
├── src/
│   ├── components/  # Reusable UI components
│   ├── pages/       # Page components
│   ├── services/    # API client, SignalR
│   ├── hooks/       # Custom React hooks
│   ├── types/       # TypeScript definitions
│   ├── schemas/     # Zod validation schemas
│   └── lib/         # Utilities
```

### Clean Architecture Layers

- **Api**: HTTP endpoints, SignalR hubs, dependency injection
- **Core**: Domain models, DTOs, service interfaces (no dependencies)
- **Infrastructure**: Service implementations, EF Core context, Docker.DotNet integration

Dependencies: `Api → Infrastructure → Core` and `Api → Core`

### How It Works

1. **Frontend wizard** collects stack configuration (name, database, ports, modules)
2. **API validates** configuration (port conflicts, name uniqueness)
3. **BuildService** (to be implemented):
   - Clones AzerothCore repository
   - Generates docker-compose.yml from config
   - Applies module selections
   - Builds Docker images
   - Streams progress via SignalR
4. **Docker.DotNet** manages container lifecycle (start, stop, logs)
5. **SignalR** provides real-time updates to frontend

## Key Conventions

### Backend (.NET)

- **Service Registration**: Use extension methods in `DependencyInjection.cs` (e.g., `AddInfrastructure()`)
- **Async/Await**: All I/O operations are async with CancellationToken support
- **Nullable Reference Types**: Enabled in all projects (`<Nullable>enable</Nullable>`)
- **Minimal APIs**: NO - use controller-based APIs with proper attributes
- **DTOs**: All API contracts in `Core.Contracts` namespace, never expose entities
- **Service Interfaces**: Defined in `Core.Services.Interfaces`, implemented in `Infrastructure.Services`
- **Logging**: Use Serilog structured logging, not Console.WriteLine
- **Configuration**: Bind options objects in DI, don't inject IConfiguration directly

### Frontend (React)

- **Components**: Functional components with hooks (no class components)
- **Styling**: TailwindCSS utility classes (avoid inline styles)
- **Type Safety**: All components use TypeScript, mirror backend DTOs in `types/`
- **State Management**: React Query for server state, Context API sparingly for UI state
- **Forms**: React Hook Form + Zod validation schemas
- **API Calls**: Always through `apiClient` in `services/api.ts`
- **Real-time**: SignalR connections in custom hooks (e.g., `useBuildProgress`)
- **File Naming**: PascalCase for components, camelCase for hooks/utils
- **Imports**: Use `@/` path alias for clean imports

### Git and Workflow

- Commit messages follow conventional commits format
- Keep PRs focused and small
- Update documentation when changing APIs or architecture
- Run linters before committing

## Context About AzerothCore

When working on this project, understand:

- **AzerothCore** is a C++ MMORPG server emulator for WoW 3.3.5a
- It uses a **modular architecture** allowing extensions via custom modules
- It requires **MySQL 8.4** for three databases: auth, world, characters
- Configuration via **.conf files** (worldserver.conf, authserver.conf)
- Docker deployment uses official AzerothCore images
- **SOAP interface** on port 7878 allows remote admin commands
- **Server binaries**: authserver (port 3724), worldserver (port 8085)
- Community **module catalogue** at https://www.azerothcore.org/catalogue.html

### AzerothCore Docker Stack

A typical stack includes:
- `ac-authserver` - Authentication server (port 3724)
- `ac-worldserver` - Game server (port 8085, SOAP 7878)
- `ac-database` - MySQL 8.4 (port 3306)
- `ac-db-import` - One-shot DB initialization
- `ac-client-data-init` - One-shot client data download

## Current Implementation Status

### ✅ Implemented
- Project structure with clean architecture
- All projects migrated to .NET 10
- Basic API endpoints (health, stacks CRUD, validation, modules)
- EF Core database with SQLite
- Frontend wizard UI (6 steps: server, database, ports, modules, advanced, review)
- Draft persistence to localStorage
- Stack list and details pages
- SignalR hubs and client hooks (not wired up yet)

### 🚧 In Progress
- **Build orchestration** - BuildService is a scaffold, needs full implementation
- **Docker lifecycle** - Start/stop/restart endpoints exist but not implemented
- **SignalR streaming** - Hubs exist but don't emit events yet
- **Frontend integration** - Wizard submits but doesn't trigger builds

### 📋 Not Started
- Container log streaming
- SOAP command proxy
- Advanced configuration editing
- Backup and restore
- Testing (unit, integration, E2E)

See `plan.md` in the session folder for detailed implementation plan.

## Special Files

- `global.json` - Pins .NET SDK to version 10.0.100
- `.github/workflows/copilot-setup-steps.yml` - Cloud agent environment setup
- `.github/agents/` - Custom agent specifications
- `ARCHITECTURE_ANALYSIS.md` - Detailed design and feasibility analysis
- `backend/AzerothPlatform.Api/appsettings.json` - API configuration
- `frontend/vite.config.ts` - Frontend build and proxy config

## Troubleshooting

### Port Already in Use
```bash
# Kill process on port 5000 (backend)
lsof -ti:5000 | xargs kill -9

# Kill process on port 5173 (frontend)
lsof -ti:5173 | xargs kill -9
```

### Docker Socket Permission Denied
```bash
sudo usermod -aG docker $USER
# Log out and back in
```

### Database Locked (SQLite)
```bash
rm backend/AzerothPlatform.Api/azeroth-platform.db*
# Will be recreated on next run
```

### .NET SDK Not Found
```bash
dotnet --list-sdks
# If 10.0.100 not listed:
sudo dnf install dotnet-sdk-10.0
```
