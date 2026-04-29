# API / MCP / CLI parity audit

The platform exposes three transports against the same business surface: REST (`/api/*`), MCP tools, and the `andy-containers-cli` binary. This page is the canonical map of what's covered where, why some gaps are deliberate, and where the open work lives.

Authoritative sources read at audit time:

- REST — every `[Http*]` action across `src/Andy.Containers.Api/Controllers/`
- MCP — every `[McpServerTool(...)]` method in `src/Andy.Containers.Api/Mcp/`
- CLI — every command tree assembled in `src/Andy.Containers.Cli/Program.cs` from `src/Andy.Containers.Cli/Commands/`

Last refreshed: **2026-04-27** — after the #76 follow-ups landed (#189 workspace CLI, #190 template CLI, #191 provider CLI).

## Counts at a glance

- **REST endpoints (business)**: 75
- **MCP coverage**: ~35 tools (~47% of REST endpoints)
- **CLI coverage**: ~28 commands (~37% of REST endpoints)

Numbers exclude health checks, Swagger, and WebSocket attach surfaces (which don't map to MCP/CLI by design).

## Matrix

✅ present · ❌ missing · — doesn't fit this transport

### Containers

| REST | MCP | CLI |
|---|---|---|
| `GET /api/containers` | `ListContainers` ✅ | `andy-containers-cli list` ✅ |
| `GET /api/containers/{id}` | `GetContainer` ✅ | `andy-containers-cli info {id}` ✅ |
| `POST /api/containers` | ❌ | `andy-containers-cli create` ✅ |
| `POST /api/containers/{id}/start` | ✅ | `andy-containers-cli start {id}` ✅ |
| `POST /api/containers/{id}/stop` | ✅ | `andy-containers-cli stop {id}` ✅ |
| `DELETE /api/containers/{id}` | ✅ | `andy-containers-cli destroy {id}` ✅ |
| `POST /api/containers/{id}/exec` | ✅ | `andy-containers-cli exec {id} ...` ✅ |
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
| `GET /api/workspaces` | `ListWorkspaces` ✅ | `andy-containers-cli workspace list` ✅ |
| `GET /api/workspaces/{id}` | ❌ | `andy-containers-cli workspace get` ✅ |
| `POST /api/workspaces` | ❌ | `andy-containers-cli workspace create` ✅ |
| `PUT /api/workspaces/{id}` | ❌ | — |
| `DELETE /api/workspaces/{id}` | ❌ | `andy-containers-cli workspace delete` ✅ |

`workspace update` is intentionally absent from CLI: `UpdateWorkspaceDto` doesn't expose the governance fields anyway (X5 keeps the EnvironmentProfile binding immutable for the workspace's life), and operators rarely reach for it.

### Templates

| REST | MCP | CLI |
|---|---|---|
| `GET /api/templates` | `BrowseTemplates` ✅ | `andy-containers-cli templates list` ✅ |
| `GET /api/templates/{id}` | ❌ | ❌ |
| `GET /api/templates/by-code/{code}` | ❌ | `andy-containers-cli templates info` ✅ |
| `GET /api/templates/{id}/definition` | ❌ | `andy-containers-cli templates definition` ✅ |
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
| `GET /api/providers` | `ListProviders` ✅ | `andy-containers-cli providers list` ✅ |
| `GET /api/providers/{id}` | ❌ | ❌ |
| `POST /api/providers` | ❌ | — |
| `GET /api/providers/{id}/health` | ❌ | `andy-containers-cli providers health` ✅ |
| `GET /api/providers/{id}/cost-estimate` | ❌ | ❌ |
| `DELETE /api/providers/{id}` | ❌ | — |

CRUD is admin-only via REST; deliberately not wired to the user CLI. Ops-shaped operations (`list`, `health`) are surfaced.

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

## Recently closed gaps

- **Container lifecycle MCP tools** — `start` / `stop` / `destroy` / `exec` over MCP. Closed in #192.
- **Workspace CLI** (full implementation) — `list` / `get` / `create` / `delete`. Closed in #189.
- **Template CLI** (full implementation) — `list` / `info` / `definition`. Closed in #190.
- **Provider CLI** — `list` + `health`. Closed in #191.

## Remaining open gaps

Ordered by impact-to-effort ratio. Open one issue per item if/when prioritised:

1. **Image-build CLI** (`build` + `status`) — ops trigger for custom-image rebuilds without dropping to curl.
2. **Container resource update** (`PUT /api/containers/{id}/resources`) — neither MCP nor CLI; rare path, but the only way to bump CPU/RAM today is HTTP.
3. **Container events SSE** — MCP cannot stream; CLI could mirror the NDJSON pattern from `runs events`.
4. **Provider cost-estimate** — CLI surface would help capacity-planning runs.

## Top 3 deliberate omissions

1. **API keys + git credentials over MCP/CLI** — secrets shouldn't transit shell history or the MCP wire. REST + HTTPS only.
2. **File uploads via MCP** (e.g. custom Dockerfiles in container-create) — MCP has no streaming primitive. REST multipart is the right shape.
3. **Admin operations (org / provider / template publish / workspace update) over CLI** — separate-concern surface; should not share a binary with user-facing commands.

## Maintaining this document

Refresh this matrix whenever a new endpoint, MCP tool, or CLI command lands. The acceptance bar isn't "fill every cell" — some cells are deliberately empty. The bar is "every cell has a defensible answer".

Cross-references:

- ADR 0002 (environment profiles) — `docs/adr/0002-environment-profiles.md`
- ADR 0003 (agent run execution) — `docs/adr/0003-agent-run-execution.md`
- Run docs — `docs/runs.md`
- Run configurator — `docs/run-configurator.md`
