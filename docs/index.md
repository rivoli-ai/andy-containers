# Andy Containers

Development container management platform for the Andy ecosystem.

## What is Andy Containers?

Andy Containers provisions, manages, and orchestrates isolated development environments across heterogeneous infrastructure. Developers and AI agents work in reproducible, secure environments with full toolchain support, IDE access, web terminal, VNC desktop, and git integration.

## Key Features

| Feature | Description |
|---------|-------------|
| **Container Lifecycle** | Create from templates, start/stop/destroy, live resize CPU/RAM |
| **12 Templates** | Including 4 VNC desktop variants (dotnet-8-desktop, dotnet-8-alpine-desktop, dotnet-10-alpine-desktop, python-3.12-desktop) |
| **10 Code Assistants** | Claude Code, Aider, OpenCode, Codex CLI, Continue, Qwen Coder, Gemini Code, GitHub Copilot, Amazon Q, Cline |
| **Web Terminal** | xterm.js + tmux session persistence, 18 themes with per-container persistence |
| **VNC Desktop** | XFCE4 + TigerVNC + noVNC with HTTPS via self-signed certs, embedded iframe in UI |
| **Multi-Provider API Keys** | Fallback chain: primary, OpenRouter, OpenAI, OpenAI-compatible, custom |
| **Container Stats** | CPU/RAM/Disk monitoring with configurable polling |
| **Container Screenshots** | Background worker captures tmux terminal content for thumbnails |
| **Git Integration** | Multiple repo cloning, credential management, clone status tracking |
| **Non-Root Containers** | Containers run as non-root user derived from authenticated user's JWT claims |
| **Image Build Tracking** | Track custom image build status, trigger rebuilds from UI |
| **CLI Tool** | Full container management from the terminal with OAuth device flow auth |
| **MCP Tools** | Model Context Protocol tools for AI assistant integration (Claude Desktop, Cursor) |
| **Auth & RBAC** | OAuth 2.0/OIDC via Andy Auth, per-endpoint RBAC permissions via Andy RBAC |
| **Organizations & Teams** | Multi-tenant support with hierarchical template scoping |

## Quick Links

- [Getting Started](getting-started.md) -- Set up your development environment
- [Docker Setup](docker-setup.md) -- Ports, volumes, and certificate configuration
- [Architecture](ARCHITECTURE.md) -- System design, domain model, and background workers
- [Security](SECURITY.md) -- Authentication, authorization, and certificates
- [CLI Reference](cli-reference.md) -- Command-line tool usage
- [API Reference](api-reference.md) -- REST API endpoints
- [Implementation](implementation.md) -- Implementation phases and details

## Services (docker compose up)

| Service | URL | Description |
|---------|-----|-------------|
| Frontend | http://localhost:4200 | Angular 18 web UI |
| API (HTTPS) | https://localhost:5200 | REST / MCP API |
| API (HTTP) | http://localhost:5201 | HTTP access |
| PostgreSQL | localhost:5434 | Database (postgres:16-alpine) |
| Andy Auth | https://localhost:5001 | OAuth 2.0 / OIDC server |
| Andy RBAC API | https://localhost:7003 | RBAC permission server |
| Andy RBAC Web | https://localhost:5180 | RBAC admin UI |

## Background Workers

| Worker | Description |
|--------|-------------|
| ContainerProvisioningWorker | Channel-based queue for container creation and setup |
| ContainerStatusSyncWorker | Periodic sync of container state with Docker (15s default) |
| ProviderHealthCheckWorker | Periodic health checks on infrastructure providers (60s default) |
| ContainerScreenshotWorker | Captures tmux terminal content for thumbnails (30s default) |
| ImageBuildWorker | Tracks and manages custom image build processes |

## Documentation Site

This documentation is built with [MkDocs Material](https://squidfunk.github.io/mkdocs-material/) and deployed via GitHub Actions (`docs.yml` workflow). To enable GitHub Pages for this repository, go to Settings > Pages > Source and select "GitHub Actions".
