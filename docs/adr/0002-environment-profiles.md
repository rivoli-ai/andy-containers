# ADR 0002 — Environment profiles

**Status.** Accepted. Implemented across stories X1–X9 (Epic X, andy-containers#88).

## Context

Until X1, the platform only had two governance handles per container:

1. The `ContainerTemplate` — a provisioning recipe (base image, dependencies, GUI mode, post-create scripts).
2. Ad-hoc per-environment env vars / network policies, configured at the provider layer.

This conflated **what to provision** with **what the environment is permitted to do**. A "headless triage container" and a "developer terminal session" that happened to share the same base image had no way to differ in:

- network egress allowlist,
- secrets scope (run-scoped tokens vs. workspace-wide),
- audit-trail intensity,
- whether a GUI sidecar was attached.

The simulator's `app.js` already enumerated three runtime shapes — `headless-container`, `terminal`, `desktop` — that downstream services (Conductor, andy-issues, andy-tasks) needed to reason about. Encoding those shapes only as templates left the governance contract implicit.

## Decision

Introduce **`EnvironmentProfile`** as a first-class entity sitting **above** `ContainerTemplate` in the resolution order:

- A profile carries the runtime shape (`HeadlessContainer | Terminal | Desktop`), the base image, and a structured capability envelope (`NetworkAllowlist`, `SecretsScope`, `HasGui`, `AuditMode`).
- Workspaces bind a profile at create time; containers created in the workspace inherit it.
- When a profile is bound, its `BaseImageRef` and `Kind` override the template's image and GUI fields. The template still drives resources, scripts, and dependencies — only the image and sidecar surface flip.
- Three baseline profiles ship as YAML seeds in `config/environments/global/*.yaml`, loaded idempotently at startup. Operator hand-edits via the catalog API survive restarts.
- Agent capability declarations (`allowed_environments`, owned by andy-agents Epic W3) gate workspace-create: a workspace bound to a profile not in the agent's allowlist is rejected with 403.

## Alternatives considered

### Extend `ContainerTemplate` with capability fields

Rejected. Templates are recipes — version-pinned, dependency-aware, often forked. Mixing governance fields would mean every template revision becomes a governance change, every governance update needs a template rev, and operators editing audit policy would be reaching into a "how to build the image" file. Profiles let the two evolve independently.

### Per-workspace capability ACLs

Rejected. Workspaces would each need a hand-curated capability set, with no sharing across workspaces of the same shape. Doesn't compose with the agent catalog (agents declare which *kinds* they allow, not which *workspaces*). Also can't be governed centrally — every workspace owner becomes a security policy author.

### A single boolean flag (`isHeadless`) on `Workspace`

Rejected. Couldn't express the desktop / terminal distinction or the audit and secrets dimensions. Would have leaked into 9 provider implementations as branching logic instead of one orchestration-layer decision.

## Consequences

### Positive

- **Provider-agnostic.** All 9 infrastructure providers inherit headless / desktop / terminal behaviour for free — sidecar wiring lives in `ContainerOrchestrationService` + the `ContainerProvisioningWorker`, not the providers (X4).
- **Governance is explicit.** The capability envelope on the wire (X3 DTO, X8 OpenAPI) lets MCP clients / CLIs / Conductor reason about what an environment is permitted to do without parsing template internals.
- **Run-scoped tokens couple cleanly.** AP10's `ITokenIssuer` revokes on terminal events when `SecretsScope == RunScoped`; broader scopes don't get auto-revoked. The enum on the profile drives the lifecycle.
- **Idempotent seeds.** The YAML pipeline catches drift at startup (round-trip tested in X9) without requiring per-environment migrations.

### Negative

- **Cross-service dependency.** Workspace-create now queries andy-agents (Epic W3) for `allowed_environments`. The current implementation is a stub returning null (open by default); a real allowlist requires W3 to ship. A capability-service outage fails closed (503) rather than provisioning against an unverifiable policy.
- **Double-resolution at create time.** Both the template and the profile must exist before provisioning. Misconfiguration surfaces as a 400 (unknown profile code) rather than a 500.
- **Conductor governance UI (Epic AF) depends on the catalog endpoint.** The shape of `EnvironmentProfile` and `EnvironmentCapabilities` is a public contract that AF will pin; field drift requires a coordinated change across the two repos.

## Cross-references

- **X1 — entity + EF migration:** `src/Andy.Containers/Models/EnvironmentProfile.cs`, `Migrations/AddEnvironmentProfiles`.
- **X2 — YAML seeder:** `src/Andy.Containers.Api/Data/EnvironmentProfileSeeder.cs`, `config/environments/global/*.yaml`.
- **X3 — catalog endpoint:** `src/Andy.Containers.Api/Controllers/EnvironmentsController.cs`.
- **X4 — provisioning override:** `src/Andy.Containers.Api/Services/ContainerOrchestrationService.cs` (profile-driven image + GuiType).
- **X5 — workspace binding:** `src/Andy.Containers.Api/Controllers/WorkspacesController.cs` (`EnvironmentProfileCode` required at create time).
- **X6 — MCP tool:** `src/Andy.Containers.Api/Mcp/EnvironmentsMcpTools.cs`.
- **X7 — CLI:** `src/Andy.Containers.Cli/Commands/EnvironmentCommands.cs`.
- **X8 — OpenAPI + lint:** `openapi/containers-api.yaml`, `.github/workflows/openapi.yml`.
- **X9 — agent enforcement + production-YAML round-trip tests:** `IAgentCapabilityService`, `EnvironmentProfileSeederProductionYamlTests`.
- **AP10 — secrets scope wiring:** `src/Andy.Containers/Configurator/ITokenIssuer.cs` (couples to `SecretsScope.RunScoped`).
- **W3 (andy-agents) — allowed_environments:** awaits cross-service contract; `IAgentCapabilityService` is the swap-in seam.

## Footnote

The three profile codes (`headless-container`, `terminal`, `desktop`) come from the simulator's `app.js`, the canonical reference for naming during the simulator-parity wave (Epic AP, Epic X, Epic Y).
