# Andy Containers -- Requirements

## 1. Overview

Andy Containers is a development container management platform for the rivoli-ai ecosystem. It provisions, manages, and orchestrates isolated development environments across heterogeneous infrastructure providers -- from local Docker and Apple Containers to cloud platforms (Azure, AWS, GCP) and bare-metal servers via SSH. Developers and AI agents get reproducible, secure environments with full toolchain support, IDE access, web terminals, code assistant integration, and git workflows, all governed by centralized authentication (Andy Auth) and role-based access control (Andy RBAC).

## 2. Goals

1. **Infrastructure Agnostic** -- Provide a single API that manages containers across Docker, Apple Containers, Azure (ACI/ACA/ACP), AWS Fargate, GCP Cloud Run, Fly.io, Hetzner, DigitalOcean, Civo, and third-party SSH hosts, with automatic provider health monitoring and routing.
2. **Catalog-Driven Reproducibility** -- Enable teams to define, version, and share container templates in a hierarchical catalog (Global / Organization / Team / User) with declarative YAML-first configuration and content-addressed image tracking.
3. **Agent-Native Development** -- First-class integration with AI code assistants (Claude Code, Codex CLI, Aider, and others) and Andy DevPilot for spawning AI agents on containers with headless or UI-attached modes.
4. **Security-First Operations** -- Gate all operations through Andy Auth (OAuth 2.0 / OIDC) and Andy RBAC (organization-scoped, per-endpoint permissions) with encrypted credential storage and zero-trust networking.
5. **Multi-Surface Access** -- Expose container management through REST API, gRPC, MCP tools, CLI, and Angular web UI so that humans, scripts, and AI assistants can all interact with the platform.
6. **Comprehensive Testing** -- Unit and integration tests for backend and frontend with sufficient code coverage to maintain confidence in production deployments.

## 3. Functional Requirements

### 3.1 Infrastructure Providers (Epic: Multi-Provider Support)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-01 | Support Docker as an infrastructure provider (Docker Desktop or remote Docker host) | Must |
| FR-02 | Support Apple Containers as a native macOS container runtime provider | Must |
| FR-03 | Support Rivoli-managed container fleet as a provider | Must |
| FR-04 | Support third-party Linux servers via SSH as a provider | Must |
| FR-05 | Support Azure Container Instances (ACI) as a serverless provider | Must |
| FR-06 | Support Azure Container Apps (ACA) as an auto-scaling provider | Must |
| FR-07 | Support Azure Container Platform (ACP) as a provider | Must |
| FR-08 | Support AWS Fargate as a provider | Should |
| FR-09 | Support GCP Cloud Run as a provider | Should |
| FR-10 | Support Fly.io as a provider | Should |
| FR-11 | Support Hetzner as a provider | Should |
| FR-12 | Support DigitalOcean as a provider | Should |
| FR-13 | Support Civo as a provider | Should |
| FR-14 | Periodic provider health monitoring with Healthy / Degraded / Unreachable statuses | Must |
| FR-15 | Auto-selection routing to choose the best available provider based on health, region, and capabilities | Should |
| FR-16 | Live resource resize (CPU and memory) on running Docker containers without restart | Must |
| FR-17 | GPU-aware provisioning: request GPU acceleration when the target provider supports it | Should |
| FR-18 | Organization-scoped provider registration (providers can be global or organization-owned) | Must |

### 3.2 Container Templates (Epic: Template Catalog)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-19 | Hierarchical template catalog with four scopes: Global, Organization, Team, User | Must |
| FR-20 | YAML-first template definitions with toolchains, IDE type, resources, and dependencies | Must |
| FR-21 | Dependency tracking with version constraints and automatic resolution (e.g., `dotnet-sdk: "8.0.*"`) | Must |
| FR-22 | Configurable auto-update policies per dependency (patch, minor, none) | Should |
| FR-23 | Automatic image rebuild when upstream dependency versions change, with changelog generation | Should |
| FR-24 | Resource defaults per template (CPU, memory, disk) | Must |
| FR-25 | Code assistant defaults per template (tool, API key env var, model name) | Must |
| FR-26 | Template versioning with semantic version strings | Must |
| FR-27 | Template publishing to the catalog with scope and visibility controls | Must |
| FR-28 | Built-in global templates: full-stack, full-stack-gpu, dotnet-8-vscode, python-3.12-vscode, angular-18-vscode, andy-cli-dev, dotnet-10-cli, dotnet-8-alpine, agent-sandbox, agent-sandbox-ui | Must |

### 3.3 Container Lifecycle (Epic: Container Management)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-29 | Create containers from templates with provider selection and resource configuration | Must |
| FR-30 | Channel-based provisioning queue with background worker processing | Must |
| FR-31 | Container lifecycle operations: start, stop, destroy | Must |
| FR-32 | Container status tracking: Pending, Creating, Running, Stopped, Failed, Destroyed | Must |
| FR-33 | Delay Running status until all provisioning steps complete (SSH setup, code assistant install, repo cloning) | Must |
| FR-34 | Post-create scripts defined in templates for distro-agnostic package installation (Alpine apk, Debian apt, RHEL yum/dnf) | Must |
| FR-35 | Automatic SSH setup during provisioning (key generation, authorized_keys configuration) | Must |
| FR-36 | Real-time container stats monitoring: CPU usage, memory usage, disk usage with configurable polling intervals | Must |
| FR-37 | Container uptime tracking with human-readable display | Must |
| FR-38 | Terminal thumbnails via server-side screenshot capture (ContainerScreenshotWorker) | Should |
| FR-39 | Container lifecycle event history (create, start, stop, error events) | Must |
| FR-40 | Container cost estimation (hourly and monthly) based on provider pricing | Should |
| FR-41 | Execute arbitrary commands in running containers | Must |
| FR-42 | Filter containers by owner, organization, team, status, template, and provider | Must |

### 3.4 Code Assistant Integration (Epic: AI Code Assistants)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-43 | Support 7 code assistant tools: Claude Code, Codex CLI, Aider, Open Code, Continue, Qwen Coder, Gemini Code | Must |
| FR-44 | Distro-agnostic installation scripts (Alpine, Debian, RHEL family) | Must |
| FR-45 | Model name selection per code assistant configuration | Must |
| FR-46 | Base URL configuration for custom API endpoints | Must |
| FR-47 | API key management: secure storage, per-provider validation, encrypted at rest | Must |
| FR-48 | Support OpenRouter as an API provider | Must |
| FR-49 | Support Ollama as a local model provider | Should |
| FR-50 | Support custom OpenAI-compatible API endpoints | Must |
| FR-51 | Auto-install code assistant during container provisioning based on template config | Must |
| FR-52 | Auto-start option for code assistants after installation | Should |

### 3.5 Git Repository Management (Epic: Git Integration)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-53 | Clone multiple git repositories into a single container | Must |
| FR-54 | Per-repository clone status tracking (Pending, Cloning, Cloned, Failed) | Must |
| FR-55 | Git credential management: store PATs and deploy keys with encrypted storage | Must |
| FR-56 | Credential resolution: automatically match stored credentials to repository URLs during clone | Must |
| FR-57 | Branch selection for repository cloning | Should |
| FR-58 | Clone depth configuration for shallow clones | Should |
| FR-59 | Submodule support during cloning | Should |
| FR-60 | Pull latest changes for cloned repositories | Must |
| FR-61 | Repository probing: detect default branch, available branches, and repository metadata before cloning | Should |
| FR-62 | Credentials never returned in API responses (write-only storage) | Must |

### 3.6 Web Terminal (Epic: Terminal Access)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-63 | Browser-based terminal using xterm.js with WebSocket transport | Must |
| FR-64 | tmux session persistence: reconnecting to a container reattaches to the existing tmux session | Must |
| FR-65 | Proper tmux resize on reattach to match the current browser viewport | Must |
| FR-66 | 18 color themes with per-container persistence (GitHub Dark, Dracula, Monokai, Solarized Dark, Nord, One Dark, Catppuccin Mocha, Gruvbox Dark, Ocean Blue, Deep Sea, Forest, Aurora, Midnight Purple, Cyberpunk, Solarized Light, GitHub Light, Catppuccin Latte, One Light) | Must |
| FR-67 | Font size controls (increase, decrease) with keyboard shortcuts (Ctrl+= / Ctrl+-) | Must |
| FR-68 | Fullscreen mode toggle (F11 / Esc) | Must |
| FR-69 | WebGL rendering via @xterm/addon-webgl for GPU-accelerated terminal display | Must |
| FR-70 | Web links addon: clickable URLs in terminal output | Should |
| FR-71 | Color reset button to restore theme defaults after in-terminal color escape sequences | Should |
| FR-72 | SSH native terminal launch via `ssh://` URL scheme or CLI `connect` command | Must |
| FR-73 | UTF-8 rendering support for international characters and emoji | Must |

### 3.7 API Surfaces (Epic: Platform APIs)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-74 | REST API with 60+ endpoints across containers, templates, images, providers, workspaces, git credentials, organizations, API keys, and terminal resources | Must |
| FR-75 | gRPC service for high-performance service-to-service communication (DevPilot integration) | Must |
| FR-76 | MCP (Model Context Protocol) server with 22 tools for AI assistant integration | Must |
| FR-77 | MCP tools cover: container management, template browsing, provider listing, workspace listing, image management (list, introspect, diff, tool search), git operations (clone, pull, list repos, credentials), organization summaries, image builds, API key management | Must |
| FR-78 | OpenAPI/Swagger specification for REST API documentation | Must |
| FR-79 | CLI tool with OAuth 2.0 Device Flow authentication (browser-based sign-in) | Must |
| FR-80 | CLI commands: auth (login, status, logout), list, create, info, start, stop, destroy, exec, stats, connect | Must |
| FR-81 | CLI output: formatted tables for TTY, JSON for pipes, colorized with Spectre.Console | Must |
| FR-82 | Client library (Andy.Containers.Client) for HTTP and gRPC access from .NET consumers | Must |

### 3.8 Authentication and Authorization (Epic: Security)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-83 | OAuth 2.0 / OpenID Connect authentication via Andy Auth server | Must |
| FR-84 | JWT Bearer token validation with configurable authority and audience | Must |
| FR-85 | Authorization Code flow with PKCE for the Angular SPA frontend | Must |
| FR-86 | Dev mode: permissive authorization when Andy Auth authority is not configured, with injected dev identity | Must |
| FR-87 | RBAC via Andy.Rbac.Client with per-endpoint permission checks | Must |
| FR-88 | Organization-scoped access control: JWT claims with RBAC API fallback and IMemoryCache caching | Must |
| FR-89 | Resource-scoped API key audience: `urn:andy-containers-api` | Must |
| FR-90 | Scopes: openid, profile, email, roles, urn:andy-containers-api, offline_access | Must |
| FR-91 | MCP tools respect the same authentication and authorization model | Must |

### 3.9 Web Frontend (Epic: Angular SPA)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-92 | Angular 18 SPA with standalone components | Must |
| FR-93 | Dark mode UI with Tailwind CSS | Must |
| FR-94 | Dashboard view with container overview and quick actions | Must |
| FR-95 | Container list view with status, uptime, provider, and template columns with filtering | Must |
| FR-96 | Container detail view with stats, events, repositories, and connection info | Must |
| FR-97 | Container create wizard with template selection, provider selection, resource configuration, code assistant configuration, and git repository configuration | Must |
| FR-98 | Template catalog browser with scope filtering | Must |
| FR-99 | Template detail view with toolchain, dependency, and resource information | Must |
| FR-100 | Provider list view with health status indicators | Must |
| FR-101 | Workspace management views (list, create, detail) | Must |
| FR-102 | Settings view for API key management and monitoring configuration | Must |
| FR-103 | Resource management: configure CPU, memory, and disk at create time | Must |
| FR-104 | Live resource resize from container detail view (Docker provider) | Must |
| FR-105 | Status badge component for consistent container status display | Must |
| FR-106 | Container stats bar component with visual CPU/RAM/disk indicators | Must |
| FR-107 | YAML editor component for template editing | Should |
| FR-108 | Container thumbnail component for terminal preview snapshots | Should |
| FR-109 | Uptime pipe for human-readable duration formatting | Must |

### 3.10 Image Management (Epic: Content-Addressed Images)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-110 | Content-addressed images: every built image has a unique hash for exact change tracking | Must |
| FR-111 | Dependency tracking: images track which template and dependency versions produced them | Must |
| FR-112 | Image introspection: automatically detect installed tool versions, OS packages, and base image details after every build | Must |
| FR-113 | Image diffing: compare two images to see tool changes, severity classification, package deltas, and size changes | Must |
| FR-114 | Air-gapped build support: offline dependency caching for internet-isolated environments | Should |
| FR-115 | Image listing per template with latest image retrieval | Must |
| FR-116 | Image manifest and tools API for programmatic inspection | Must |
| FR-117 | Organization-scoped image publishing and management | Must |
| FR-118 | Search images by installed tool name | Should |
| FR-119 | Re-run introspection on demand for existing images | Should |

## 4. Non-Functional Requirements

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-01 | Container creation latency | < 60 seconds from request to Running status (Docker provider) |
| NFR-02 | API response time | < 200ms for read operations, < 500ms for mutations (excluding provisioning) |
| NFR-03 | Terminal latency | < 100ms round-trip keystroke latency over WebSocket |
| NFR-04 | Concurrent containers | Support 100+ managed containers per deployment |
| NFR-05 | Concurrent API clients | Support 50+ concurrent API clients |
| NFR-06 | Provider failover | Detect provider health degradation within 60 seconds |
| NFR-07 | Container image size | < 500MB for base development container images |
| NFR-08 | Startup time | < 10 seconds from process start to healthy API endpoint |
| NFR-09 | Availability | 99.9% API availability during operational hours |
| NFR-10 | Security | No OWASP Top 10 vulnerabilities; encrypted credential storage; zero plaintext secrets in API responses |
| NFR-11 | Observability | OpenTelemetry distributed tracing and metrics with OTLP or console exporters |
| NFR-12 | API documentation | All REST endpoints documented in OpenAPI/Swagger; gRPC services documented in .proto files |
| NFR-13 | Deployment | Docker container, Railway-compatible, docker-compose for local development |
| NFR-14 | Database migrations | EF Core migrations with automatic schema creation on first startup |
| NFR-15 | Seed data | Automatic seeding of providers and global templates on startup |

## 5. Constraints

- **Must use .NET 8** -- matches andy-auth, andy-rbac, and other rivoli-ai services.
- **Must use PostgreSQL 16** -- relational storage for containers, templates, images, providers, and credentials via EF Core.
- **Must use Docker** -- primary local infrastructure provider and deployment target.
- **Must use Angular 18** -- standalone components with Tailwind CSS for the web frontend.
- **Must integrate with Andy Auth** -- no standalone authentication implementation; OAuth 2.0 / OIDC only.
- **Must integrate with Andy RBAC** -- no standalone permission system; organization-scoped RBAC via Andy.Rbac.Client.
- **Must follow clean architecture** -- Domain (Andy.Containers), Infrastructure (Andy.Containers.Infrastructure), API (Andy.Containers.Api), Client (Andy.Containers.Client) layers.
- **Must use YAML as source of truth** -- templates, providers, and workspaces defined in YAML files, synced to database at runtime.
- **Must use ModelContextProtocol NuGet package** -- same MCP library as other rivoli-ai services.

## 6. Traceability Matrix

| Requirement | Area | Feature(s) |
|-------------|------|------------|
| FR-01..FR-18 | Infrastructure Providers | Multi-provider support, health monitoring, auto-routing, live resize, GPU |
| FR-19..FR-28 | Container Templates | Hierarchical catalog, YAML definitions, dependency tracking, built-in templates |
| FR-29..FR-42 | Container Lifecycle | Create/start/stop/destroy, provisioning queue, stats, uptime, thumbnails, events |
| FR-43..FR-52 | Code Assistant Integration | 7 tools, distro-agnostic install, model/URL config, API key management |
| FR-53..FR-62 | Git Repository Management | Multi-repo clone, credential management, branch/depth/submodules |
| FR-63..FR-73 | Web Terminal | xterm.js + WebSocket, tmux persistence, 18 themes, font controls, WebGL, SSH launch |
| FR-74..FR-82 | API Surfaces | REST (60+ endpoints), gRPC, MCP (22 tools), CLI, client library |
| FR-83..FR-91 | Authentication and Authorization | OAuth 2.0/OIDC, PKCE, RBAC, org-scoped access, dev mode |
| FR-92..FR-109 | Web Frontend | Angular 18 SPA, dashboard, container views, template catalog, settings |
| FR-110..FR-119 | Image Management | Content-addressed images, introspection, diffing, air-gapped builds |
