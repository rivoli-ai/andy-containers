# Environment profiles

`EnvironmentProfile` is the governance descriptor that sits **above** `ContainerTemplate` in the resolution order. A template says **what to provision**; a profile says **what the environment is permitted to do**.

The architectural decision is recorded in [ADR 0002](adr/0002-environment-profiles.md). This page is the operator + developer reference: the entity shape, the capability matrix for the seeded profiles, the provisioning flow, and how to extend the catalog.

## Entity shape

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | Primary key. |
| `Name` (a.k.a. `code`) | `string`, unique | Stable slug across renames (e.g. `headless-container`). |
| `DisplayName` | `string` | Human-readable label. |
| `Kind` | `enum` | `HeadlessContainer | Terminal | Desktop`. Drives the GUI sidecar at provision time. |
| `BaseImageRef` | `string` | OCI reference; overrides `template.BaseImage` when bound. |
| `Capabilities` | `EnvironmentCapabilities` | Structured envelope, owned-as-JSON in EF (one column on Postgres `jsonb`, TEXT on SQLite). |
| `CreatedAt` | `DateTimeOffset` | Audit. |

`EnvironmentCapabilities` carries:

| Field | Type | Notes |
|---|---|---|
| `NetworkAllowlist` | `List<string>` | Hostnames or wildcards (`*.github.com`); empty list = no egress. |
| `SecretsScope` | `enum` | `None | RunScoped | WorkspaceScoped | OrganizationScoped`. Couples to AP10's token lifecycle. |
| `HasGui` | `bool` | True only for `Desktop`. Drives VNC sidecar wiring (port 6080, `/start.sh` entrypoint). |
| `AuditMode` | `enum` | `None | Standard | Strict`. `Strict` captures every tool call pre-redaction. |

## Capability matrix (seeded profiles)

The three baseline profiles live in `config/environments/global/*.yaml` and are loaded idempotently at API startup:

| Code | Kind | GUI | Secrets scope | Audit | Network |
|---|---|---|---|---|---|
| `headless-container` | `HeadlessContainer` | no | `WorkspaceScoped` | `Strict` | restricted (registry, GitHub API, pypi, nuget) |
| `terminal` | `Terminal` | no | `WorkspaceScoped` | `Standard` | wildcard (`*`) |
| `desktop` | `Desktop` | yes | `OrganizationScoped` | `Standard` | wildcard (`*`) |

Rationale per profile:

- **`headless-container`** — unattended agents (triage, planning, execution). Strict audit because no human is in the loop. Restricted egress because anything beyond platform services + canonical package mirrors is suspicious for an unattended run.
- **`terminal`** — TTY attach where a human drives the agent. Wildcard egress because interactive sessions routinely reach docs, registries, third-party APIs. Standard audit since the human is the primary control.
- **`desktop`** — full GUI session via VNC. Long-lived, often spans multiple runs, so `OrganizationScoped` (≈ SSO-scoped) credentials beat run-scoped ones.

## Provisioning flow

```
workspace-create  →  resolve EnvironmentProfile.Code (X5)
                   ↳ optional agent-allowed-environments check (X9)
                   ↳ persist Workspace.EnvironmentProfileId

container-create  →  EnvironmentProfileId from request
                   ↳ fall back to Workspace.EnvironmentProfileId (X5 inheritance)
                   ↳ profile.BaseImageRef wins over template.BaseImage    (X4)
                   ↳ profile.Kind drives GuiType (Desktop → "vnc"; else "none")  (X4)
                   ↳ ContainerProvisionJob carries the resolved values
                   ↳ provider creates the container; worker maps ports / cmd
                                                          (sidecars are the worker's
                                                           concern, not the provider's)

run-time          →  AP10 mints a run-scoped token when the agent's profile
                   has SecretsScope = RunScoped. Token is revoked on every
                   terminal event (Succeeded / Failed / Cancelled / Timeout).
```

Two key invariants:

1. **Profile-as-source-of-truth.** When a profile is bound, `template.BaseImage` and `template.GuiType` are ignored. The template still drives resources / scripts / dependencies.
2. **Workspace inheritance, not pin.** Containers in a workspace inherit the workspace's profile. An explicit `request.EnvironmentProfileId` still wins — one-off shells into a different env are intentional.

## Extending the catalog

Add a YAML file under `config/environments/global/` and restart the API.

```yaml
code: my-profile
display_name: My profile
kind: HeadlessContainer
base_image_ref: ghcr.io/example/my-headless:latest

capabilities:
  network_allowlist:
    - api.example.com
  secrets_scope: WorkspaceScoped
  has_gui: false
  audit_mode: Strict
```

**Rules.**

- Field names use `snake_case` in YAML; the seeder maps to `PascalCase` properties via `UnderscoredNamingConvention`.
- `kind` / `secrets_scope` / `audit_mode` must match the enum names exactly (case-insensitive).
- The seeder is idempotent on `code`. Existing rows are never overwritten — operator hand-edits via the catalog API survive restarts. To force a refresh, delete the row in the catalog and restart.
- Malformed files are logged and skipped; the host never aborts startup on a bad seed entry.

`config/environments/README.md` carries the colocated schema reference for operators editing seeds.

## Surfaces

The catalog is exposed via three transports, all gated on the `environment:read` permission (granted to Admin / Editor / Viewer in `Andy.Containers.Models.OrgRoles`):

- **HTTP** — `GET /api/environments`, `GET /api/environments/{id}`, `GET /api/environments/by-code/{code}`. Pagination envelope `{items, totalCount}`. See [API reference](api-reference.md) and the OpenAPI spec at `openapi/containers-api.yaml`.
- **MCP** — `environment.list` tool exposed by `EnvironmentsMcpTools`. Auto-discovered via `WithToolsFromAssembly()`; consumers (Conductor, Claude Code, agent runtimes) can introspect the catalog without an HTTP round-trip.
- **CLI** — `andy-containers-cli environments list [--kind <Kind>] [--format table|json]` and `andy-containers-cli environments get <code>`. Shared `--format` flag is part of the Epic AN per-service CLI contract.

## Relationship to `ContainerTemplate`

Both exist; both are required to provision. They answer different questions:

| Concern | ContainerTemplate | EnvironmentProfile |
|---|---|---|
| Base image | yes (default) | yes (overrides template when bound) |
| Resources (CPU/RAM/disk) | yes | no |
| Post-create scripts / dependencies | yes | no |
| GUI sidecar (VNC) | yes (legacy) | yes (drives flip when bound) |
| Network allowlist | no | yes |
| Secrets scope | no | yes |
| Audit mode | no | yes |
| Mutable by operators via API | yes (CRUD) | read-only today (X3); CRUD lands when an operator UI requires it |

Templates evolve with toolchain versions; profiles evolve with governance policy. Decoupling them means a template revision isn't a security review and an audit-policy change isn't a rebuild.

## Cross-service contract

When an agent declares `allowed_environments` (Epic W3 in `andy-agents`), `WorkspacesController.Create` enforces it: a workspace bound to a profile not in the agent's allowlist is rejected with 403. Today the resolver is a stub (`StubAgentCapabilityService` returns null = no policy on record). Once the W3 endpoint ships, swap the DI registration in `Program.cs` for an HTTP client; `IAgentCapabilityService` is the seam.

A capability-service outage fails closed (503) rather than provisioning against an unverifiable policy. See ADR 0002 for the rationale.
