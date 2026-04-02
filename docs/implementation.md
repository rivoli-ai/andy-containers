# Andy.Containers -- Implementation Guide

## Status

| Metric | Value |
|--------|-------|
| **Source files** | 122 (.cs) + Angular frontend |
| **Test files** | 56 (.cs) + 1 (.spec.ts) |
| **Tests** | 502 API + 259 core + 20 frontend = 781 |
| **Issues** | See GitHub |
| **Version** | 0.1.0-alpha |

| Phase | Stories | Status |
|-------|---------|--------|
| 1: Foundation | 10 | Complete |
| 2: Infrastructure Providers | 12 | Complete |
| 3: Template Catalog | 8 | Complete |
| 4: Container Lifecycle | 14 | Complete |
| 5: Code Assistants | 7 | Complete |
| 6: API Surfaces | 12 | Complete |
| 7: Auth & RBAC | 8 | Complete |
| 8: Web Frontend | 16 | Complete |
| 9: Git & Image Management | 14 | Complete |
| 10: VNC Desktop | 4 | Complete |
| 11: Non-Root Containers | 3 | Complete |
| 12: Image Build Tracking | 3 | Complete |

## 1. Implementation Order

### Phase 1: Foundation
**Goal:** Buildable solution with database, Docker support, health checks, and OpenTelemetry.

```
Solution structure (Andy.Containers, Andy.Containers.Api, Andy.Containers.Infrastructure)
NuGet package configuration (Directory.Packages.props, central version management)
Domain entities (Container, Template, Provider, Workspace, etc.)
EF Core DbContext with code-first configuration
PostgreSQL connection via Npgsql
Program.cs DI registration
Health check endpoint (/health)
Dockerfile (multi-stage build with cert management)
docker-compose.yml (postgres, api, web services)
OpenTelemetry setup (tracing + metrics + OTLP exporter)
```

**Exit Criteria:** `dotnet build` succeeds, `docker-compose up` starts API + PostgreSQL, `/health` returns 200, telemetry spans emitted.

### Phase 2: Infrastructure Providers
**Goal:** Pluggable infrastructure backends with factory pattern and health monitoring.

```
IInfrastructureProvider interface (lifecycle, exec, stats, resize)
InfrastructureProviderFactory (resolves ProviderType -> implementation)
DockerInfrastructureProvider (Docker Engine API via Docker.DotNet)
AppleContainerProvider (macOS native via CLI)
AzureAciProvider (Azure Container Instances via Azure.ResourceManager)
GcpCloudRunProvider (Google Cloud Run via Google.Cloud.Run.V2)
AwsFargateProvider (AWS Fargate via AWSSDK.ECS)
FlyIoProvider (Fly.io REST API)
HetznerCloudProvider (Hetzner Cloud REST API)
DigitalOceanProvider (DigitalOcean REST API)
CivoProvider (Civo REST API)
InfrastructureRoutingService (auto-select best provider based on specs + cost)
ProviderHealthCheckWorker (PeriodicTimer-based background worker)
```

**Exit Criteria:** At least Docker and Apple Container providers pass health checks. Factory resolves all 9 provider types.

### Phase 3: Template Catalog
**Goal:** YAML-first template definitions with hierarchical scoping and data seeding.

```
ContainerTemplate entity (code, version, scope, resources, scripts, toolchains)
YamlTemplateParser (YAML -> ContainerTemplate)
Hierarchical scoping (Global, Organization, Team, User)
CatalogScope enum and filtering
DependencySpec entity (template-level dependency declarations)
DataSeeder (auto-seed providers + templates on startup)
Multi-distro post-create scripts (apt, apk, dnf, yum, zypper, pacman)
Template YAML files (config/templates/global/*.yaml)
```

**Exit Criteria:** Templates are seeded from YAML on startup. Template catalog queryable by scope, search, and organization.

### Phase 4: Container Lifecycle
**Goal:** End-to-end container provisioning with background queue and monitoring.

```
ContainerProvisioningQueue (Channel<T>-based bounded queue)
ContainerProvisioningWorker (BackgroundService consuming queue)
  - Infrastructure provisioning with 5-minute timeout
  - Post-create script execution
  - Environment variable injection
  - Code assistant installation
  - Git repository cloning
  - Stuck container recovery on startup
ContainerOrchestrationService (create, start, stop, destroy, exec)
ContainerStatusSyncWorker (PeriodicTimer, 15s interval, syncs Running/Stopped/Destroyed)
ContainerScreenshotWorker (PeriodicTimer, 30s interval, captures tmux pane text)
ContainerStats (CPU, memory, disk monitoring via provider exec)
Live resize (update CPU/memory on running Docker containers)
Uptime tracking (StartedAt, StoppedAt, human-readable display)
ContainerEvent audit log
```

**Exit Criteria:** Creating a container enqueues a job, provisions on the selected provider, runs setup scripts, and transitions to Running. Status sync detects external state changes.

### Phase 5: Code Assistants
**Goal:** Auto-install AI coding tools in containers with model/provider selection.

```
CodeAssistantType enum (10 tools):
  - ClaudeCode (npm: @anthropic-ai/claude-code)
  - CodexCli (npm: @openai/codex)
  - Aider (pip: aider-chat)
  - Continue (IDE marketplace)
  - OpenCode (binary from GitHub releases)
  - QwenCoder (pip: qwen-coder-cli)
  - GeminiCode (npm: gemini-code)
  - GitHubCopilot (gh extension)
  - AmazonQ (binary installer)
  - Cline (IDE marketplace)
CodeAssistantInstallService (distro-agnostic install scripts)
  - Node.js install: apk / apt+nodesource / dnf
  - Python/pip install: apk / apt / dnf
CodeAssistantConfig (Tool, ModelName, ApiBaseUrl, ApiKeyEnvVar)
ApiKeyCredential entity (encrypted storage, provider-based env var defaults)
ApiKeyService (create, list, validate, delete, inject into containers)
ApiKeyValidationService (HTTP validation against provider APIs)
Default env var mapping per tool (ANTHROPIC_API_KEY, OPENAI_API_KEY, etc.)
```

**Exit Criteria:** Creating a container with `codeAssistant: ClaudeCode` installs Claude Code and injects the API key. All 10 tools install successfully on Alpine, Debian, and RHEL.

### Phase 6: API Surfaces
**Goal:** REST, gRPC, and MCP APIs with full endpoint coverage.

```
REST Controllers (9 controllers, 60+ endpoints):
  - ContainersController (CRUD, exec, stats, resize, git repos)
  - TemplatesController (CRUD, YAML import, publish)
  - ProvidersController (CRUD, health status)
  - WorkspacesController (CRUD, container association)
  - ImagesController (build, diff, manifest, introspect, tools)
  - GitCredentialsController (CRUD for PATs/deploy keys)
  - ApiKeysController (CRUD, validate, inject)
  - OrganizationsController (membership, resources, images)
  - TerminalController (WebSocket terminal proxy)

gRPC Service (containers.proto, 22 RPCs):
  - Container lifecycle, exec, streaming logs
  - Workspace, template, image management
  - Git clone/pull, organization-scoped operations

MCP Tools (22 tools in ContainersMcpTools):
  - ListContainers, GetContainer, CreateContainer
  - BrowseTemplates, ListProviders, ListWorkspaces
  - ListImages, FindImageByTool, CompareImages
  - GetImageManifest, GetImageTools
  - ListContainerRepositories, CloneRepository, PullRepository
  - ListGitCredentials, StoreGitCredential
  - StoreApiKey, ListApiKeys, DeleteApiKey, ValidateApiKey
  - GetOrganizationResources, BuildOrganizationImage

CLI (System.CommandLine + Spectre.Console):
  - auth login, list, connect, exec, stats, etc.
```

**Exit Criteria:** All REST endpoints return correct status codes. gRPC service handles container lifecycle. MCP tools callable from Claude Desktop/Cursor.

### Phase 7: Auth & RBAC
**Goal:** Full authentication and per-endpoint permission enforcement.

```
JWT Bearer authentication via Andy.Auth:
  - Authority + Audience from configuration
  - PKCE flow for frontend (angular-auth-oidc-client)
  - Dev fallback: permissive FallbackPolicy when Authority is empty
  - Dev middleware: auto-assigns admin identity in Development mode

Andy.Rbac.Client integration:
  - AddRbacClient(options) with ApplicationCode = "containers"
  - [RequirePermission] attribute on 63 controller actions
  - AllowAllPolicyProvider fallback when RBAC unavailable

Organization membership:
  - IOrganizationMembershipService (JWT claims + RBAC API fallback)
  - IContainerAuthorizationService (org-scoped resource access)
  - IMemoryCache for permission caching

Permission model (Permissions.cs):
  - container:read, container:create, container:manage, container:delete
  - template:read, template:create, template:publish, template:manage
  - provider:read, provider:manage
  - image:read, image:create, image:publish, image:delete, image:build
  - api-key:manage, api-key:admin

OrgRoles (Admin, Editor, Viewer) with permission mappings

ICurrentUserService:
  - GetUserId(), IsAdmin() from ClaimsPrincipal
```

**Exit Criteria:** Unauthenticated requests return 401. Missing permissions return 403. Organization-scoped access enforced via membership checks.

### Phase 8: Web Frontend
**Goal:** Angular 18 SPA with full container management.

```
Angular 18 standalone components + Tailwind CSS:
  - DashboardComponent (overview, stats, recent containers)
  - ContainerListComponent (filterable table with status badges)
  - ContainerDetailComponent (info, stats, events, git repos)
  - ContainerCreateComponent (template + provider selection, code assistant config)
  - ContainerTerminalComponent (xterm.js with WebGL renderer)
    - 18 terminal themes
    - tmux session persistence
    - Resize events via WebSocket
    - UTF-8 / xterm-256color support
  - ContainerThumbnailComponent (ANSI text screenshot preview)
  - ContainerStatsBarComponent (CPU/RAM/disk bars)
  - TemplateListComponent, TemplateDetailComponent
  - ProviderListComponent
  - WorkspaceListComponent, WorkspaceDetailComponent, WorkspaceCreateComponent
  - SettingsComponent
  - LoginComponent, CallbackComponent (OIDC flow)

Shared components:
  - StatusBadgeComponent (Pending/Creating/Running/Stopped/Failed/Destroyed)
  - YamlEditorComponent (CodeMirror 6 with YAML language support)
  - UptimePipe (human-readable duration)

Auth integration:
  - angular-auth-oidc-client for OIDC/PKCE
  - HTTP interceptor for Bearer token injection
```

**Exit Criteria:** User can log in, browse templates, create containers, connect via terminal, view stats, and manage workspaces.

### Phase 9: Git & Image Management
**Goal:** Multi-repo cloning, credential management, and content-addressed image versioning.

```
Git Clone:
  - ContainerGitRepository entity (URL, branch, targetPath, cloneStatus)
  - GitCloneService (clone + pull via provider exec)
  - GitRepositoryProbeService (validate URLs + credentials before clone)
  - GitRepositoryValidator (URL format, branch name validation)
  - Per-repo status tracking (Pending, Cloning, Completed, Failed)
  - Template-defined repos auto-cloned on provisioning

Git Credentials:
  - GitCredential entity (label, encrypted token, gitHost matching)
  - GitCredentialService (create, list, resolve, delete)
  - DataProtection-based encryption at rest
  - Auto-match credentials by git host

Image Management:
  - ContainerImage entity (content hash, tag, build number, build status)
  - Content-addressed hashing (SHA-256 of image contents)
  - ImageManifestService (introspect installed tools, OS packages, base image)
  - ImageDiffService (compare two images: tool changes, package deltas, severity)
  - ImageToolManifest (tools, OS info, architecture, packages)
  - ToolVersionDetector + ToolRegistry (detect installed tool versions)
  - Organization-scoped builds with visibility control
  - Build changelog tracking
```

**Exit Criteria:** Containers clone multiple repos with per-repo status. Images have content hashes and introspection manifests. Image diffs show tool changes with severity.

### Phase 10: VNC Desktop
**Goal:** Graphical desktop environment accessible via web browser.

```
VNC Desktop templates (4 variants):
  - dotnet-8-desktop (Ubuntu 24.04 + XFCE4 + TigerVNC + noVNC)
  - dotnet-8-alpine-desktop (Alpine + XFCE4 + TigerVNC + noVNC)
  - dotnet-10-alpine-desktop (Alpine + .NET 10 + XFCE4 + TigerVNC + noVNC)
  - python-3.12-desktop (Ubuntu 24.04 + Python 3.12 + XFCE4 + TigerVNC + noVNC)
VNC server configuration (TigerVNC with HTTPS via self-signed certs)
noVNC web client embedded as iframe in Angular UI
VNC endpoint exposed via container connection info
```

**Exit Criteria:** Desktop templates provide a full XFCE4 graphical environment accessible via noVNC in the web UI.

### Phase 11: Non-Root Containers
**Goal:** Containers run as non-root user derived from authenticated user's JWT claims.

```
Non-root user provisioning:
  - Extract username from JWT preferred_username or email claim
  - Create Linux user during container provisioning
  - Configure home directory, file ownership, and shell profile
  - Run all processes under non-root user
  - Code assistant and API key injection into user environment
```

**Exit Criteria:** Containers do not run as root. User is created from JWT claims during provisioning.

### Phase 12: Image Build Tracking
**Goal:** Track custom image build status and trigger rebuilds from UI.

```
ImageBuildWorker (BackgroundService):
  - Monitor build processes (Pending, Building, Completed, Failed)
  - Trigger rebuilds from web UI
  - Update content hashes and metadata on completion
  - Build status visible in frontend image management views
```

**Exit Criteria:** Image builds can be triggered from the UI. Build status is tracked and displayed in real time.

## 2. Key Implementation Details

### 2.1 Program.cs Setup

The API server uses top-level statements with the following DI registration order:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Logging — Serilog with optional OTLP sink
builder.Host.UseSerilog((context, config) => {
    config.WriteTo.Console();
    if (!string.IsNullOrEmpty(otlpEndpoint))
        config.WriteTo.OpenTelemetry(o => { o.Endpoint = otlpEndpoint; });
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Controllers (JSON with enum-as-string, ignore cycles)
builder.Services.AddControllers().AddJsonOptions(o => {
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

// Database
builder.Services.AddDbContext<ContainersDbContext>(options =>
    options.UseNpgsql(connectionString));

// Application services (scoped)
builder.Services.AddScoped<IContainerService, ContainerOrchestrationService>();
builder.Services.AddScoped<IInfrastructureRoutingService, InfrastructureRoutingService>();
builder.Services.AddScoped<IGitCredentialService, GitCredentialService>();
builder.Services.AddScoped<IGitCloneService, GitCloneService>();
builder.Services.AddScoped<IGitRepositoryProbeService, GitRepositoryProbeService>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<IApiKeyValidationService, ApiKeyValidationService>();
builder.Services.AddScoped<IToolVersionDetector, ToolVersionDetector>();
builder.Services.AddScoped<IImageManifestService, ImageManifestService>();
builder.Services.AddScoped<IImageDiffService, ImageDiffService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IOrganizationMembershipService, OrganizationMembershipService>();
builder.Services.AddScoped<IContainerAuthorizationService, ContainerAuthorizationService>();

// Singletons
builder.Services.AddSingleton<IInfrastructureProviderFactory, InfrastructureProviderFactory>();
builder.Services.AddSingleton<ICostEstimationService, CostEstimationService>();
builder.Services.AddSingleton<IYamlTemplateParser, YamlTemplateParser>();
builder.Services.AddSingleton<ICodeAssistantInstallService, CodeAssistantInstallService>();

// Background workers
builder.Services.AddSingleton<ContainerProvisioningQueue>();
builder.Services.AddHostedService<ContainerProvisioningWorker>();
builder.Services.AddHostedService<ProviderHealthCheckWorker>();
builder.Services.AddHostedService<ContainerStatusSyncWorker>();
builder.Services.AddHostedService<ContainerScreenshotWorker>();
builder.Services.AddHostedService<ImageBuildWorker>();

// MCP Server
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

// Authentication (JWT Bearer with dev fallback)
// RBAC Client (Andy.Rbac.Client with RequirePermission attribute)
// CORS, Health Checks, OpenTelemetry, MemoryCache, DataProtection

var app = builder.Build();

// Auto-create DB and seed data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ContainersDbContext>();
    await db.Database.EnsureCreatedAsync();
    await DataSeeder.SeedAsync(db);
}

// Middleware pipeline
app.UseHttpsRedirection();
app.UseSwagger();            // Development only
app.UseCors();
app.MapMcp("/mcp");
app.UseAuthentication();
app.Use(/* dev identity middleware */);  // Development only
app.UseWebSockets();
app.UseAuthorization();
app.MapControllers().RequireAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();
app.Run();
```

### 2.2 Background Workers

Five `BackgroundService` workers run concurrently:

**ContainerProvisioningWorker** (Channel-based queue):
- Reads from `ContainerProvisioningQueue` (bounded `Channel<ContainerProvisionJob>`)
- On startup, recovers containers stuck in Creating/Pending for 30+ minutes
- For each job: create on provider -> run post-create scripts -> inject env vars -> install code assistant -> clone git repos -> set Running
- 5-minute timeout per provision via `CancellationTokenSource.CreateLinkedTokenSource`
- Emits OpenTelemetry spans (`Andy.Containers.Provisioning`) and metrics (`ProvisioningDuration`, `ProvisioningErrors`)

**ProviderHealthCheckWorker** (PeriodicTimer):
- Configurable interval via `HealthCheck:IntervalSeconds` (default: 60s)
- 5-second startup delay, then immediate first check
- 30-second timeout per individual provider health check
- Updates `ProviderHealth` status (Healthy, Degraded, Unreachable) on `InfrastructureProvider` entity
- Logs status transitions

**ContainerStatusSyncWorker** (PeriodicTimer):
- Configurable interval via `ContainerSync:IntervalSeconds` (default: 15s)
- Syncs Running/Stopped/Creating containers against provider state
- Skips Creating -> Running transitions (deferred to provisioning worker)
- Detects destroyed containers ("not found" / "no such" errors)
- 10-second timeout per container info call

**ContainerScreenshotWorker** (PeriodicTimer):
- Configurable interval via `Screenshot:IntervalSeconds` (default: 30s)
- Captures tmux pane text via `tmux capture-pane -p -t web -S -40`
- Stores ANSI text in `ContainerMetadata.Screenshot` (JSON in container Metadata column)
- Max 20 containers per cycle with 500ms delay between containers
- 10-second exec timeout

**ImageBuildWorker** (PeriodicTimer):
- Monitors custom image build processes
- Tracks build status: Pending, Building, Completed, Failed
- Triggers rebuilds when requested from the UI
- Updates image metadata and content hashes on completion

### 2.3 Non-Root Container Execution

Containers run as a non-root user derived from the authenticated user's JWT claims:
- Username is extracted from `preferred_username` or `email` claim during provisioning
- A Linux user is created inside the container with appropriate permissions
- The home directory, file ownership, and environment are configured for this user
- Code assistant configuration and API keys are injected into the user's shell profile

### 2.4 Infrastructure Provider Pattern

All providers implement `IInfrastructureProvider`:

```csharp
public interface IInfrastructureProvider
{
    ProviderType Type { get; }
    Task<ProviderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);
    Task<ProviderHealth> HealthCheckAsync(CancellationToken ct = default);
    Task<ContainerProvisionResult> CreateContainerAsync(ContainerSpec spec, CancellationToken ct = default);
    Task StartContainerAsync(string externalId, CancellationToken ct = default);
    Task StopContainerAsync(string externalId, CancellationToken ct = default);
    Task DestroyContainerAsync(string externalId, CancellationToken ct = default);
    Task<ContainerRuntimeInfo> GetContainerInfoAsync(string externalId, CancellationToken ct = default);
    Task<ContainerProvisionResult> ResizeContainerAsync(string externalId, ResourceSpec resources, CancellationToken ct = default);
    Task<ConnectionInfo> GetConnectionInfoAsync(string externalId, CancellationToken ct = default);
    Task<ContainerStats> GetContainerStatsAsync(string externalId, CancellationToken ct = default);
    Task<ExecResult> ExecAsync(string externalId, string command, CancellationToken ct = default);
    Task<ExecResult> ExecAsync(string externalId, string command, TimeSpan timeout, CancellationToken ct = default);
}
```

`InfrastructureProviderFactory` maps `ProviderType` to concrete implementations:

| ProviderType | Implementation | SDK |
|--------------|----------------|-----|
| Docker | DockerInfrastructureProvider | Docker.DotNet |
| AppleContainer | AppleContainerProvider | macOS `container` CLI |
| AzureAci | AzureAciProvider | Azure.ResourceManager.ContainerInstance |
| GcpCloudRun | GcpCloudRunProvider | Google.Cloud.Run.V2 |
| AwsFargate | AwsFargateProvider | AWSSDK.ECS + AWSSDK.EC2 |
| FlyIo | FlyIoProvider | REST API |
| Hetzner | HetznerCloudProvider | REST API |
| DigitalOcean | DigitalOceanProvider | REST API |
| Civo | CivoProvider | REST API |

`InfrastructureRoutingService` auto-selects the best provider based on container specs, GPU requirements, cost estimation, and health status.

### 2.5 Terminal WebSocket

The `TerminalController` provides a WebSocket-based terminal at `GET /api/containers/{id}/terminal`:

1. Client connects via WebSocket
2. Client sends initial terminal size as JSON: `{"cols":120,"rows":40}`
3. Server spawns `script -q /dev/null bash -c '...'` wrapping `docker exec -it` or `container exec`
4. Inside the container: tmux session (`web`) with `xterm-256color`
   - New session: `tmux new-session -s web -x {cols} -y {rows}`
   - Reattach: `tmux resize-window` then `tmux attach -d` (fixes stale dimensions)
5. Three relay tasks run concurrently:
   - Process stdout -> WebSocket (binary frames, 4KB buffer)
   - Process stderr -> WebSocket (binary frames)
   - WebSocket -> Process stdin (raw bytes, no text encoding)
6. Client resize messages (`{"type":"resize","cols":N,"rows":N}`) are intercepted
7. On disconnect, the process tree is killed and WebSocket is closed cleanly

PTY sizing is critical: both the outer `script` PTY and inner container exec PTY are sized via `stty rows N cols N` to prevent line-wrapping corruption at column 80.

### 2.6 MCP Tool Implementation

Tools use the `[McpServerToolType]` / `[McpServerTool]` attribute pattern:

```csharp
[McpServerToolType]
public class ContainersMcpTools
{
    // DI-injected: ContainersDbContext, IContainerService, IGitCloneService,
    // IGitCredentialService, IImageManifestService, IImageDiffService,
    // ICurrentUserService, IOrganizationMembershipService, IApiKeyService, etc.

    [McpServerTool, Description("List all containers with their status")]
    public async Task<IReadOnlyList<McpContainerInfo>> ListContainers(
        [Description("Filter by status")] string? status = null,
        [Description("Filter by organization ID")] string? organizationId = null)
    {
        // Organization membership check before returning data
        // Returns typed records, not raw entities
    }

    [McpServerTool, Description("Create a new development container")]
    public async Task<string> CreateContainer(
        [Description("Container name")] string name,
        [Description("Template code")] string templateCode,
        [Description("Code assistant tool")] string? codeAssistant = null,
        [Description("LLM model name")] string? model = null,
        [Description("API base URL")] string? baseUrl = null)
    { ... }

    // 22 tools total -- all enforce organization membership
}
```

All MCP tools enforce organization-level access via `IOrganizationMembershipService.IsMemberAsync()` and `ICurrentUserService.IsAdmin()`. Return types use dedicated MCP record types (e.g., `McpContainerInfo`, `McpTemplateInfo`) rather than domain entities.

### 2.7 RBAC Integration

Per-endpoint permission enforcement uses the `[RequirePermission]` attribute from `Andy.Rbac.Authorization`:

```csharp
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ContainersController : ControllerBase
{
    [HttpGet]
    [RequirePermission("container:read")]
    public async Task<IActionResult> List(...) { ... }

    [HttpPost]
    [RequirePermission("container:create")]
    public async Task<IActionResult> Create(...) { ... }
}
```

63 `[RequirePermission]` usages across 9 controllers.

When RBAC is unavailable, `AllowAllPolicyProvider` returns a permissive policy for any policy name:

```csharp
public class AllowAllPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        => Task.FromResult<AuthorizationPolicy?>(AllowAll);
}
```

Organization membership uses a two-tier lookup: JWT claims first, then RBAC API fallback with `IMemoryCache` caching.

## 3. NuGet Package Reference

### 3.1 Andy.Containers.Api

```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
<PackageReference Include="Grpc.AspNetCore" />
<PackageReference Include="Google.Protobuf" />
<PackageReference Include="Grpc.Tools" />
<PackageReference Include="Swashbuckle.AspNetCore" />
<PackageReference Include="YamlDotNet" />
<PackageReference Include="Serilog.AspNetCore" />
<PackageReference Include="Serilog.Sinks.Console" />
<PackageReference Include="Serilog.Sinks.OpenTelemetry" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" />
<PackageReference Include="OpenTelemetry.Instrumentation.GrpcNetClient" />
<PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
<PackageReference Include="OpenTelemetry.Exporter.Console" />
<PackageReference Include="ModelContextProtocol" />
<PackageReference Include="ModelContextProtocol.AspNetCore" />
<PackageReference Include="Andy.Auth" />
<PackageReference Include="Andy.Rbac.Client" />
```

### 3.2 Andy.Containers.Infrastructure

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
<PackageReference Include="Docker.DotNet" />
<PackageReference Include="Azure.ResourceManager.ContainerInstance" />
<PackageReference Include="Azure.ResourceManager.AppContainers" />
<PackageReference Include="Azure.Identity" />
<PackageReference Include="SSH.NET" />
<PackageReference Include="Google.Cloud.Run.V2" />
<PackageReference Include="AWSSDK.ECS" />
<PackageReference Include="AWSSDK.EC2" />
```

### 3.3 Andy.Containers (Core)

```xml
<FrameworkReference Include="Microsoft.AspNetCore.App" />
<PackageReference Include="Microsoft.Extensions.Caching.Memory" />
<PackageReference Include="Microsoft.Extensions.Http" />
```

### 3.4 Andy.Containers.Client

```xml
<PackageReference Include="Grpc.Net.Client" />
<PackageReference Include="Google.Protobuf" />
<PackageReference Include="Grpc.Tools" />
<PackageReference Include="Microsoft.Extensions.Http.Polly" />
```

### 3.5 Andy.Containers.Cli

```xml
<PackageReference Include="System.CommandLine" />
<PackageReference Include="Spectre.Console" />
```

### 3.6 Test Projects

```xml
<PackageReference Include="Microsoft.NET.Test.Sdk" />
<PackageReference Include="xunit" />
<PackageReference Include="xunit.runner.visualstudio" />
<PackageReference Include="coverlet.collector" />
<PackageReference Include="Moq" />
<PackageReference Include="FluentAssertions" />
<!-- API tests additionally: -->
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" />
```

### 3.7 Angular Client

```json
{
  "dependencies": {
    "@angular/core": "^18.2.0",
    "@angular/forms": "^18.2.0",
    "@angular/router": "^18.2.0",
    "@xterm/xterm": "^6.0.0",
    "@xterm/addon-fit": "^0.11.0",
    "@xterm/addon-webgl": "^0.19.0",
    "@xterm/addon-web-links": "^0.12.0",
    "@codemirror/lang-yaml": "^6.1.2",
    "@codemirror/theme-one-dark": "^6.1.3",
    "angular-auth-oidc-client": "^18.0.2",
    "tailwindcss": "^3.4.19",
    "rxjs": "~7.8.0"
  }
}
```

## 4. Database Schema

EF Core code-first with PostgreSQL. Key entities and their configurations:

### 4.1 Core Entities

| Entity | Key Columns | JSONB Columns | Indexes |
|--------|-------------|---------------|---------|
| **Container** | Id, Name, OwnerId, Status, ExternalId, TemplateId, ProviderId, OrganizationId | AllocatedResources, NetworkConfig, GitRepository, EnvironmentVariables, CodeAssistant, Metadata | OwnerId, Status, OrganizationId |
| **ContainerTemplate** | Id, Code (unique), Name, Version, CatalogScope | Toolchains, DefaultResources, EnvironmentVariables, Ports, Scripts, GitRepositories, CodeAssistant, Metadata | Code (unique), CatalogScope |
| **ContainerImage** | Id, ContentHash (unique), Tag, BuildNumber, BuildStatus, TemplateId, OrganizationId | DependencyManifest, DependencyLock, Metadata | ContentHash (unique), Tag, TemplateId+OrganizationId, OrganizationId |
| **InfrastructureProvider** | Id, Code (unique), Name, Type, IsEnabled, HealthStatus, Region | ConnectionConfig, Capabilities, Metadata | Code (unique) |
| **Workspace** | Id, Name, OwnerId, OrganizationId, Status, DefaultContainerId | GitRepositories, Configuration, Metadata | OwnerId, OrganizationId |

### 4.2 Supporting Entities

| Entity | Key Columns | Indexes |
|--------|-------------|---------|
| **ContainerSession** | Id, ContainerId, SubjectId | ContainerId, SubjectId |
| **ContainerEvent** | Id, ContainerId, EventType, Timestamp | ContainerId, Timestamp |
| **DependencySpec** | Id, TemplateId, Name | TemplateId+Name (unique) |
| **ResolvedDependency** | Id, ImageId, DependencySpecId | ImageId |
| **ContainerGitRepository** | Id, ContainerId, Url, Branch, TargetPath, CloneStatus | ContainerId, CloneStatus |
| **GitCredential** | Id, OwnerId, Label | OwnerId, OwnerId+Label (unique) |
| **ApiKeyCredential** | Id, OwnerId, Provider, Label, EnvVarName, IsValid | OwnerId, OwnerId+Provider+Label (unique) |

### 4.3 Relationships

```
Container ──▶ ContainerTemplate (FK: TemplateId)
Container ──▶ InfrastructureProvider (FK: ProviderId)
Container ──◀ ContainerSession (1:many)
Container ──◀ ContainerEvent (1:many)
Container ──◀ ContainerGitRepository (1:many)
ContainerImage ──▶ ContainerTemplate (FK: TemplateId)
ContainerImage ──▶ ContainerImage (FK: PreviousImageId, self-ref)
DependencySpec ──▶ ContainerTemplate (FK: TemplateId)
ResolvedDependency ──▶ ContainerImage (FK: ImageId)
ResolvedDependency ──▶ DependencySpec (FK: DependencySpecId)
ContainerTemplate ──▶ ContainerTemplate (FK: ParentTemplateId, self-ref)
Workspace ──▶ Container (FK: DefaultContainerId)
Workspace ──◀▶ Container (many:many)
```

## 5. Testing Conventions

### 5.1 Framework

xUnit + FluentAssertions + Moq. Test projects:

| Project | Focus | Key Tests |
|---------|-------|-----------|
| Andy.Containers.Tests | Core library (models, validators, tools) | Model behavior, git URL validation, version parsing, content hash calculation |
| Andy.Containers.Api.Tests | API layer (controllers, services, workers, MCP) | Controller endpoints, orchestration service, provisioning worker, health check worker, MCP tools |
| Andy.Containers.Client.Tests | Client library | gRPC/HTTP client behavior |
| Andy.Containers.Integration.Tests | Live provider tests | DockerProviderTests, AppleContainerProviderTests |

### 5.2 Test Naming

```
MethodUnderTest_StateOrInput_ExpectedResult
```

Examples:
- `List_WhenContainersExist_ReturnsOkWithFilteredResults`
- `CreateContainer_WithInvalidTemplate_Returns404`
- `HealthCheck_WhenProviderTimesOut_SetsUnreachable`
- `GenerateInstallScript_ClaudeCode_InstallsViaNpm`

### 5.3 Test Infrastructure

```csharp
// Reusable InMemory DB factory
public static class InMemoryDbHelper
{
    public static ContainersDbContext CreateContext(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<ContainersDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;
        var context = new ContainersDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    public static IServiceScopeFactory CreateScopeFactory(ContainersDbContext db)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton<ContainersDbContext>(_ => db)
            .BuildServiceProvider();
        return serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }
}
```

### 5.4 Test Organization

```
tests/
├── Andy.Containers.Tests/
│   ├── Models/
│   │   ├── ContainerTests.cs
│   │   ├── ContainerImageTests.cs
│   │   ├── ContainerTemplateTests.cs
│   │   ├── ContainerGitRepositoryTests.cs
│   │   ├── DependencySpecTests.cs
│   │   ├── InfrastructureProviderTests.cs
│   │   ├── ImageToolManifestTests.cs
│   │   ├── ContainerStatsTests.cs
│   │   └── PermissionsTests.cs
│   ├── GitRepositoryValidatorTests.cs
│   ├── VersionParserTests.cs
│   ├── VersionConstraintMatcherTests.cs
│   ├── ContentHashCalculatorTests.cs
│   ├── IntrospectionScriptBuilderTests.cs
│   ├── ToolRegistryTests.cs
│   └── ContainersClientTests.cs
│
├── Andy.Containers.Api.Tests/
│   ├── Controllers/
│   │   ├── ContainersControllerTests.cs
│   │   ├── ContainersControllerGitTests.cs
│   │   ├── TemplatesControllerTests.cs
│   │   ├── TemplatesControllerYamlTests.cs
│   │   ├── ProvidersControllerTests.cs
│   │   ├── WorkspacesControllerTests.cs
│   │   ├── ImagesControllerTests.cs
│   │   ├── GitCredentialsControllerTests.cs
│   │   ├── OrganizationsControllerTests.cs
│   │   └── ApiKeysControllerTests.cs
│   ├── Services/
│   │   ├── ContainerOrchestrationServiceTests.cs
│   │   ├── ContainerProvisioningWorkerTests.cs
│   │   ├── ContainerStatusSyncWorkerTests.cs
│   │   ├── ProviderHealthCheckWorkerTests.cs
│   │   ├── InfrastructureRoutingServiceTests.cs
│   │   ├── GitCloneServiceTests.cs
│   │   ├── GitCredentialServiceTests.cs
│   │   ├── GitRepositoryProbeServiceTests.cs
│   │   ├── ImageManifestServiceTests.cs
│   │   ├── ImageDiffServiceTests.cs
│   │   ├── ToolVersionDetectorTests.cs
│   │   ├── YamlTemplateParserTests.cs
│   │   ├── ContainerAuthorizationServiceTests.cs
│   │   ├── OrganizationMembershipServiceTests.cs
│   │   ├── AllowAllPolicyProviderTests.cs
│   │   ├── ApiKeyServiceTests.cs
│   │   ├── CodeAssistantInstallServiceTests.cs
│   │   └── ContainerGrpcServiceTests.cs
│   ├── Mcp/
│   │   ├── ContainersMcpToolsTests.cs
│   │   ├── ContainersMcpToolsGitTests.cs
│   │   └── ContainersMcpToolsImageTests.cs
│   ├── Telemetry/
│   │   ├── ActivitySourcesTests.cs
│   │   ├── MetersTests.cs
│   │   └── OpenTelemetryExtensionsTests.cs
│   ├── Data/
│   │   └── DataSeederTests.cs
│   └── Helpers/
│       └── InMemoryDbHelper.cs
│
├── Andy.Containers.Client.Tests/
│   └── ContainersClientTests.cs
│
└── Andy.Containers.Integration.Tests/
    ├── DockerProviderTests.cs
    └── AppleContainerProviderTests.cs
```

## 6. Configuration Reference

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5434;Database=andy_containers;Username=postgres;Password=postgres"
  },
  "AndyAuth": {
    "Authority": "",
    "Audience": "urn:andy-containers-api",
    "DevUserId": "dev-user",
    "DevEmail": "dev@andy.local"
  },
  "Rbac": {
    "ApiBaseUrl": "https://localhost:7003",
    "ApplicationCode": "containers"
  },
  "Cors": {
    "Origins": [
      "https://localhost:5280",
      "https://localhost:4200",
      "https://localhost:3000"
    ]
  },
  "OpenTelemetry": {
    "ServiceName": "andy-containers-api",
    "OtlpEndpoint": ""
  },
  "HealthCheck": {
    "IntervalSeconds": 60
  },
  "ContainerSync": {
    "IntervalSeconds": 15
  },
  "Screenshot": {
    "IntervalSeconds": 30
  },
  "CodeAssistant": {
    "DefaultLlmBaseUrl": "https://api.openai.com/v1",
    "DefaultLlmModel": "gpt-4o-mini",
    "DefaultEmbeddingBaseUrl": "https://api.openai.com/v1",
    "DefaultEmbeddingModel": "text-embedding-3-small"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    }
  }
}
```

### Environment Variable Overrides

All configuration uses the standard ASP.NET Core `__` separator:

```bash
ConnectionStrings__DefaultConnection=Host=...
AndyAuth__Authority=https://auth.example.com
AndyAuth__Audience=urn:andy-containers-api
Rbac__ApiBaseUrl=https://rbac.example.com
Rbac__ApplicationCode=containers
OpenTelemetry__OtlpEndpoint=http://collector:4317
HealthCheck__IntervalSeconds=120
ContainerSync__IntervalSeconds=30
Screenshot__IntervalSeconds=60
CodeAssistant__DefaultLlmModel=claude-sonnet-4-20250514
Cors__Origins__0=https://app.example.com
```

## 7. Docker Setup

### Multi-Stage Build

The Dockerfile (`src/Andy.Containers.Api/Dockerfile`) uses a two-stage build:

**Build stage** (`mcr.microsoft.com/dotnet/sdk:8.0`):
- Copies corporate root CAs from `certs/` directory and runs `update-ca-certificates`
- Sets SSL/NuGet environment variables for corporate proxy environments
- Layer-cached restore: copies only `.csproj` + `Directory.Build.props` + `Directory.Packages.props` + `proto/`
- Full publish: `dotnet publish -c Release -o /app/publish`

**Runtime stage** (`mcr.microsoft.com/dotnet/aspnet:8.0`):
- Installs `ca-certificates`, `curl`, `openssl`
- Generates a self-signed dev certificate at build time (`/https/aspnetapp.pfx`)
- Entrypoint script trusts custom CAs mounted at `/usr/local/share/ca-certificates/custom/`
- Exposes port 8080

### docker-compose Services

```yaml
services:
  postgres:          # PostgreSQL 16 Alpine, port 5434:5432
  api:               # Andy Containers API, ports 5200:8443 (HTTPS), 5201:8080 (HTTP)
  web:               # Angular SPA (nginx), port 4200:80
```

Key docker-compose features:
- `extra_hosts: host.docker.internal:host-gateway` for reaching host-running Andy Auth/RBAC
- Health check on postgres via `pg_isready`
- Health check on API via `curl -fk https://localhost:8443/health`
- Cert volume mount: `./certs:/usr/local/share/ca-certificates/custom:ro`
- API depends on postgres `service_healthy` condition

### OpenTelemetry

Five `ActivitySource` instances for distributed tracing:
- `Andy.Containers.Provisioning` -- container creation spans
- `Andy.Containers.Introspection` -- image manifest generation
- `Andy.Containers.Git` -- clone/pull operations
- `Andy.Containers.Infrastructure` -- provider resolution
- `Andy.Containers.ApiKeys` -- key creation/validation

One `Meter` (`Andy.Containers`) with 11 counters and 4 histograms tracking containers created/deleted, git clones, provisioning duration/errors, health checks, and API key operations.
