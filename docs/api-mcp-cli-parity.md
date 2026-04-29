# API / MCP / CLI parity audit

The platform exposes three transports against the same business surface: REST (`/api/*`), MCP tools, and the `andy-containers-cli` binary. This page is the canonical map of what's covered where, why some gaps are deliberate, and where the open work lives.

Authoritative sources read at audit time:

- REST — every `[Http*]` action across `src/Andy.Containers.Api/Controllers/`
- MCP — every `[McpServerTool(...)]` method in `src/Andy.Containers.Api/Mcp/`
- CLI — every command tree assembled in `src/Andy.Containers.Cli/Program.cs` from `src/Andy.Containers.Cli/Commands/`

Last refreshed: **2026-04-28** ([rivoli-ai/andy-containers#76](https://github.com/rivoli-ai/andy-containers/issues/76)).

## Counts at a glance

- **REST endpoints (business)**: 75
- **MCP coverage**: 14 tools across ~19% of REST endpoints
- **CLI coverage**: 19 commands across ~25% of REST endpoints

Numbers exclude health checks, Swagger, and WebSocket attach surfaces (which don't map to MCP/CLI by design).

## Matrix

✅ present · ❌ missing · — doesn't fit this transport

### Containers

| REST | MCP | CLI |
|---|---|---|
| `GET /api/containers` | `ListContainers` ✅ | `andy-containers-cli list` ✅ |
| `GET /api/containers/{id}` | `GetContainer` ✅ | `andy-containers-cli info {id}` ✅ |
| `POST /api/containers` | ❌ | `andy-containers-cli create` ✅ |
| `POST /api/containers/{id}/start` | ✅ (this PR) | `andy-containers-cli start {id}` ✅ |
| `POST /api/containers/{id}/stop` | ✅ (this PR) | `andy-containers-cli stop {id}` ✅ |
| `DELETE /api/containers/{id}` | ✅ (this PR) | `andy-containers-cli destroy {id}` ✅ |
| `POST /api/containers/{id}/exec` | ✅ (this PR) | `andy-containers-cli exec {id} ...` ✅ |
| `GET /api/containers/{id}/connection` | ❌ | ❌ |
| `PUT /api/containers/{id}/resources` | ❌ | ❌ |
| `GET /api/containers/{id}/screenshot` | — | — |
| `POST /api/containers/screenshots` | — | — |
| `GET /api/containers/{id}/stats` | ❌ | `andy-containers-cli stats {id}` ✅ |
| `GET /api/containers/{id}/events` | ❌ | ❌ |
| `GET /api/containers/{id}/repositories` | `ListContainerRepositories` ✅ | ❌ |
| `POST /api/containers/{id}/repositories` | `CloneRepository` ✅ | ❌ |
| `POST /api/containers/{id}/repositories/{repoId}/pull` | `PullRepository` ✅ | ❌ |
| `WS /api/containers/{id}/terminal` | — | `andy-containers-cli connect {id}` ✅ |

### Runs (Epic AP)

| REST | MCP | CLI |
|---|---|---|
| `POST /api/runs` | `run.create` ✅ | `andy-containers-cli runs create` ✅ |
| `GET /api/runs/{id}` | `run.get` ✅ | `andy-containers-cli runs get` ✅ |
| `POST /api/runs/{id}/cancel` | `run.cancel` ✅ | `andy-containers-cli runs cancel` ✅ |
| `GET /api/runs/{id}/events` (NDJSON) | `run.events` ✅ | `andy-containers-cli runs events` ✅ |

Full parity. No gaps.

### Environment profiles (Epic X)

| REST | MCP | CLI |
|---|---|---|
| `GET /api/environments` | `environment.list` ✅ | `andy-containers-cli environments list` ✅ |
| `GET /api/environments/{id}` | ❌ | ❌ |
| `GET /api/environments/by-code/{code}` | ❌ | `andy-containers-cli environments get` ✅ |

The catalog is read-only; by-id is rarely the access path (slug lookup is canonical).

### Workspaces

| REST | MCP | CLI |
|---|---|---|
| `GET /api/workspaces` | `ListWorkspaces` ✅ | stub |
| `GET /api/workspaces/{id}` | ❌ | ❌ |
| `POST /api/workspaces` | ❌ | stub |
| `PUT /api/workspaces/{id}` | ❌ | ❌ |
| `DELETE /api/workspaces/{id}` | ❌ | ❌ |

CLI stubs exist in `Program.cs` but aren't wired to real commands. Tracked as a follow-up.

### Templates

| REST | MCP | CLI |
|---|---|---|
| `GET /api/templates` | `BrowseTemplates` ✅ | stub |
| `GET /api/templates/{id}` | ❌ | ❌ |
| `GET /api/templates/by-code/{code}` | ❌ | ❌ |
| `GET /api/templates/{id}/definition` | ❌ | ❌ |
| `POST /api/templates` | ❌ | ❌ |
| `PUT /api/templates/{id}` | ❌ | ❌ |
| `POST /api/templates/{id}/publish` | ❌ | ❌ |
| `DELETE /api/templates/{id}` | ❌ | ❌ |
| `POST /api/templates/validate` | ❌ | ❌ |
| `POST /api/templates/from-yaml` | ❌ | ❌ |
| `PUT /api/templates/{id}/definition` | ❌ | ❌ |
| `GET /api/templates/{code}/image-status` | ❌ | ❌ |
| `GET /api/templates/image-statuses` | ❌ | ❌ |
| `POST /api/templates/{code}/build-image` | ❌ | ❌ |

The CRUD + publish + validate surface is admin-only and lives behind `template:write` / `template:manage`. Image build / image-status would benefit from CLI access for ops; tracked as a follow-up.

### Providers

| REST | MCP | CLI |
|---|---|---|
| `GET /api/providers` | `ListProviders` ✅ | ❌ |
| `GET /api/providers/{id}` | ❌ | ❌ |
| `POST /api/providers` | ❌ | ❌ |
| `GET /api/providers/{id}/health` | ❌ | ❌ |
| `GET /api/providers/{id}/cost-estimate` | ❌ | ❌ |
| `DELETE /api/providers/{id}` | ❌ | ❌ |

CRUD is admin-only. `list` + `health` over CLI would unblock ops; tracked as a follow-up.

### Images

| REST | MCP | CLI |
|---|---|---|
| `GET /api/images/{templateId}` | `ListImages` ✅ | ❌ |
| `GET /api/images/{templateId}/latest` | ❌ | ❌ |
| `POST /api/images/{templateId}/build` | ❌ | ❌ |
| `GET /api/images/diff` | `CompareImages` ✅ | ❌ |
| `GET /api/images/{imageId}/manifest` | `GetImageManifest` ✅ | ❌ |
| `GET /api/images/{imageId}/tools` | `GetImageTools` ✅ | ❌ |
| `GET /api/images/{imageId}/packages` | ❌ | ❌ |
| `POST /api/images/{imageId}/introspect` | ❌ | ❌ |

MCP coverage is solid for the introspection-heavy surfaces agents use. CLI is intentionally absent — ops tooling for images runs through the registry, not this binary.

### API keys & git credentials

| REST | MCP | CLI |
|---|---|---|
| `* /api/api-keys*` (8 routes) | ❌ | — |
| `GET /api/git-credentials` | `ListGitCredentials` ✅ | — |
| `POST /api/git-credentials` | `StoreGitCredential` ✅ | — |
| `DELETE /api/git-credentials/{id}` | ❌ | — |

Credentials are HTTPS-only by policy. Shell history and MCP-message logs are both leak surfaces; the call to keep these out of CLI/MCP is deliberate.

### Organizations

| REST | MCP | CLI |
|---|---|---|
| `* /api/organizations*` (12 routes) | ❌ | — |

Org admin lives in the platform-admin UI / a separate admin CLI; mixing it into the user CLI invites accidental destructive actions.

### Auth

| REST | MCP | CLI |
|---|---|---|
| OAuth device flow | — | `andy-containers-cli auth login` ✅ |

Device flow is interactive and CLI-shaped; not an MCP-tool concern.

## Top 5 highest-value gaps

The following are tracked as separate follow-up issues, ordered by impact-to-effort ratio:

1. **Container lifecycle MCP tools** — start / stop / destroy / exec. **Closed in this PR.**
2. **Workspace CLI** (full implementation) — stubs exist; ~2-3 hours to wire real `create` / `list` / `get` / `delete`.
3. **Template CLI** (full implementation) — stubs exist; ~1-2 hours for `list` / `info` / `definition`.
4. **Provider CLI** (`list` + `health`) — ops visibility for multi-provider deployments.
5. **Image-build CLI** (`build` + `status`) — ops trigger for custom-image rebuilds without dropping to curl.

## Top 3 deliberate omissions

1. **API keys + git credentials over MCP/CLI** — secrets shouldn't transit shell history or the MCP wire. REST + HTTPS only.
2. **File uploads via MCP** (e.g. custom Dockerfiles in container-create) — MCP has no streaming primitive. REST multipart is the right shape.
3. **Admin operations (org / provider / template publish) over CLI** — separate-concern surface; should not share a binary with user-facing commands.

## Maintaining this document

Refresh this matrix whenever a new endpoint, MCP tool, or CLI command lands. The acceptance bar isn't "fill every cell" — some cells are deliberately empty. The bar is "every cell has a defensible answer".

Cross-references:

- ADR 0002 (environment profiles) — `docs/adr/0002-environment-profiles.md`
- ADR 0003 (agent run execution) — `docs/adr/0003-agent-run-execution.md`
- Run docs — `docs/runs.md`
- Run configurator — `docs/run-configurator.md`
