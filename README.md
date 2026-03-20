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

- **Multi-Infrastructure** - Provision containers on Docker, Apple Containers (macOS), Azure (ACI/ACA/ACP), Rivoli-managed, or third-party servers via SSH
- **Template Catalog** - Hierarchical catalog of container templates scoped by global, organization, team, or user
- **Declarative Dependencies** - Specify compilers, tools, and libraries in YAML; exact versions resolved and locked automatically
- **Content-Addressed Images** - Every built image has a unique hash; track exactly what changed between versions
- **Image Introspection** - Automatically detect installed tool versions, OS packages, and base image details after every build
- **Image Diffing** - Compare two images to see tool changes, severity classification, package deltas, and size changes
- **Multi-Repo Git Clone** - Clone multiple git repositories into containers with per-repo status tracking
- **Git Credential Management** - Securely store and resolve PATs and deploy keys for private repository cloning
- **Automatic Rebuilds** - New compiler/library versions trigger automatic image rebuilds per your update policy
- **Air-Gapped Builds** - Full support for internet-isolated environments with offline dependency caching
- **GPU Support** - Request GPU acceleration when available (NVIDIA, Azure GPU SKUs)
- **IDE Access** - VSCode (code-server) and/or Zed editor, accessible via browser
- **Workspace Management** - Group containers into workspaces tied to git repositories
- **Andy Auth Integration** - OAuth 2.0 / OIDC authentication via Andy Auth
- **Andy RBAC Integration** - Fine-grained permissions for all container operations
- **DevPilot Integration** - Spawn AI agents on containers with UI (noVNC) or headless
- **gRPC & REST APIs** - High-performance service-to-service and user-facing APIs
- **MCP Support** - Model Context Protocol tools for AI assistants (Claude Desktop, Cursor)
- **YAML-First Config** - All templates, providers, and dependencies defined in YAML files

## Quick Start

### Prerequisites

- .NET 8.0 SDK
- Docker Desktop (for PostgreSQL and local container provider)

### Local Development

```bash
# 1. Start PostgreSQL
docker-compose up -d postgres

# 2. Run the API server
cd src/Andy.Containers.Api
dotnet run
```

API runs at: **https://localhost:5200**

## Project Structure

```
src/
├── Andy.Containers/              # Core library (models, abstractions)
├── Andy.Containers.Api/          # REST, gRPC & MCP API server
├── Andy.Containers.Client/       # HTTP/gRPC client library
├── Andy.Containers.Infrastructure/ # EF Core, repositories, infrastructure providers
├── Andy.Containers.Web/          # Admin UI (Blazor)
└── Andy.Containers.Cli/          # Command-line interface

tests/
├── Andy.Containers.Tests/        # Core library tests
├── Andy.Containers.Api.Tests/    # API integration tests
└── Andy.Containers.Client.Tests/ # Client library tests

config/
├── templates/                    # YAML template definitions
│   └── global/                   # Global catalog templates
├── providers/                    # YAML provider definitions
└── workspaces/                   # YAML workspace definitions

proto/                            # gRPC protobuf definitions
openapi/                          # OpenAPI/Swagger specifications
docs/                             # Architecture and design docs
images/                           # Container image Dockerfiles
```

## Template Catalog

Templates are organized hierarchically with visibility scoping:

| Scope | Visibility | Example |
|-------|-----------|---------|
| Global | Everyone | `full-stack`, `agent-sandbox-ui` |
| Organization | Org members | `acme-backend` |
| Team | Team members | `team-alpha-workspace` |
| User | Private | `my-custom-env` |

### Built-In Templates

| Template | Toolchains | IDE | GPU |
|----------|-----------|-----|-----|
| `full-stack` | .NET 8, Python 3.12, Node 20, Angular 18 | VSCode | No |
| `full-stack-gpu` | Same as full-stack | VSCode | Yes |
| `dotnet-8-vscode` | .NET 8 SDK | VSCode | No |
| `python-3.12-vscode` | Python 3.12 | VSCode | No |
| `angular-18-vscode` | Node 20, Angular 18 | VSCode | No |
| `andy-cli-dev` | .NET 8, Andy CLI | VSCode | No |
| `agent-sandbox` | .NET 8, Python 3.12, git | None (headless) | No |
| `agent-sandbox-ui` | .NET 8, Python 3.12, git | Zed + noVNC | Optional |

## Dependency Tracking

Templates declare dependencies with version constraints. The build system resolves exact versions, locks them, and tracks changes:

```yaml
dependencies:
  - type: sdk
    name: dotnet-sdk
    version: "8.0.*"          # Any 8.0.x
    auto_update: true
    update_policy: patch      # Auto-rebuild on 8.0.3 -> 8.0.4
```

When upstream versions change, images are automatically rebuilt and a changelog is generated showing exactly what changed.

## Infrastructure Providers

| Provider | Type | Description |
|----------|------|-------------|
| Local Docker | `docker` | Docker Desktop or remote Docker host |
| Apple Containers | `apple-container` | Native macOS container runtime |
| Rivoli Managed | `rivoli` | Rivoli-managed container fleet |
| Third-Party SSH | `ssh` | Any Linux server via SSH |
| Azure ACI | `azure-aci` | Azure Container Instances (serverless) |
| Azure ACA | `azure-aca` | Azure Container Apps (auto-scaling) |
| Azure ACP | `azure-acp` | Azure Container Platform |

## API Endpoints

### Containers
- `POST /api/containers` - Create a container
- `GET /api/containers` - List containers
- `GET /api/containers/{id}` - Get container details
- `POST /api/containers/{id}/start` - Start container
- `POST /api/containers/{id}/stop` - Stop container
- `POST /api/containers/{id}/exec` - Execute command
- `GET /api/containers/{id}/connection` - Get IDE/VNC/SSH endpoints
- `DELETE /api/containers/{id}` - Destroy container
- `GET /api/containers/{id}/repositories` - List cloned git repositories
- `POST /api/containers/{id}/repositories` - Clone a new repository
- `POST /api/containers/{id}/repositories/{repoId}/pull` - Pull latest changes

### Workspaces
- `POST /api/workspaces` - Create workspace
- `GET /api/workspaces` - List workspaces
- `GET /api/workspaces/{id}` - Get workspace details
- `PUT /api/workspaces/{id}` - Update workspace
- `DELETE /api/workspaces/{id}` - Delete workspace

### Templates (Catalog)
- `GET /api/templates` - Browse catalog
- `POST /api/templates` - Create template
- `GET /api/templates/{id}` - Get template details
- `POST /api/templates/{id}/publish` - Publish to catalog

### Images
- `GET /api/images/{templateId}` - List built images
- `POST /api/images/{templateId}/build` - Trigger build
- `GET /api/images/{templateId}/latest` - Get latest image
- `GET /api/images/diff` - Compare two images
- `GET /api/images/{imageId}/manifest` - Get introspection manifest
- `GET /api/images/{imageId}/tools` - List installed tools
- `GET /api/images/{imageId}/packages` - List OS packages
- `POST /api/images/{imageId}/introspect` - Re-run introspection

### Git Credentials
- `POST /api/git-credentials` - Store a credential (PAT or deploy key)
- `GET /api/git-credentials` - List stored credentials (tokens never returned)
- `DELETE /api/git-credentials/{id}` - Delete a credential

### Providers
- `GET /api/providers` - List providers
- `POST /api/providers` - Register provider
- `GET /api/providers/{id}/health` - Check health

## CLI

```bash
# Container management
andy-containers create --template full-stack --name my-dev
andy-containers list
andy-containers start <id>
andy-containers stop <id>
andy-containers exec <id> "dotnet --version"
andy-containers destroy <id>

# Workspace management
andy-containers workspace create --name my-project --git https://github.com/me/repo.git
andy-containers workspace list

# Template catalog
andy-containers templates list
andy-containers templates create -f my-template.yaml

# Image management
andy-containers images list --template full-stack
andy-containers images build --template full-stack
andy-containers images diff <from-id> <to-id>

# YAML configuration
andy-containers config sync ./config/
andy-containers config validate ./config/
andy-containers config diff ./config/
```

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
- **APIs**: REST + gRPC + MCP
- **UI**: Blazor Server
- **CLI**: System.CommandLine + Spectre.Console
- **Docker**: Docker.DotNet
- **Azure**: Azure.ResourceManager SDK
- **SSH**: SSH.NET
- **Auth**: JWT Bearer (Andy Auth)
- **AuthZ**: Andy RBAC Client
- **Config**: YAML (source of truth) + Database (runtime)
- **Caching**: IMemoryCache
- **Testing**: xUnit, FluentAssertions, Moq

## Documentation

- [Architecture](docs/ARCHITECTURE.md) - System design, domain model, provider architecture
- [YAML Configuration](docs/YAML-CONFIGURATION.md) - YAML-first configuration guide
- [OpenAPI Spec](openapi/containers-api.yaml) - REST API specification
- [gRPC Proto](proto/containers.proto) - gRPC service definition

## License

Apache 2.0

---

**Status:** Alpha
**Version:** 0.1.0-alpha
**Last Updated:** 2026-03-20
