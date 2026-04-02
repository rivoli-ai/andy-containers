# Andy Containers

Development container management platform for the Andy ecosystem.

> **ALPHA RELEASE WARNING**
>
> This software is in ALPHA stage. **NO GUARANTEES** are made about its functionality, stability, or safety.
>
> **CRITICAL WARNINGS:**
> - Container management is **NOT FULLY TESTED** and may have security vulnerabilities
> - Infrastructure provider integrations are **EXPERIMENTAL**
> - **DO NOT USE** in production environments
> - The authors assume **NO RESPONSIBILITY** for data loss, infrastructure costs, or security breaches
>
> **USE AT YOUR OWN RISK**

## Features

- **Container Lifecycle** - Create from templates, start/stop/destroy, live resize CPU/RAM
- **12 Templates** - Including 4 VNC desktop variants (dotnet-8-desktop, dotnet-8-alpine-desktop, dotnet-10-alpine-desktop, python-3.12-desktop)
- **10 Code Assistants** - Claude Code, Aider, OpenCode, Codex CLI, Continue, Qwen Coder, Gemini Code, GitHub Copilot, Amazon Q, Cline with model/base URL configuration
- **Web Terminal** - xterm.js + tmux session persistence, 18 themes with per-container persistence, WebGL rendering
- **VNC Desktop** - XFCE4 + TigerVNC + noVNC with HTTPS via self-signed certs, embedded iframe in UI
- **Multi-Provider API Keys** - Fallback chain: primary, OpenRouter, OpenAI, OpenAI-compatible, custom
- **Container Stats** - Real-time CPU/RAM/Disk monitoring with configurable polling
- **Container Screenshots** - Background worker captures tmux terminal content for thumbnails
- **Git Integration** - Multiple repo cloning, credential management, clone status tracking
- **Non-Root Containers** - Containers run as non-root user derived from authenticated user's JWT claims
- **Image Build Tracking** - Track custom image build status, trigger rebuilds from UI
- **Organizations & Teams** - Multi-tenant support with hierarchical template scoping
- **Multi-Infrastructure** - Docker, Apple Containers, Azure (ACI/ACA/ACP), SSH, and more
- **Template Catalog** - Hierarchical catalog scoped by global, organization, team, or user
- **CLI Tool** - Full container management from the terminal with OAuth Device Flow auth
- **MCP Support** - Model Context Protocol tools for AI assistants (Claude Desktop, Cursor)
- **Auth & RBAC** - OAuth 2.0/OIDC via Andy Auth, per-endpoint RBAC permissions via Andy RBAC
- **Live Resource Monitoring** - Real-time CPU, RAM, and disk usage with configurable polling intervals
- **Dynamic Resource Resize** - Adjust CPU and memory on running containers without restart

## Quick Start

### Prerequisites

- Docker Desktop (for PostgreSQL and local container provider)
- .NET 8.0 SDK (for building the API)
- Node.js 20+ (for the Angular frontend)

### Docker Compose (Recommended)

```bash
git clone https://github.com/rivoli-ai/andy-containers.git
cd andy-containers
docker compose up --build
```

This starts all services:

| Service | URL | Description |
|---------|-----|-------------|
| Frontend | http://localhost:4200 | Angular 18 web UI |
| API (HTTPS) | https://localhost:5200 | REST / MCP API |
| API (HTTP) | http://localhost:5201 | HTTP access |
| PostgreSQL | localhost:5434 | Database (postgres:16-alpine) |
| Andy Auth | https://localhost:5001 | OAuth 2.0 / OIDC server |
| Andy RBAC API | https://localhost:7003 | RBAC permission server |
| Andy RBAC Web | https://localhost:5180 | RBAC admin UI |

The database schema is auto-created on first startup and seed data (providers, templates) is inserted automatically.

### Local Development

```bash
# 1. Start PostgreSQL
docker compose up -d postgres

# 2. Run the API server
dotnet run --project src/Andy.Containers.Api

# 3. Run the Angular frontend
cd src/andy-containers-web && npm install && npx ng serve
```

- API: **https://localhost:5200**
- Web UI: **http://localhost:4200**

## Project Structure

```
src/
├── Andy.Containers/              # Core library (models, abstractions)
├── Andy.Containers.Api/          # REST & MCP API server
├── Andy.Containers.Client/       # HTTP client library
├── Andy.Containers.Infrastructure/ # EF Core, repositories, infrastructure providers
├── Andy.Containers.Cli/          # Command-line interface
└── andy-containers-web/          # Web UI (Angular 18 SPA)

tests/
├── Andy.Containers.Tests/        # Core library tests
├── Andy.Containers.Api.Tests/    # API integration tests
└── Andy.Containers.Client.Tests/ # Client library tests

config/
├── templates/                    # YAML template definitions
│   └── global/                   # Global catalog templates
├── providers/                    # YAML provider definitions
└── workspaces/                   # YAML workspace definitions

images/                           # Container image Dockerfiles
docs/                             # Documentation (MkDocs Material)
```

## Background Workers

| Worker | Description |
|--------|-------------|
| ContainerProvisioningWorker | Channel-based queue for container creation and setup |
| ContainerStatusSyncWorker | Periodic sync of container state with Docker (15s default) |
| ProviderHealthCheckWorker | Periodic health checks on infrastructure providers (60s default) |
| ContainerScreenshotWorker | Captures tmux terminal content for thumbnails (30s default) |
| ImageBuildWorker | Tracks and manages custom image build processes |

## CLI

```bash
# Authentication (OAuth 2.0 Device Flow)
andy-containers auth login
andy-containers auth logout

# Container management
andy-containers list
andy-containers create --name <n> --template <code> [--provider <code>] [--code-assistant <tool>] [--model <model>]
andy-containers start <id>
andy-containers stop <id>
andy-containers destroy <id>
andy-containers exec <id> <command>
andy-containers ssh <id>
andy-containers info <id>
andy-containers stats <id>

# Template catalog
andy-containers templates list
andy-containers templates info <code>

# Provider management
andy-containers providers list
```

## RBAC Permissions

All endpoints are protected by `[RequirePermission]` attributes:

| Permission | Description |
|------------|-------------|
| container:read | List and view containers |
| container:write | Create containers, add repositories |
| container:delete | Destroy containers |
| container:exec | Start, stop, exec, resize, pull |
| template:read | Browse template catalog |
| template:write | Create, update, publish templates |
| template:delete | Delete templates |
| provider:read | List providers, health checks |
| provider:write | Register and delete providers |
| workspace:read | List and view workspaces |
| workspace:write | Create and update workspaces |
| workspace:delete | Delete workspaces |
| settings:read | List API keys and git credentials |
| settings:write | Manage API keys and git credentials |
| organization:read | View organization resources |
| organization:write | Manage organization resources |

## Testing

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test --settings coverlet.runsettings --collect:"XPlat Code Coverage"
```

## Technology Stack

- **Framework**: ASP.NET Core 8.0
- **Database**: PostgreSQL 16 with EF Core
- **APIs**: REST + MCP (Model Context Protocol)
- **UI**: Angular 18 (standalone components, Tailwind CSS)
- **CLI**: System.CommandLine + Spectre.Console
- **Docker**: Docker.DotNet + Docker-in-Docker
- **Auth**: JWT Bearer (Andy Auth OAuth 2.0 / OIDC)
- **AuthZ**: Andy RBAC with `[RequirePermission]` attributes
- **Observability**: OpenTelemetry (tracing + metrics), Serilog
- **Testing**: xUnit, FluentAssertions, Moq

## Documentation

Documentation is built with [MkDocs Material](https://squidfunk.github.io/mkdocs-material/) and deployed via GitHub Actions (`docs.yml` workflow).

To enable GitHub Pages, go to Settings > Pages > Source and select "GitHub Actions".

- [Architecture](docs/ARCHITECTURE.md) - System design, domain model, provider architecture
- [Security](docs/SECURITY.md) - Authentication, authorization, certificates
- [Getting Started](docs/getting-started.md) - Setup guide
- [API Reference](docs/api-reference.md) - REST API endpoints
- [CLI Reference](docs/cli-reference.md) - Command-line tool usage
- [Implementation](docs/implementation.md) - Implementation phases and details
- [YAML Configuration](docs/YAML-CONFIGURATION.md) - YAML-first configuration guide

## License

Apache 2.0

---

**Status:** Alpha
**Version:** 0.1.0-alpha
**Last Updated:** 2026-04-01
