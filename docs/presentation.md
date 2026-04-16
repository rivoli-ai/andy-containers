---
marp: true
theme: default
paginate: true
size: 16:9
header: 'Andy Containers — End-to-End Walkthrough'
footer: 'Rivoli AI · andy-containers'
style: |
  section { font-size: 24px; }
  section h1 { color: #1f4e79; }
  section h2 { color: #2e75b6; border-bottom: 2px solid #2e75b6; padding-bottom: 4px; }
  code { background: #f4f4f4; padding: 2px 4px; border-radius: 3px; }
  pre { font-size: 18px; }
  table { font-size: 20px; }
---

<!-- _class: lead -->
<!-- _paginate: false -->

# Andy Containers
## End-to-End System Walkthrough

Infrastructure-agnostic dev-container and sandbox runtime for the Andy ecosystem.

*Designed for engineers who have never seen this service before.*

---

## What is Andy Containers?

A **microservice** that provisions and manages isolated dev environments (sandboxes) across many backends — Docker, Apple Containers, SSH hosts, Azure, AWS Fargate, GCP Cloud Run, Hetzner, Fly.io, …

- One API surface, many **infrastructure providers**
- **Templates** define the dev experience (toolchains, IDE, ports, scripts)
- Web terminal + IDE + VNC endpoints per container
- **Code assistants** pre-installed (Claude Code, Aider, Codex, …)
- Publishes **lifecycle events** to NATS so other services (e.g. andy-issues) can react

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Runtime | .NET 8.0 |
| API | REST + gRPC + MCP + WebSocket |
| Frontend | Angular 18 + Tailwind (xterm.js, noVNC) |
| Database | PostgreSQL (prod) / SQLite (embedded) |
| ORM | EF Core 8 with JSONB |
| Container runtime | **Docker.DotNet**, shell, SDKs (Azure/AWS/GCP/…) |
| Messaging | NATS JetStream (or in-memory) |
| Auth | JWT (Andy Auth) + RBAC |
| Observability | Serilog + OpenTelemetry |

---

## Solution Layout

```
andy-containers/
├── src/
│   ├── Andy.Containers/                ← core models, abstractions
│   ├── Andy.Containers.Infrastructure/ ← EF, providers, messaging
│   ├── Andy.Containers.Api/            ← REST/gRPC/MCP + workers
│   ├── Andy.Containers.Client/         ← HTTP client lib
│   ├── Andy.Containers.Cli/            ← `andy-containers` CLI
│   └── andy-containers-web/            ← Angular 18 SPA
└── tests/
    ├── Andy.Containers.Tests/
    ├── Andy.Containers.Api.Tests/
    ├── Andy.Containers.Client.Tests/
    └── Andy.Containers.Integration.Tests/
```

---

## Domain Model — Container

**`Container`** (`src/Andy.Containers/Models/Container.cs`):

- `Name`, `TemplateId`, `ProviderId`, `ExternalId` (provider-specific)
- `Status`: Pending → Creating → Running → Stopping → Stopped → Failed → Destroying → Destroyed
- `OwnerId`, `OrganizationId?`, `TeamId?`
- `AllocatedResources`, `NetworkConfig` (JSONB)
- `IdeEndpoint`, `VncEndpoint`, `HostIp`
- `EnvironmentVariables` (encrypted), `CodeAssistant` (JSON)
- `CreationSource`: WebUi / RestApi / Mcp / Grpc / Cli
- **`StoryId?`** — correlation key with andy-issues

---

## Domain Model — Templates & Providers

**`ContainerTemplate`** — the dev environment recipe:

- `Code`, `Version`, `BaseImage`
- `CatalogScope`: Global / Organization / Team / User
- `IdeType`: None / CodeServer / Zed / Both
- `GuiType`: none / vnc
- `Toolchains`, `DefaultResources`, `Scripts` (`post_create`, `setup`)
- `Ports`, `GitRepositories`, `CodeAssistant`
- `ParentTemplateId?` — inheritance
- 12 built-ins: `dotnet-8-vscode`, `python-3.12-vscode`, `angular-18-vscode`, `full-stack`, `full-stack-gpu`, `andy-cli-dev`, desktop variants, …

**`InfrastructureProvider`** — `Code`, `Type` (13 types), `ConnectionConfig` (encrypted), `Capabilities`, `HealthStatus`.

---

## Domain Model — Sessions, Events, Workspaces

- **`ContainerSession`** — active connection (`Ide`, `Vnc`, `Ssh`, `Agent`, `Api`); `SubjectId`, `EndpointUrl`, `AgentId?`
- **`ContainerEvent`** — audit trail of lifecycle transitions
- **`ContainerGitRepository`** — per-container clone state
- **`Workspace`** — higher-level grouping (Active / Suspended / Archived)
- **`OutboxEntry`** — transactional outbox for NATS events

All enum fields stored as strings; all JSON fields as jsonb (PG) or TEXT (SQLite).

---

## Application Interfaces

**`IContainerService`** — user-facing orchestration:

- `CreateContainerAsync` / `GetContainerAsync` / `ListContainersAsync`
- `StartContainerAsync` / `StopContainerAsync` / `DestroyContainerAsync`
- `GetStatsAsync`, `ResizeContainerAsync`, `ExecAsync`

**`IInfrastructureProvider`** — implemented per backend:

- `HealthCheckAsync`, `CreateContainerAsync`, `Start/Stop/Destroy`, `GetContainerInfoAsync`, `ExecAsync`

**`IInfrastructureRoutingService`** — picks the best provider for a spec.

---

## Providers — 13 Backends

```
Local:     Docker, AppleContainer
Server:    Ssh
Cloud:     AzureAci, AzureAca, AzureAcp,
           AwsFargate, GcpCloudRun
Commodity: Hetzner, DigitalOcean, Civo, FlyIo
Internal:  Rivoli
```

Each implements the same `IInfrastructureProvider` contract. Routing picks based on capability (GPU, arch, OS), cost, latency, health.

`DockerInfrastructureProvider` (`Infrastructure/Providers/Local/DockerInfrastructureProvider.cs`) uses **Docker.DotNet** against `/var/run/docker.sock` or TCP.

---

## REST API Surface

| Controller | Base path | Highlights |
|-----------|-----------|-----------|
| `ContainersController` | `/api/containers` | list, create, start, stop, resize, stats, WS exec |
| `TemplatesController` | `/api/templates` | CRUD |
| `ProvidersController` | `/api/providers` | register, health |
| `WorkspacesController` | `/api/workspaces` | CRUD |
| `SessionsController` | `/api/sessions` | disconnect |
| `GitCredentialsController` | `/api/credentials/git` | encrypted storage |
| `ApiKeysController` | `/api/settings/keys` | LLM key vault |
| `TerminalController` | `/api/containers/{id}/terminal` | WebSocket |
| `ImagesController` | `/api/images` | introspection |

All routes `[Authorize]` + `[RequirePermission]`.

---

## gRPC & MCP Surfaces

**gRPC** — `ContainersGrpc.proto` → `ContainerGrpcService`:

- `CreateContainer`, `GetContainerStatus`, `ExecCommand`
- `StreamLogs` (server-streaming) — used by DevPilot agents

**MCP** — `Mcp/ContainersMcpTools.cs` (via `/mcp`):

- `ListContainers`, `GetContainer`, `CreateContainer`, `StartContainer`, `StopContainer`, `DestroyContainer`
- `ListTemplates`, `GetTemplate`, `ListProviders`
- `CloneRepository`, `ListCredentials`, `StoreCredential`
- `GetImageManifest`, `CompareImages`

---

## The Background Workers

Five `BackgroundService` implementations:

| Worker | Cadence | Purpose |
|--------|---------|---------|
| `ContainerProvisioningWorker` | queue-driven | create containers, run post-create, clone repos, install assistant |
| `ContainerStatusSyncWorker` | 15s | poll providers, detect drift |
| `ProviderHealthCheckWorker` | 60s | Healthy / Degraded / Unreachable |
| `ContainerScreenshotWorker` | 30s | capture tmux text for UI tiles |
| `ImageBuildWorker` | 30s | track custom image builds |

Crash recovery: containers stuck > 2 min in Pending → marked Failed.

---

## Events — NATS JetStream

**Subject taxonomy** (ADR 0001):

```
andy.containers.events.run.{runId}.{kind}
```

**Kinds** (`Messaging/Events/RunEvents.cs`):

- `.finished` — graceful stop
- `.failed` — provisioning or runtime error
- `.cancelled` — explicit destroy

**Payload** (`RunEventPayload`, snake_case):

```json
{ "run_id": "…", "story_id": "…|null",
  "status": "Finished|Failed|Cancelled",
  "exit_code": 0, "duration_seconds": 125.5,
  "schema_version": 1 }
```

Published via an **outbox** → `OutboxDispatcher` → NATS. Andy Issues subscribes via its Story 15.6 consumer.

---

## Angular Web UI

`src/andy-containers-web`:

- **Container list** — filter by status/template/provider, live CPU/RAM/disk polling
- **Container detail** — xterm.js terminal, "Open IDE" button, noVNC iframe
- **VNC desktop** — XFCE4 + TigerVNC for desktop templates
- **Template catalog** — browse Global/Org/Team/User scopes
- **Provider management** — register, test connectivity
- **Settings** — API keys (encrypted), git credentials, polling intervals
- **Organizations/Teams**

Standalone components, Tailwind, typed DTOs from `Andy.Containers.Client`.

---

## CLI (`andy-containers`)

```bash
andy-containers auth login             # OAuth Device Flow (RFC 8628)
andy-containers auth logout

andy-containers list [--status Running] [--org <id>]
andy-containers create --name myenv --template full-stack
andy-containers start <id>
andy-containers stop <id>
andy-containers destroy <id>
andy-containers exec <id> <cmd>
andy-containers ssh <id>               # native SSH
andy-containers stats <id>
andy-containers info <id>

andy-containers templates list
andy-containers providers list
```

Credentials cached at `~/.andy/credentials.json` (mode 600).

---

## Data Flow — Create a Container (1/2)

1. Client → `POST /api/containers { name, templateCode:"full-stack", storyId:"…" }`
2. `ContainersController.CreateAsync` → `[RequirePermission("container:write")]`
3. `ContainerOrchestrationService.CreateContainerAsync`:
   - Resolve template + route to provider
   - Insert `Container { status: Pending, StoryId }`
   - Enqueue `ContainerProvisionJob(containerId)`
4. **Return `201` immediately** with Container DTO + placeholder endpoints

The hard work runs asynchronously in the background worker.

---

## Data Flow — Create a Container (2/2)

5. **`ContainerProvisioningWorker`** picks up the job:
   - Call `IInfrastructureProvider.CreateContainerAsync(spec)` → e.g. Docker
   - Run `post_create` script: install git, SSH, base tools
   - Clone git repos (using encrypted credentials)
   - Install code assistant (Claude Code, Aider, …)
   - Inject LLM API keys
   - Start IDE (code-server on a mapped port)
   - Mark container **Running**; write `IdeEndpoint` + `VncEndpoint`
6. **`OutboxDispatcher`** publishes `andy.containers.events.run.{id}.finished` to NATS
7. **andy-issues** consumer receives → matches by `storyId` → transitions the user story to "Sandbox Available"

---

## Authentication & Authorization

- **Andy Auth** issues JWTs; validated via JWKS
- **Andy RBAC** `[RequirePermission]` on every controller
- **Resource types**: container, template, provider, workspace, credential, api-key, image
- **Audit**: `ContainerEvent` rows capture every state transition
- Service-to-service calls propagate the caller's JWT (`BearerForwardingHandler` pattern)
- Secrets (`ConnectionConfig`, env vars, API keys) encrypted with ASP.NET Core Data Protection

---

## Configuration Snapshot

```json
"ConnectionStrings": { "DefaultConnection": "Host=postgres;Port=5434;…" },
"Database": { "Provider": "PostgreSql" },
"AndyAuth": { "Authority": "https://andy-auth:5001" },
"Rbac":     { "ApiBaseUrl": "https://andy-rbac:7003" },
"Messaging": { "Provider": "Nats",
               "Nats": { "Url": "nats://nats:4222" } },
"OpenTelemetry": { "OtlpEndpoint": "…" },
"CodeAssistant": { "DefaultLlmBaseUrl": "…", "DefaultLlmModel": "…" }
```

Mount: `/var/run/docker.sock` for the Docker provider.

---

## Ports & Docker

| Port | Service |
|------|---------|
| 5200 | API HTTPS |
| 5201 | API HTTP |
| 4200 | Web UI |
| 5434 | PostgreSQL |
| 4222 | NATS |

`docker-compose.yml` runs Postgres 16, NATS 2, API, Web. Dockerfile is multi-stage with custom CA injection and non-root runtime.

Volumes: `postgres_data`, `nats_data`, `dataprotection_keys`.

---

## Testing

| Project | Scope |
|---------|-------|
| `Andy.Containers.Tests` | Domain models |
| `Andy.Containers.Api.Tests` | Controllers + DTOs + auth |
| `Andy.Containers.Client.Tests` | HTTP client |
| `Andy.Containers.Integration.Tests` | E2E (create → provision → list → destroy) |

Tools: xUnit, FluentAssertions, Moq, optional Testcontainers for real Postgres.

```bash
dotnet test --filter Category=Integration
dotnet test --settings coverlet.runsettings
```

---

<!-- _class: lead -->

# Where to start reading

1. `src/Andy.Containers/Models/Container.cs` — the aggregate
2. `src/Andy.Containers/Abstractions/IInfrastructureProvider.cs` — the provider contract
3. `src/Andy.Containers.Api/Services/ContainerOrchestrationService.cs` — the orchestrator
4. `src/Andy.Containers.Api/Services/ContainerProvisioningWorker.cs` — the async machinery
5. `src/Andy.Containers.Infrastructure/Providers/Local/DockerInfrastructureProvider.cs` — canonical provider
6. `src/Andy.Containers/Messaging/Events/RunEvents.cs` — the outbound contract

Web UI: port 4200 · MCP: `/mcp` · NATS: `andy.containers.events.run.>`.
