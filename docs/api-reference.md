# API Reference

Base URL: `https://localhost:5200/api`

All endpoints require authentication (JWT Bearer token) and RBAC permissions via `[RequirePermission]` attributes.

## Containers

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/containers` | container:read | List containers (supports templateId, workspaceId, status filters) |
| GET | `/containers/{id}` | container:read | Get container details |
| POST | `/containers` | container:write | Create a new container |
| POST | `/containers/{id}/start` | container:execute | Start a stopped container |
| POST | `/containers/{id}/stop` | container:execute | Stop a running container |
| DELETE | `/containers/{id}` | container:delete | Destroy a container |
| POST | `/containers/{id}/exec` | container:exec | Execute a command |
| GET | `/containers/{id}/stats` | container:read | Get CPU/RAM/disk usage |
| PUT | `/containers/{id}/resources` | container:execute | Live resize CPU/memory |
| GET | `/containers/{id}/connection` | container:read | Get IDE/VNC/SSH endpoints |
| GET | `/containers/{id}/screenshot` | container:read | Get terminal thumbnail |
| GET | `/containers/{id}/events` | container:read | Get lifecycle events |
| GET | `/containers/{id}/repositories` | container:read | List cloned git repos |
| POST | `/containers/{id}/repositories` | container:write | Clone a new repository |
| POST | `/containers/{id}/repositories/{repoId}/pull` | container:execute | Pull latest changes |

## Templates

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/templates` | template:read | Browse catalog |
| GET | `/templates/{id}` | template:read | Get template details |
| GET | `/templates/by-code/{code}` | template:read | Get by code |
| GET | `/templates/{id}/definition` | template:read | Get YAML definition |
| POST | `/templates` | template:write | Create template |
| PUT | `/templates/{id}` | template:write | Update template |
| PUT | `/templates/{id}/definition` | template:write | Update YAML definition |
| POST | `/templates/{id}/publish` | template:write | Publish to catalog |
| POST | `/templates/validate` | template:write | Validate YAML |
| POST | `/templates/from-yaml` | template:write | Create from YAML |
| DELETE | `/templates/{id}` | template:delete | Delete template |

## Providers

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/providers` | provider:read | List providers |
| GET | `/providers/{id}` | provider:read | Get provider details |
| GET | `/providers/{id}/health` | provider:read | Check health |
| GET | `/providers/{id}/cost-estimate` | provider:read | Get cost estimate |
| POST | `/providers` | provider:write | Register provider |
| DELETE | `/providers/{id}` | provider:write | Delete provider |

## Workspaces

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/workspaces` | workspace:read | List workspaces |
| GET | `/workspaces/{id}` | workspace:read | Get workspace |
| POST | `/workspaces` | workspace:write | Create workspace |
| PUT | `/workspaces/{id}` | workspace:write | Update workspace |
| DELETE | `/workspaces/{id}` | workspace:delete | Delete workspace |

## API Keys

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/api-keys` | settings:read | List API keys |
| POST | `/api-keys` | settings:write | Create API key |
| PUT | `/api-keys/{id}` | settings:write | Update API key |
| DELETE | `/api-keys/{id}` | settings:write | Delete API key |
| POST | `/api-keys/{id}/validate` | settings:write | Re-validate key |
| GET | `/api-keys/{id}/history` | settings:read | Get audit trail |

## Git Credentials

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/git-credentials` | settings:read | List credentials |
| POST | `/git-credentials` | settings:write | Store credential |
| DELETE | `/git-credentials/{id}` | settings:write | Delete credential |

## Images

### Legacy (template-build-centric)

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| GET | `/images/{templateId}` | template:read | List built images |
| POST | `/images/{templateId}/build` | template:write | Trigger build (returns `BuildHandle` since IM5) |
| GET | `/images/{templateId}/latest` | template:read | Get latest image |
| GET | `/images/diff` | template:read | Compare two images |
| GET | `/images/{imageId}/manifest` | template:read | Get introspection manifest |
| GET | `/images/{imageId}/tools` | template:read | List installed tools |
| GET | `/images/{imageId}/packages` | template:read | List OS packages |
| POST | `/images/{imageId}/introspect` | template:write | Re-run introspection |

### Image Management (Epic IM — digest-anchored)

The endpoints below ship the digest-anchored image identity model introduced in IM3 (`#252`). Same physical bytes produce the same `digest` in every registry; the `references` array on each `BuildArtifact` records every place the bytes have been pushed.

| Method | Endpoint | Permission | Description |
|--------|----------|------------|-------------|
| POST | `/templates/from-yaml` | template:write | Register a YAML template (multipart for spec + files; JSON for spec-only). Idempotent on `specHash`. |
| GET | `/images` | template:read | List `BuildArtifact`s with their references. Filters: `templateId`, `registryId`, `marker`. |
| GET | `/images/by-digest/{digest}` | template:read | Get a single artifact by OCI digest. |
| DELETE | `/images/by-digest/{digest}/references/{referenceId}` | image:admin | Untag one registry coordinate. Idempotent. |
| GET | `/images/build/{buildId}` | template:read | Build status snapshot (`BuildStatus`). |
| GET | `/images/build/{buildId}/events` | template:read | SSE stream of `BuildEvent`s (build progress). |

#### Error mapping

| Status | Code prefix | Meaning |
|---|---|---|
| 400 | `template.spec.invalid` | YAML schema validation failed. |
| 401 | (auth) | Missing or expired JWT. |
| 403 | (rbac) | Caller lacks `template:write` / `image:admin`. |
| 404 | (resource) | Unknown templateId / digest / buildId / referenceId. |
| 409 | `template.code.in-use` | Template `code` is already registered with a different `specHash`. |
| 422 | `template.extends.cycle`, `build.failed` | Cycle in `extends:`, or build engine reported a non-zero exit (logs in `buildLog`). |
| 503 | `build.engine.unavailable` | No build engine on the host (no Docker daemon, no Apple Containers CLI). |
| 507 | `registry.quota.exceeded` | Registry storage quota exhausted. |

## Other Endpoints

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| GET | `/health` | No | Service health check |
| -- | `/mcp` | Yes | MCP tools (HTTP Streamable transport) |
| WS | `/containers/{id}/terminal` | Yes | WebSocket terminal (xterm.js + tmux) |
