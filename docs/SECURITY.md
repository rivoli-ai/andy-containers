# Andy Containers - Security

## 1. Authentication

### 1.1 Overview

Andy Containers uses **OAuth 2.0 / OpenID Connect** via Andy Auth for authentication. The API validates JWT Bearer tokens issued by the Andy Auth server. The Angular frontend implements Authorization Code flow with PKCE.

### 1.2 Backend Configuration

```json
{
  "AndyAuth": {
    "Authority": "https://localhost:5001",
    "Audience": "urn:andy-containers-api"
  }
}
```

When `AndyAuth:Authority` is empty, the API runs in **dev mode** with a permissive authorization policy that allows all requests. A dev identity is injected for unauthenticated requests with claims: `sub=dev-user`, `email=dev@andy.local`, `role=admin`.

### 1.3 Client Registration in Andy Auth

The `andy-containers-web` client is registered in Andy Auth's `DbSeeder.cs`:

```
Client ID:      andy-containers-web
Client Type:    Public (no secret - SPA)
Consent Type:   Implicit (no consent screen)
Grant Types:    authorization_code, refresh_token
Scopes:         openid, profile, email, roles, urn:andy-containers-api, offline_access
Redirect URIs:  https://localhost:4200/callback
Post-Logout:    https://localhost:4200/
```

The `urn:andy-containers-api` scope is registered as an OpenIddict scope resource, so tokens issued with this scope include it as an audience claim.

### 1.4 Frontend Auth Flow

```
User (Browser)
    |
    v
[Click "Sign In" at /login]
    |
    v
AuthService.signIn()
  - Generate PKCE: code_verifier (64 bytes) + code_challenge (SHA-256)
  - Generate state (CSRF token)
  - Redirect to: {authority}/connect/authorize?
    client_id=andy-containers-web
    &redirect_uri=https://localhost:4200/callback
    &response_type=code
    &scope=openid profile email urn:andy-containers-api offline_access
    &code_challenge={S256}
    &state={random}
    |
    v
[Andy Auth authenticates user]
    |
    v
Redirect to: /callback?code=...&state=...
    |
    v
CallbackComponent
  - Validate state (CSRF protection)
  - Exchange: POST /connect/token with code + code_verifier
  - Store tokens in localStorage
  - Redirect to originally requested page
```

### 1.5 Token Storage

| Key | Storage | Purpose |
|-----|---------|---------|
| auth_access_token | localStorage | API authorization (Bearer header) |
| auth_id_token | localStorage | User identity claims |
| auth_refresh_token | localStorage | Silent token renewal |
| auth_token_expiry | localStorage | Expiry timestamp for proactive refresh |

## 2. Authorization (RBAC)

### 2.1 Overview

Andy Containers uses Andy RBAC for role-based access control. The `Andy.Rbac.Client` NuGet package provides declarative `[RequirePermission]` attributes on controller actions.

### 2.2 Configuration

```json
{
  "Rbac": {
    "ApiBaseUrl": "https://localhost:7003",
    "ApplicationCode": "containers"
  }
}
```

When `Rbac:ApiBaseUrl` is empty, RBAC checks are not enforced (dev fallback).

### 2.3 Permission Model

Application code: `containers`

| Resource Type | Actions | Supports Instances | Description |
|---|---|---|---|
| container | read, write, delete, execute | Yes | Container lifecycle, exec, resize |
| template | read, write, delete | No | Template catalog |
| workspace | read, write, delete | No | Workspace management |
| provider | read, admin | No | Infrastructure providers |
| settings | read, write | No | API keys, monitoring config |
| image | read, write, delete | No | Container images |

### 2.4 Roles

| Role | Permissions | Use Case |
|------|------------|----------|
| admin | All ~15 permissions | Full platform access |
| user | read + container:write/execute + workspace:write + settings:write | Use containers, can't manage templates/providers |

### 2.5 Controller Permission Mapping

| Controller | Action | Permission |
|------------|--------|------------|
| ContainersController | List, Get, Stats, Events, Connection, Repos | `container:read` |
| ContainersController | Create, AddRepository | `container:write` |
| ContainersController | Start, Stop, Exec, Resize, Pull | `container:execute` |
| ContainersController | Destroy | `container:delete` |
| TemplatesController | Browse, Get, Definition | `template:read` |
| TemplatesController | Create, Update, Publish, FromYaml | `template:write` |
| TemplatesController | Delete | `template:delete` |
| WorkspacesController | List, Get | `workspace:read` |
| WorkspacesController | Create, Update | `workspace:write` |
| WorkspacesController | Delete | `workspace:delete` |
| ProvidersController | List, Get, Health, CostEstimate | `provider:read` |
| ProvidersController | Create, Delete | `provider:admin` |
| ApiKeysController | List, Get, History | `settings:read` |
| ApiKeysController | Create, Update, Delete, Validate | `settings:write` |

### 2.6 Authorization Flow

```
HTTP Request with JWT
    |
    v
[JWT Validation] -- 401 --> Unauthorized
    | valid
    v
[Extract SubjectId from "sub" claim]
    |
    v
[RequirePermission("resource:action")]
    |
    v
[IRbacClient.HasPermissionAsync(subjectId, "containers:resource:action")]
    |
    +-- Check in-memory cache (5-min TTL)
    +-- Cache miss: HTTP call to RBAC server, cache result
    |
    +-- allowed --> 200 OK
    +-- denied  --> 403 Forbidden
```

## 3. Development Setup

### 3.1 Prerequisites

- Andy Auth running on `https://localhost:5001`
- Andy RBAC running on `https://localhost:7003`

### 3.2 Andy Auth Setup

1. Start Andy Auth: `dotnet run --project src/Andy.Auth.Server --urls "https://localhost:5001"` (in andy-auth directory)
2. The `DbSeeder` automatically registers the `andy-containers-web` client and `urn:andy-containers-api` scope
3. Create a user at `https://localhost:5001/register` if needed

### 3.3 RBAC Setup

Run the seed script against the Andy RBAC database:

```bash
docker exec andy-rbac-db psql -U postgres -d andy_rbac -f /path/to/scripts/rbac-seed.sql
```

Or execute each SQL statement from `scripts/rbac-seed.sql` manually.

Then assign the admin role to your user (see Step 6 in the SQL script for instructions).

### 3.4 Required Services

Both Andy Auth and Andy RBAC must be running. The API will fail to start if `Rbac:ApiBaseUrl` is not configured. There is no dev-mode bypass for RBAC — permissions are always enforced.

The authentication layer has a dev fallback: when `AndyAuth:Authority` is empty, unauthenticated requests get a dev identity (`dev-user`, admin role). However, RBAC still validates permissions against the RBAC server for that identity.

## 4. API Key Management

### 4.1 Per-User API Keys

Users store API keys for AI code assistants (Anthropic, OpenAI, OpenRouter, Ollama, custom OpenAI-compatible) via the Settings page. Keys are encrypted using ASP.NET Core Data Protection API.

### 4.2 Supported Providers

| Provider | API Key | Base URL | Notes |
|----------|---------|----------|-------|
| Anthropic | ANTHROPIC_API_KEY | - | Claude models |
| OpenAI | OPENAI_API_KEY | - | GPT models |
| Google | GOOGLE_API_KEY | - | Gemini models |
| Dashscope | DASHSCOPE_API_KEY | - | Qwen models |
| OpenRouter | OPENROUTER_API_KEY | https://openrouter.ai/api/v1 | Multi-model proxy |
| Ollama | (none) | http://host.docker.internal:11434/v1 | Local LLM, no key |
| OpenAI Compatible | OPENAI_API_KEY | Custom | vLLM, LiteLLM, Azure OpenAI |

### 4.3 Key Storage and Injection

- Keys encrypted at rest with ASP.NET Core Data Protection (`IDataProtector`)
- Displayed masked in UI: `****...XXXX` (last 4 characters)
- All changes logged in audit trail (creation, update, validation, usage, deletion)
- Injected into containers as environment variables during provisioning
- Also written to `~/.bashrc` and `~/.profile` for terminal session persistence

## 5. MCP Security

### 5.1 MCP Endpoint

The MCP server is mounted at `/mcp` with HTTP Streamable transport:

```csharp
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
app.MapMcp("/mcp").RequireAuthorization();
```

All MCP tools require authentication via the same JWT Bearer flow as REST endpoints. Each tool has `[RequirePermission]` attributes matching the REST API permissions.

### 5.2 Available MCP Tools

- Container management: list, get, create, start, stop, destroy, exec
- Template catalog: browse, get details
- Provider management: list, health check
- API key management: store, list, validate, delete
- Git credential and repository operations
- Image operations: list, diff, manifest

## 6. Container Access Control

In addition to RBAC permissions, containers enforce ownership:
- Non-admin users can only see/manage their own containers
- Organization membership is checked for org-scoped operations
- Template/image visibility respects catalog scope (Global > Organization > Team > User)
- SSH access uses `root:container` credentials (dev only)

## 7. Certificate Management

### 7.1 Corporate Certificates

The `certs/` directory at the repo root holds corporate root CA certificates:
- **Build time**: Copied into Docker images and trusted via `update-ca-certificates`
- **Runtime**: Mounted as a volume and trusted on container startup via entrypoint script
- Self-signed dev cert generated automatically in Docker builds (no host setup needed)

### 7.2 SSL Environment Variables

All Docker images set these for corporate proxy/SSL compatibility:
- `SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt`
- `DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0`
- `NUGET_CERT_REVOCATION_MODE=off`
- `DOTNET_NUGET_SIGNATURE_VERIFICATION=false`

## 8. Security Checklist

### Development
- [ ] Andy Auth running on https://localhost:5001
- [ ] Andy RBAC running on https://localhost:7003
- [ ] RBAC seed script executed (`scripts/rbac-seed.sql`)
- [ ] User assigned admin role in RBAC database
- [ ] Corporate certs placed in `certs/` (if behind proxy)

### Production
- [ ] Real TLS certificates (not self-signed)
- [ ] `AndyAuth:Authority` set to production auth server
- [ ] `Rbac:ApiBaseUrl` set to production RBAC server
- [ ] API keys encrypted with production-grade Data Protection keys
- [ ] CORS origins restricted to production domains
- [ ] SSH credentials changed from default `root:container`
- [ ] Container expiry policies configured
