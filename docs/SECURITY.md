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
    "ApiBaseUrl": "https://localhost:5300",
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
| provider | read, manage | No | Infrastructure providers |
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
| ProvidersController | Create, Delete | `provider:manage` |
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
- Andy RBAC running on `https://localhost:5300`

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

### 3.4 Dev Mode (No Auth)

To run without auth servers, ensure `AndyAuth:Authority` is empty in `appsettings.json`. The API will accept all requests with a dev identity (`dev-user`, admin role).

## 4. API Key Management

Users can store API keys for AI code assistants (Anthropic, OpenAI, etc.) via the Settings page. Keys are encrypted using ASP.NET Core Data Protection and injected into containers as environment variables at runtime.

## 5. Container Access Control

In addition to RBAC permissions, containers enforce ownership:
- Non-admin users can only see/manage their own containers
- Organization membership is checked for org-scoped operations
- Template/image visibility respects catalog scope (Global > Organization > Team > User)
