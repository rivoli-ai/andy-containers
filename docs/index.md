# Andy Containers

Development container management platform for the Andy ecosystem.

## What is Andy Containers?

Andy Containers provisions, manages, and orchestrates isolated development environments across heterogeneous infrastructure. Developers and AI agents work in reproducible, secure environments with full toolchain support, IDE access, and git integration.

## Key Features

| Feature | Description |
|---------|-------------|
| **Multi-Infrastructure** | Docker, Apple Containers, Azure, AWS, GCP, Fly.io, Hetzner, DigitalOcean, Civo, SSH |
| **Template Catalog** | Hierarchical templates (Global/Org/Team/User) defined in YAML |
| **10 Code Assistants** | Claude Code, Aider, Open Code, Codex CLI, GitHub Copilot, Amazon Q, Cline, and more |
| **Web Terminal** | Browser-based terminal with 18 themes, tmux persistence, WebGL rendering |
| **Live Stats** | Real-time CPU, RAM, disk monitoring with configurable polling |
| **CLI Tool** | Full container management from the terminal with OAuth device flow auth |
| **MCP Tools** | 26+ tools for AI assistant integration (Claude Desktop, Cursor) |
| **Auth & RBAC** | OAuth 2.0/OIDC via Andy Auth, per-endpoint RBAC permissions |

## Quick Links

- [Getting Started](getting-started.md) — Set up your development environment
- [Architecture](ARCHITECTURE.md) — System design and domain model
- [Security](SECURITY.md) — Authentication, authorization, and certificates
- [CLI Reference](cli-reference.md) — Command-line tool usage
- [API Reference](api-reference.md) — REST API endpoints

## Services (docker compose up)

| Service | URL | Description |
|---------|-----|-------------|
| Frontend | http://localhost:4200 | Angular web UI |
| API (HTTPS) | https://localhost:5200 | REST/gRPC/MCP API |
| API (HTTP) | http://localhost:5201 | HTTP access |
| PostgreSQL | localhost:5434 | Database |
