# Andy Containers - Architecture

> Development Container Management Platform for the Andy Ecosystem

## 1. Vision

Andy Containers is a platform for provisioning, managing, and orchestrating isolated development environments ("containers" / "workspaces") across heterogeneous infrastructure. It enables developers and AI agents to work in reproducible, secure environments with full toolchain support (.NET, Python, Angular), IDE access (VSCode via code-server, Zed), and git integration — regardless of whether the container runs locally, on Rivoli-managed infrastructure, on third-party servers, or on cloud platforms like Azure.

### Core Principles

1. **Infrastructure Agnostic** — A single API manages containers across Docker, Apple Containers, Azure Container Instances, Azure Container Apps, Azure ACP, Rivoli-managed VPS, and third-party SSH hosts
2. **Catalog-Driven** — Container templates are organized in a hierarchical catalog (global, organization, team, user) enabling reuse and governance
3. **Security-First** — All operations gated by Andy Auth (authentication) and Andy RBAC (authorization)
4. **Agent-Native** — First-class integration with Andy-DevPilot for spawning AI agents on containers
5. **GPU-Aware** — Containers can request GPU acceleration when available on the target infrastructure

## 2. System Context

```
                                    ┌──────────────┐
                                    │  Andy Auth    │
                                    │  (OAuth/OIDC) │
                                    └──────┬───────┘
                                           │ JWT tokens
                                           ▼
┌─────────────┐   ┌─────────────┐   ┌─────────────────┐   ┌──────────────┐
│ Andy        │   │ Andy        │   │                   │   │ Andy RBAC    │
│ Containers  │──▶│ Containers  │──▶│  Andy Containers  │──▶│ (Permissions)│
│ Web UI      │   │ CLI         │   │  API Server       │   └──────────────┘
└─────────────┘   └─────────────┘   │  (REST/gRPC/MCP)  │
                                    └────────┬──────────┘
┌─────────────┐                              │
│ Andy        │──────────────────────────────▶│
│ DevPilot    │  (spawns agents on containers)│
└─────────────┘                              │
                                    ┌────────┴──────────┐
                                    │ Infrastructure     │
                                    │ Provider Layer     │
                                    ├────────────────────┤
                                    │ ┌────────────────┐ │
                                    │ │ Local Docker   │ │
                                    │ ├────────────────┤ │
                                    │ │ Apple          │ │
                                    │ │ Containers     │ │
                                    │ ├────────────────┤ │
                                    │ │ Rivoli Managed │ │
                                    │ ├────────────────┤ │
                                    │ │ Third-Party    │ │
                                    │ │ (SSH)          │ │
                                    │ ├────────────────┤ │
                                    │ │ Azure ACI      │ │
                                    │ ├────────────────┤ │
                                    │ │ Azure ACA      │ │
                                    │ ├────────────────┤ │
                                    │ │ Azure ACP      │ │
                                    │ └────────────────┘ │
                                    └────────────────────┘
```

## 3. Architecture Layers

### 3.1 Clean Architecture

```
Presentation Layer
├── REST Controllers (Andy.Containers.Api)
├── gRPC Services (Andy.Containers.Api)
├── MCP Tools (Andy.Containers.Api)
├── Blazor UI (Andy.Containers.Web)
└── CLI (Andy.Containers.Cli)
         │
Business Logic Layer
├── Container Orchestration Service
├── Container Provisioning Worker (Channel-based background queue)
├── Workspace Service
├── Template Catalog Service
├── Session Management Service
├── Infrastructure Routing Service
├── Health Monitoring Service
├── Image Manifest & Introspection Service
├── Image Diff Service
├── Git Clone Service
├── Git Credential Service
├── Organization Membership Service (JWT claims + RBAC API fallback)
└── Container Authorization Service
         │
Domain Layer (Andy.Containers)
├── Models (Container, Workspace, Template, etc.)
├── Abstractions (IInfrastructureProvider, IContainerService, etc.)
├── Configuration
└── Enums & Value Objects
         │
Infrastructure Layer (Andy.Containers.Infrastructure)
├── EF Core DbContext & Repositories
├── Infrastructure Providers
│   ├── DockerProvider (Docker Engine API)
│   ├── AppleContainerProvider (macOS native)
│   ├── RivoliProvider (Rivoli-managed fleet)
│   ├── SshProvider (third-party servers)
│   ├── AzureAciProvider (Azure Container Instances)
│   ├── AzureAcaProvider (Azure Container Apps)
│   └── AzureAcpProvider (Azure ACP)
└── External Service Clients (Auth, RBAC, DevPilot)
```

### 3.2 Project Dependencies

```
Andy.Containers.Web ──▶ Andy.Containers.Client
Andy.Containers.Cli ──▶ Andy.Containers.Client
Andy.Containers.Client ──▶ Andy.Containers (core)

Andy.Containers.Api ──▶ Andy.Containers (core)
Andy.Containers.Api ──▶ Andy.Containers.Infrastructure

Andy.Containers.Infrastructure ──▶ Andy.Containers (core)
```

## 4. Domain Model

### 4.1 Core Entities

#### InfrastructureProvider
Represents a registered compute backend where containers can be provisioned.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Primary key |
| Code | string | Unique identifier (e.g., `local-docker`, `rivoli-eu-west`, `azure-aci-prod`) |
| Name | string | Display name |
| Type | ProviderType | Docker, AppleContainer, Rivoli, Ssh, AzureAci, AzureAca, AzureAcp |
| ConnectionConfig | jsonb | Provider-specific connection settings (encrypted) |
| Capabilities | jsonb | GPU, architecture (arm64/amd64), OS, max resources |
| Region | string? | Geographic region |
| OrganizationId | Guid? | Scoped to organization (null = global) |
| IsEnabled | bool | Whether provider is available |
| HealthStatus | ProviderHealth | Healthy, Degraded, Unreachable |
| LastHealthCheck | DateTime? | |
| CreatedAt | DateTime | |
| Metadata | jsonb | |

#### ContainerTemplate
A reusable, versioned blueprint for creating containers. Organized in a catalog.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Primary key |
| Code | string | Unique identifier (e.g., `dotnet-8-vscode`, `full-stack-gpu`) |
| Name | string | Display name |
| Description | string? | |
| Version | string | Semantic version |
| BaseImage | string | Docker/OCI image reference |
| CatalogScope | CatalogScope | Global, Organization, Team, User |
| OrganizationId | Guid? | Owner org (if org/team/user scope) |
| TeamId | Guid? | Owner team (if team/user scope) |
| OwnerId | string? | Owner user subject ID (if user scope) |
| Toolchains | jsonb | List of toolchains: dotnet, python, node, angular, etc. |
| IdeType | IdeType | None, CodeServer (VSCode), Zed, Both |
| DefaultResources | jsonb | CPU, memory, disk defaults |
| GpuRequired | bool | Whether GPU is mandatory |
| GpuPreferred | bool | Whether GPU is preferred but optional |
| EnvironmentVariables | jsonb | Default env vars |
| Ports | jsonb | Default port mappings |
| Scripts | jsonb | Lifecycle scripts (init, setup, teardown) |
| Tags | string[] | Searchable tags |
| IsPublished | bool | Visible in catalog |
| ParentTemplateId | Guid? | Inheritance from parent template |
| CreatedAt | DateTime | |
| UpdatedAt | DateTime? | |
| Metadata | jsonb | |

#### Container
A running (or stopped) container instance.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Primary key |
| Name | string | User-friendly name |
| TemplateId | Guid | Template used to create this container |
| ProviderId | Guid | Infrastructure provider hosting this container |
| ExternalId | string? | Provider-specific ID (Docker container ID, ACI name, etc.) |
| Status | ContainerStatus | Creating, Running, Stopped, Failed, Destroying, Destroyed |
| OwnerId | string | Subject ID of the owner |
| OrganizationId | Guid? | Organization context |
| TeamId | Guid? | Team context |
| AllocatedResources | jsonb | Actual CPU, memory, disk, GPU allocated |
| NetworkConfig | jsonb | IP, ports, DNS, endpoint URLs |
| IdeEndpoint | string? | URL for VSCode/Zed web access |
| VncEndpoint | string? | URL for noVNC remote desktop |
| GitRepository | jsonb | Cloned repo info (URL, branch, credentials ref) |
| EnvironmentVariables | jsonb | Runtime env vars (secrets encrypted) |
| CreatedAt | DateTime | |
| StartedAt | DateTime? | |
| StoppedAt | DateTime? | |
| ExpiresAt | DateTime? | Auto-cleanup deadline |
| LastActivityAt | DateTime? | |
| Metadata | jsonb | |

#### Workspace
A higher-level abstraction grouping one or more containers into a coherent development environment. Analogous to a "Codespace" or "Dev Environment."

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Primary key |
| Name | string | User-friendly name |
| Description | string? | |
| OwnerId | string | Subject ID |
| OrganizationId | Guid? | |
| TeamId | Guid? | |
| Status | WorkspaceStatus | Active, Suspended, Archived |
| DefaultContainerId | Guid? | Primary container |
| GitRepositoryUrl | string? | Associated repository |
| GitBranch | string? | |
| Configuration | jsonb | Workspace-level settings |
| CreatedAt | DateTime | |
| UpdatedAt | DateTime? | |
| LastAccessedAt | DateTime? | |
| Metadata | jsonb | |

#### ContainerSession
Tracks active connections to containers (SSH, IDE, VNC, agent).

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Primary key |
| ContainerId | Guid | |
| SubjectId | string | Who is connected |
| SessionType | SessionType | Ide, Vnc, Ssh, Agent, Api |
| Status | SessionStatus | Active, Disconnected, Expired |
| EndpointUrl | string? | Connection URL |
| StartedAt | DateTime | |
| EndedAt | DateTime? | |
| AgentId | string? | DevPilot agent ID (if agent session) |
| Metadata | jsonb | |

#### ContainerEvent
Audit trail for container lifecycle events.

| Field | Type | Description |
|-------|------|-------------|
| Id | Guid | Primary key |
| ContainerId | Guid | |
| EventType | ContainerEventType | Created, Started, Stopped, Resized, Failed, etc. |
| SubjectId | string? | Who triggered it |
| Details | jsonb | Event-specific data |
| Timestamp | DateTime | |

### 4.2 Enumerations

```csharp
enum ProviderType
{
    Docker,             // Docker Engine (local or remote)
    AppleContainer,     // macOS native Apple Containers
    Rivoli,             // Rivoli-managed infrastructure
    Ssh,                // Third-party server via SSH
    AzureAci,           // Azure Container Instances
    AzureAca,           // Azure Container Apps
    AzureAcp            // Azure ACP (Azure Container Platform)
}

enum CatalogScope
{
    Global,             // Available to everyone
    Organization,       // Available within an org
    Team,               // Available to team members
    User                // Private to user
}

enum ContainerStatus
{
    Pending,            // Queued for creation
    Creating,           // Being provisioned
    Running,            // Active and healthy
    Stopping,           // Graceful shutdown in progress
    Stopped,            // Stopped but preserving state
    Failed,             // Creation or runtime failure
    Destroying,         // Being removed
    Destroyed           // Fully removed
}

enum IdeType
{
    None,               // No IDE
    CodeServer,         // VSCode via code-server
    Zed,                // Zed editor
    Both                // Both available
}

enum SessionType
{
    Ide,                // VSCode / Zed web IDE
    Vnc,                // noVNC remote desktop
    Ssh,                // SSH terminal
    Agent,              // DevPilot AI agent
    Api                 // API/programmatic access
}
```

## 5. Infrastructure Provider Architecture

### 5.1 Provider Abstraction

All infrastructure providers implement a common interface:

```csharp
public interface IInfrastructureProvider
{
    ProviderType Type { get; }

    Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct);
    Task<ProviderHealth> HealthCheckAsync(CancellationToken ct);

    // Container lifecycle
    Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct);
    Task StartContainerAsync(string externalId, CancellationToken ct);
    Task StopContainerAsync(string externalId, CancellationToken ct);
    Task DestroyContainerAsync(string externalId, CancellationToken ct);
    Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct);

    // Resource management
    Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct);

    // Connectivity
    Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct);

    // Execution
    Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct);
}
```

### 5.2 Provider Implementations

#### Docker Provider
- Uses `Docker.DotNet` library to communicate with Docker Engine API
- Supports local Docker Desktop or remote Docker hosts (TCP/TLS)
- Image management: pull, build, tag
- Volume management for persistent workspace data
- Network management for inter-container communication
- GPU passthrough via NVIDIA Container Toolkit

#### Apple Container Provider (macOS)
- Native integration with Apple's container runtime on macOS
- Uses the `container` CLI tool (`container run`, `container exec -it`, `container inspect`)
- Lightweight Linux VMs on Apple Silicon
- Fast startup times, low overhead
- Rosetta 2 for x86_64 compatibility
- Shared filesystem via virtiofs
- Pre-creation cleanup: automatically removes existing containers with the same name
- Shell access via `container exec -it <name> bash` (direct SSH not supported due to vmnet limitations)
- Ideal for local development on Mac

#### Rivoli Provider
- HTTP/gRPC client to Rivoli-managed container fleet
- Pre-provisioned infrastructure with guaranteed capacity
- Multi-region support (eu-west, us-east, etc.)
- Built-in monitoring and log aggregation
- SLA-backed availability

#### SSH Provider (Third-Party)
- Connects to any Linux server via SSH (using `SSH.NET`)
- Installs and manages Docker remotely
- Supports SSH key and password authentication
- Port forwarding for IDE/VNC access
- Ideal for on-premise servers or VPS instances

#### Azure ACI Provider
- Provisions Azure Container Instances via `Azure.ResourceManager.ContainerInstance`
- Serverless, per-second billing
- GPU support (NVIDIA Tesla T4/V100/A100)
- VNet integration for private networking
- Fast startup (~30 seconds)

#### Azure ACA Provider
- Deploys to Azure Container Apps via `Azure.ResourceManager.AppContainers`
- Auto-scaling, revision management
- Dapr integration for service-to-service communication
- Custom domain and TLS support
- Suitable for long-running workspaces

#### Azure ACP Provider
- Integration with Azure Container Platform
- Enterprise-grade container orchestration
- Managed Kubernetes backend
- Advanced networking and security policies

### 5.3 Infrastructure Routing

The **InfrastructureRoutingService** selects the best provider for a container request based on:

1. **Explicit provider** — User/template specifies a provider
2. **Capability matching** — GPU requirements, architecture, OS
3. **Affinity rules** — Organization preferences, data residency
4. **Capacity** — Available resources on each provider
5. **Cost optimization** — Prefer cheaper providers when capabilities match
6. **Latency** — Prefer providers closer to the user

```csharp
public interface IInfrastructureRoutingService
{
    Task<InfrastructureProvider> SelectProviderAsync(
        ContainerSpec spec,
        RoutingPreferences? preferences,
        CancellationToken ct);

    Task<IReadOnlyList<InfrastructureProvider>> GetCandidateProvidersAsync(
        ContainerSpec spec,
        CancellationToken ct);
}
```

## 6. Container Template Catalog

### 6.1 Catalog Hierarchy

Templates are organized in a hierarchical catalog with visibility scoping:

```
Global Catalog (maintained by Rivoli)
├── dotnet-8-vscode          → .NET 8 SDK + VSCode
├── python-3.12-vscode       → Python 3.12 + VSCode
├── angular-18-vscode        → Node 20 + Angular 18 + VSCode
├── full-stack               → .NET + Python + Node + Angular + VSCode
├── full-stack-gpu           → Full stack + NVIDIA GPU support
├── andy-cli-dev             → Pre-installed Andy CLI environment
└── agent-sandbox            → Minimal environment for DevPilot agents

Organization Catalog (per org, extends global)
├── acme-backend             → Custom .NET config for ACME Corp
└── acme-data-science        → Python + Jupyter + GPU

Team Catalog (per team, extends org)
├── team-alpha-workspace     → Team-specific tooling
└── team-alpha-review        → Lightweight review environment

User Catalog (per user, extends team)
├── my-custom-env            → Personal customization
└── experiment-gpu           → Personal GPU experiments
```

### 6.2 Template Inheritance

Templates can inherit from parent templates, overriding specific fields:

```
full-stack (global)
  └── acme-backend (org) [inherits full-stack, adds private NuGet feed]
        └── team-alpha-workspace (team) [inherits acme-backend, adds team secrets]
              └── my-custom-env (user) [inherits team workspace, adds personal dotfiles]
```

### 6.3 Built-In Templates

| Template | Toolchains | IDE | GPU | Base Image |
|----------|-----------|-----|-----|------------|
| `dotnet-8-vscode` | .NET 8 SDK | code-server | No | Ubuntu 24.04 |
| `python-3.12-vscode` | Python 3.12, pip, venv | code-server | No | Ubuntu 24.04 |
| `angular-18-vscode` | Node 20, npm, Angular CLI 18 | code-server | No | Ubuntu 24.04 |
| `full-stack` | .NET 8, Python 3.12, Node 20, Angular 18 | code-server | No | Ubuntu 24.04 |
| `full-stack-gpu` | .NET 8, Python 3.12, Node 20, Angular 18 | code-server | Yes | Ubuntu 24.04 + CUDA |
| `andy-cli-dev` | .NET 8, Andy CLI pre-installed | code-server + terminal | No | Ubuntu 24.04 |
| `agent-sandbox` | .NET 8, Python 3.12, git | None (headless) | No | Ubuntu 24.04 minimal |
| `agent-sandbox-ui` | .NET 8, Python 3.12, git | Zed + noVNC | Optional | Ubuntu 24.04 + XFCE |

## 7. Security Model

### 7.1 Authentication (Andy Auth)

- All API requests require a valid JWT token from Andy Auth
- OAuth 2.0 Authorization Code flow for Web UI and CLI
- Client Credentials flow for service-to-service (DevPilot → Containers API)
- Token validation via JWKS endpoint

### 7.2 Authorization (Andy RBAC)

Container operations are protected by RBAC permissions in the format `andy-containers:{resource}:{action}`:

| Permission | Description |
|-----------|-------------|
| `container:create` | Create new containers |
| `container:read` | View container details |
| `container:start` | Start a stopped container |
| `container:stop` | Stop a running container |
| `container:destroy` | Destroy a container |
| `container:exec` | Execute commands in a container |
| `container:connect` | Open IDE/VNC/SSH session |
| `workspace:create` | Create workspaces |
| `workspace:read` | View workspaces |
| `workspace:manage` | Update/delete workspaces |
| `template:create` | Create container templates |
| `template:read` | Browse template catalog |
| `template:publish` | Publish templates to catalog |
| `template:manage` | Update/delete templates |
| `provider:create` | Register infrastructure providers |
| `provider:read` | View providers |
| `provider:manage` | Configure/delete providers |
| `agent:spawn` | Spawn DevPilot agents on containers |
| `admin:*` | Full administrative access |

### 7.3 Resource-Level Permissions

Beyond role-based permissions, containers support instance-level sharing:

- Container owners have full access to their containers
- Containers can be shared with specific users (read, connect, exec)
- Team containers are accessible to team members per their role
- Organization admins can manage all containers in their org

### 7.4 Secret Management

- Container environment variables containing secrets are encrypted at rest (AES-256-GCM)
- Git credentials are stored as secret references, injected at container start
- API keys for LLM providers are passed securely to agent containers
- No secrets are exposed in API responses or logs

## 8. Andy-DevPilot Integration

### 8.1 Agent Spawning Flow

```
DevPilot Backend                     Containers API
     │                                    │
     │ POST /api/containers               │
     │ { template: "agent-sandbox-ui",    │
     │   git: { url, branch, token },     │
     │   agent: { model, apiKey } }       │
     │───────────────────────────────────▶│
     │                                    │ Select provider
     │                                    │ Provision container
     │                                    │ Clone repository
     │                                    │ Start IDE + agent tools
     │                                    │
     │◀───────────────────────────────────│
     │ { containerId, ideEndpoint,        │
     │   vncEndpoint, agentEndpoint }     │
     │                                    │
     │ Connect WebSocket to agentEndpoint │
     │───────────────────────────────────▶│
     │                                    │
     │ Stream agent output, tool calls    │
     │◀──────────────────────────────────▶│
```

### 8.2 Agent Container Types

| Type | UI | GPU | Use Case |
|------|-----|-----|---------|
| Headless Agent | None | No | Code analysis, generation, testing |
| UI Agent (Software) | noVNC + Zed | No | Browser testing, UI development |
| UI Agent (GPU) | noVNC + Zed | Yes | ML training, GPU-accelerated tasks |

### 8.3 Agent Communication Protocol

Containers expose an agent endpoint compatible with DevPilot's ACP (Agent Control Protocol):

- **WebSocket** for real-time bidirectional communication
- **HTTP bridge** for command execution (git, shell, file ops)
- **Streaming** for real-time log output
- **Status callbacks** for lifecycle events (ready, completed, failed)

### 8.4 DevPilot Client Integration

The `Andy.Containers.Client` library provides a high-level API for DevPilot:

```csharp
public interface IContainerClient
{
    // Provision a container for agent work
    Task<AgentContainer> SpawnAgentContainerAsync(AgentContainerRequest request, CancellationToken ct);

    // Connect to agent endpoint
    Task<IAgentConnection> ConnectAgentAsync(Guid containerId, CancellationToken ct);

    // Get container endpoints (IDE, VNC)
    Task<ContainerEndpoints> GetEndpointsAsync(Guid containerId, CancellationToken ct);

    // Cleanup
    Task DestroyContainerAsync(Guid containerId, CancellationToken ct);
}
```

## 9. GPU Support

### 9.1 GPU Detection

Each infrastructure provider reports its GPU capabilities:

```csharp
public class GpuCapability
{
    public string Vendor { get; set; }       // nvidia, amd, apple
    public string Model { get; set; }        // "Tesla T4", "A100", "M3 Max"
    public int MemoryMb { get; set; }        // GPU memory in MB
    public int Count { get; set; }           // Number of GPUs available
    public bool IsAvailable { get; set; }    // Currently free
}
```

### 9.2 GPU Allocation

- Templates can specify `GpuRequired` (fail if no GPU) or `GpuPreferred` (use if available)
- The routing service matches GPU requirements to provider capabilities
- NVIDIA Container Toolkit for Docker-based providers
- Azure GPU SKUs (NC-series) for ACI/ACA
- Apple Metal for Apple Container provider (future)

## 10. Container Lifecycle

```
         ┌─────────┐
         │ Pending  │ ← Request queued
         └────┬─────┘
              │ Provider selected
              ▼
         ┌──────────┐
         │ Creating  │ ← Image pull, volume setup, network config
         └────┬──────┘
              │ Container started
              ▼
         ┌──────────┐     ┌──────────┐
    ┌───▶│ Running   │◀───│ Starting │
    │    └────┬──────┘    └──────────┘
    │         │ Stop requested     ▲
    │         ▼                    │ Start requested
    │    ┌──────────┐              │
    │    │ Stopping  │             │
    │    └────┬──────┘             │
    │         │                    │
    │         ▼                    │
    │    ┌──────────┐──────────────┘
    └────│ Stopped   │
         └────┬──────┘
              │ Destroy requested or expired
              ▼
         ┌───────────┐
         │ Destroying │
         └────┬───────┘
              │
              ▼
         ┌───────────┐
         │ Destroyed  │
         └────────────┘

    Any state ──▶ Failed (on error)
```

### 10.1 Auto-Cleanup

- Containers have an optional `ExpiresAt` timestamp
- A background service checks for expired containers every 5 minutes
- Idle containers (no sessions for configurable duration) are auto-stopped
- Stopped containers older than configurable duration are auto-destroyed
- Destroyed container metadata is retained for audit (soft delete)

## 11. API Design

### 11.1 REST API Groups

| Group | Base Path | Description |
|-------|----------|-------------|
| Containers | `/api/containers` | CRUD + lifecycle management |
| Workspaces | `/api/workspaces` | Workspace management |
| Templates | `/api/templates` | Template catalog CRUD |
| Providers | `/api/providers` | Infrastructure provider management |
| Sessions | `/api/sessions` | Active session tracking |
| Health | `/health` | Service health check |

### 11.2 gRPC Service

High-performance container operations for DevPilot integration:
- `CreateContainer`, `StartContainer`, `StopContainer`, `DestroyContainer`
- `GetContainerStatus`, `ExecCommand`
- `StreamLogs` (server streaming)

### 11.3 MCP Tools

AI assistant integration for managing containers from Claude Desktop, Cursor, etc.:
- List/create/manage containers and workspaces
- Browse template catalog
- Check container status and logs
- Connect to running containers
- Clone and manage git repositories in containers
- Store and list git credentials
- Get image manifests and installed tool lists
- Compare images to see what changed
- Search for images by installed tool

## 12. Technology Stack

| Layer | Technology |
|-------|-----------|
| **Framework** | ASP.NET Core 8.0 |
| **Language** | C# 12 |
| **Database** | PostgreSQL 16 + EF Core 8.0 |
| **APIs** | REST, gRPC, MCP |
| **Web UI** | Blazor Server |
| **CLI** | System.CommandLine + Spectre.Console |
| **Docker** | Docker.DotNet |
| **Azure** | Azure.ResourceManager SDK |
| **SSH** | SSH.NET |
| **Auth** | JWT Bearer (Andy Auth) |
| **AuthZ** | Andy RBAC Client |
| **Caching** | IMemoryCache (Redis-ready) |
| **Logging** | Serilog |
| **Observability** | OpenTelemetry (tracing + metrics), Serilog |
| **Testing** | xUnit, FluentAssertions, Moq, bUnit |

## 13. Deployment

### 13.1 Docker Compose (Local Development)

```
PostgreSQL ← andy-containers-db (port 5434)
API Server ← andy-containers-api (port 5200)
Web UI     ← andy-containers-web (port 5280)
```

### 13.2 Production

- API and Web deployed as Docker containers (Railway, Azure, or self-hosted)
- PostgreSQL managed database
- Infrastructure providers configured via environment variables or admin UI
- Health checks and readiness probes

---

## 14. Image Versioning and Dependency Tracking

### 14.1 Content-Addressed Images

Every built container image has a **content hash** — a SHA-256 digest computed from the sorted dependency manifest (all resolved dependency names, versions, and artifact hashes) plus the base image digest. This guarantees:

- **Uniqueness**: Two images with the same content hash are byte-identical
- **Reproducibility**: Given the same dependency lock file, the same image is produced
- **Traceability**: Every image can be traced back to its exact dependency versions

Image tags follow the format: `{template-code}:{version}-{build-number}`
Example: `full-stack:1.2.0-42`

### 14.2 Declarative Dependencies

Developers declare **what** they need, not **how** to install it:

```yaml
# Example: what the user specifies in a template
dependencies:
  - type: sdk
    name: dotnet-sdk
    version: "8.0.*"         # Any 8.0.x
    auto_update: true
    update_policy: patch     # Auto-rebuild on 8.0.3 → 8.0.4

  - type: runtime
    name: python
    version: ">=3.12,<4.0"
    auto_update: true
    update_policy: minor     # Auto-rebuild on 3.12 → 3.13

  - type: tool
    name: node
    version: "20.x"
    auto_update: true
    update_policy: minor

  - type: tool
    name: angular-cli
    version: "latest"
    auto_update: true
    update_policy: major

  - type: library
    name: numpy
    ecosystem: pip
    version: ">=1.26,<2.0"
    auto_update: true
    update_policy: patch
```

### 14.3 Dependency Resolution and Locking

When an image is built:

1. **Resolution**: Each dependency constraint is resolved to an exact version from upstream registries/feeds
2. **Locking**: Resolved versions, sources, and artifact SHA-256 hashes are recorded in a `DependencyLock`
3. **Hashing**: The lock file contents are hashed to produce the content-addressed image ID
4. **Building**: The image is built using the locked versions
5. **Verification**: After build, installed versions are verified against the lock file

The dependency lock captures:

```json
{
  "baseImage": "ubuntu:24.04@sha256:abc123...",
  "resolvedAt": "2026-02-10T14:30:00Z",
  "dependencies": [
    {
      "name": "dotnet-sdk",
      "type": "sdk",
      "constraint": "8.0.*",
      "resolvedVersion": "8.0.404",
      "source": "https://dotnetcli.azureedge.net/dotnet/Sdk/8.0.404/dotnet-sdk-8.0.404-linux-x64.tar.gz",
      "artifactHash": "sha256:def456...",
      "artifactSize": 218103808
    },
    {
      "name": "python",
      "type": "runtime",
      "constraint": ">=3.12,<4.0",
      "resolvedVersion": "3.12.8",
      "source": "deadsnakes PPA",
      "artifactHash": "sha256:789abc..."
    }
  ]
}
```

### 14.4 Automatic Rebuild on Upstream Changes

A background service (`DependencyUpdateChecker`) periodically:

1. Queries upstream registries for new versions matching each template's constraints
2. Evaluates each update against the template's `UpdatePolicy`:
   - **SecurityOnly**: Only CVE-patched releases
   - **Patch**: 8.0.3 → 8.0.4 (yes), 8.0 → 8.1 (no)
   - **Minor**: 8.0 → 8.1 (yes), 8 → 9 (no)
   - **Major**: Any version change
   - **Manual**: Never auto-rebuild
3. If updates are available and policy allows, triggers a new image build
4. The new image is linked to the previous image via `PreviousImageId`
5. A changelog is generated showing exactly what changed

### 14.5 Image Diff and Changelog

Users and admins can compare any two images to see what changed:

```
Image #41 → #42 changelog:
  UPDATED  dotnet-sdk      8.0.403 → 8.0.404  (patch)
  UPDATED  python          3.12.7  → 3.12.8   (patch, security fix: CVE-2026-1234)
  UNCHANGED node           20.18.1
  UNCHANGED angular-cli    18.2.12
```

### 14.6 Runtime Introspection

After an image is built, the platform automatically introspects the running container to discover what is actually installed. This produces an `ImageToolManifest` containing:

- **Architecture** (amd64, arm64)
- **Operating System** (name, version, codename, kernel)
- **Installed Tools** with exact versions (15+ tool types: dotnet, python, node, go, rust, java, git, etc.)
- **OS Packages** (dpkg on Debian/Ubuntu, apk on Alpine)

The introspection runs as a single shell script inside the container, avoiding 15+ individual exec calls. Tool version output is parsed using compile-time generated regexes (`[GeneratedRegex]`). Each detected tool is matched against declared template dependencies to flag version mismatches.

The manifest is serialized to the `DependencyManifest` JSONB column on `ContainerImage`, and `ResolvedDependency` records are created for each tool.

### 14.7 Image Diffing

Any two images can be compared to produce a structured diff showing:

- **Tool changes** (Added, Removed, VersionChanged) with severity classification (Major, Minor, Patch)
- **Package changes** (added, removed, upgraded, downgraded counts)
- **Base image** changes
- **OS version** changes
- **Architecture** changes
- **Size** delta in MB

The diff API is exposed via REST (`GET /api/images/diff`), MCP (`CompareImages` tool), and gRPC (`DiffImages` RPC).

### 14.8 Air-Gapped / Offline Builds

For environments with no internet access (secure environments, CI pipelines):

1. **Dependency Cache**: All resolved artifacts are cached in an internal registry/store
2. **Offline Build Mode**: `BuildImageAsync(templateId, new ImageBuildOptions { Offline = true })`
3. **Pre-fetching**: A connected environment resolves and caches dependencies
4. **Verification**: The offline build verifies artifact hashes match the lock file
5. **Fail-fast**: If any dependency is not in the offline cache, the build fails immediately with a clear error listing what's missing

This ensures containers in air-gapped environments use the exact same verified artifacts as their online-built counterparts.

### 14.9 Library Dependency Tracking

Beyond compilers and tools, templates can track **library dependencies** per ecosystem:

| Ecosystem | Lock File | Registry |
|-----------|-----------|----------|
| NuGet (.NET) | packages.lock.json | nuget.org / private feeds |
| pip (Python) | requirements.txt (pinned) | PyPI / private index |
| npm (Node) | package-lock.json | npmjs.com / private registry |

Library dependencies follow the same versioning, locking, and auto-update model. When a library update matches the update policy, the image is rebuilt with the new version and the change is recorded in the changelog.

## 15. Container Provisioning Pipeline

### 15.1 Channel-Based Queue

Container creation is asynchronous. The `ContainerOrchestrationService` validates the request, creates a `Pending` container in the database, and enqueues a `ContainerProvisionJob` to a `System.Threading.Channels`-based queue. The `ContainerProvisioningWorker` (a `BackgroundService`) reads from this queue and handles provisioning with a 5-minute timeout.

### 15.2 Post-Create Scripts

Templates define lifecycle scripts in their `Scripts` JSONB field. The `post_create` script runs after the container reaches `Running` status, before git clones. The seed templates include a multi-distro script that:

1. Detects the package manager (apt-get, apk, dnf, yum, zypper, pacman)
2. Installs essential tools: git, curl, wget, ca-certificates, openssh-server
3. Configures and starts SSH (root login with password `container`)
4. Generates SSH host keys

Failed post-create scripts log a warning but do not fail the container.

### 15.3 Crash Recovery

On startup, the provisioning worker scans for containers stuck in `Creating` or `Pending` status for more than 2 minutes and marks them as `Failed`.

### 15.4 Web Terminal

The Web UI provides a browser-based terminal at `/containers/{id}/terminal` that executes commands via the container exec API. Features include command history (arrow keys), working directory tracking, Ctrl+L to clear, and color-coded output. This is the primary access method for Apple Containers where direct SSH is not available.

## 16. Git Repository Management

### 16.1 Multi-Repository Clone

Containers support cloning multiple git repositories at creation time or into running containers. Each repository is tracked as a `ContainerGitRepository` entity with individual clone status (`Pending`, `Cloning`, `Cloned`, `Failed`).

Repository sources:
1. **Request repos** -- specified in the `CreateContainerRequest.GitRepositories` list
2. **Template repos** -- default repositories defined on the `ContainerTemplate.GitRepositories` JSONB field
3. **Runtime repos** -- added via REST, MCP, or gRPC to a running container

Template repos are automatically merged with request repos unless `ExcludeTemplateRepos` is set.

### 16.2 Git Credential Management

Private repository access is handled via `GitCredential` entities:

- Credentials are encrypted using ASP.NET Core Data Protection API
- Tokens are never returned in API responses
- Resolution order: explicit `credentialRef` label match, then auto-match by git host
- Credential injection uses HTTPS URL format (`https://token@host/...`), cleaned up after clone

### 16.3 Clone Flow

1. Validate repository URLs (HTTPS and SSH only, no embedded credentials, no path traversal)
2. Resolve credential if needed (by label or host auto-match)
3. Execute `git clone` inside the container via `IInfrastructureProvider.ExecAsync`
4. Update clone status and timestamps
5. Failed clones do NOT fail the container -- the container stays Running

---

**Status:** Alpha
**Version:** 0.1.0-alpha
**Last Updated:** 2026-03-23
