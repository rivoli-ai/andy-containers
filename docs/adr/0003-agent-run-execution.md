# ADR 0003 — Agent run execution

**Status.** Accepted. Implemented across stories AP1-AP12 (Epic AP, andy-containers#101).

## Context

Before Epic AP, andy-containers handled containers as opaque runtime hosts: the API created them, exposed exec / terminal / VNC, and emitted lifecycle events (`run.{containerId}.finished` and friends), but had no opinion on what executed inside them. Agents were a Conductor concern; the andy-containers controller didn't know whether a container was running a triage prompt, a test suite, or nothing at all.

The simulator-parity wave (Epics AP / X / Y) makes the agent run a **first-class entity** the platform tracks end-to-end:

- Conductor asks andy-containers to start a run via `POST /api/runs`.
- The run is observable (`GET /api/runs/{id}`), cancellable (`POST /api/runs/{id}/cancel`), and its lifecycle streams to subscribers via NATS subjects keyed on `Run.Id`.
- Subjects, payloads, and state-machine transitions are stable enough for downstream services (andy-issues, andy-tasks, audit) to subscribe without negotiating semantics out-of-band.

That requires answering three questions:

1. **What runs inside the container?** A specific binary with a specific config contract — not "whatever the agent author wrote".
2. **Who decides what gets run, where, and how?** A central piece in andy-containers that maps `RunMode` to the right execution surface.
3. **How do we observe and cancel cleanly?** Without a registry of in-flight processes, the cancel endpoint can only flip a database flag and hope.

## Decision

**`andy-cli` is the in-container agent runtime.** Every `Headless` run spawns

```
andy-cli run --headless --config <path-to-headless-run-config.json>
```

inside the run's container. `andy-cli` reads the config, executes the agent, emits structured events on stdout, and exits with a defined code (the AQ2 contract). andy-containers parses the exit code to derive the terminal `RunStatus` + `RunEventKind`, writes both to the DB and the outbox in the same transaction, and revokes the run-scoped token (AP10).

**`IRunModeDispatcher` is the routing seam.** `RunsController.Create` doesn't know about runner internals. It hands off to a single `IRunModeDispatcher` that:

- selects or provisions a container,
- transitions `Pending` → `Provisioning`,
- routes to the right execution surface based on `Run.Mode`:
  - `Headless` → `IHeadlessRunner` (spawn andy-cli, await exit, terminate).
  - `Terminal` → return `Attachable` (caller binds a TTY via `/api/containers/{id}/terminal`).
  - `Desktop` → not yet implemented; explicit `NotImplemented` outcome rather than a dead branch.

**`IRunCancellationRegistry` is the cancel seam.** Each active runner registers a linked CTS at spawn and disposes the registration on terminal write. The cancel endpoint signals via the registry and awaits the runner's terminal write up to a 30 s grace before forcing the row to `Cancelled`. No registry entry → flip the row directly (Pending row, no in-flight process).

## Alternatives considered

### Run agents directly via `docker exec` from the controller thread

Rejected. Conflates "what to run" (agent prompt + tools) with "how to run it" (exec syntax, stdout parsing, exit-code mapping). Every agent author would re-derive the contract; every regression in andy-containers would require touching agent code and vice versa. `andy-cli` decouples them: the contract between andy-containers and the agent is a JSON config schema (AQ1) plus an exit-code table (AQ2), not a shell command.

### Push agents into background workers; synchronous controller is just persistence

Tempting but premature. AP1-AP9 land the synchronous path because:
- It keeps the wire shape stable for early consumers.
- It surfaces every state-machine transition in one stack trace, which made AP6/AP7 wiring much easier to test.
- The 201 response carrying the terminal state turned out to be a useful UX signal for the CLI / MCP clients.

The follow-up to move dispatch off the request thread is intentional, but doing it on day one would have made the AP1-AP9 sequence much harder to bisect.

### A single `IRunService` instead of separate runner / dispatcher / registry

Rejected. The three concerns have different lifetimes and DI scopes:

- `IHeadlessRunner` is **scoped** — one per request — because it consumes a `DbContext`.
- `IRunCancellationRegistry` is **singleton** — runner registrations span request scopes (the runner is alive in request A, the cancel POST arrives on request B).
- `IRunModeDispatcher` is **scoped** — same reason as the runner.

Collapsing them would force one of the three into the wrong scope and silently break the cancel signalling.

### Use the existing `andy.containers.events.run.{containerId}.*` subjects for AP runs

Rejected — the subject must key on `Run.Id`, not `Container.Id`. A container can host multiple runs (sequential or — once per-container concurrency lands — parallel); subscribers need to correlate to the specific run. AP6 introduced `AppendAgentRunEvent` keyed on `Run.Id` while leaving the container-lifecycle helper (`AppendRunEvent`) keyed on `Container.Id`. Same outbox table, different correlation.

## Amendment — F4.1 mid-run output stream (rivoli-ai/conductor#1934)

The original AP9 event stream only surfaces **terminal** observations (`run.{id}.finished|failed|cancelled|timeout`). F4.1 adds a **mid-run** incremental stdout/stderr feed so an operator (or the TX Task Execution Cockpit) can watch a headless run's progress live, not just learn the outcome after it ends.

Two shapes were considered:

1. **Run-scoped output events** — a dedicated per-run live feed (`GET /api/runs/{id}/output`, SSE) carrying `andy-cli` stdout/stderr line-by-line with a monotonic sequence number, resumable via `Last-Event-ID`.
2. **Container-logs SSE** — `GET /api/containers/{id}/logs?follow=1` over the run's container, framing the run feed as "logs of the container backing this run".

**Decision: shape (1).** A run-scoped endpoint keeps the credential-redaction boundary and the `run:read` permission gate aligned with the existing `/api/runs/{id}/events`, and keeps the resumption cursor a clean per-run sequence rather than a container-log offset. The streaming plumbing is provider-agnostic: `IContainerService.ExecStreamingAsync` / `IInfrastructureProvider.ExecStreamingAsync` read the **same** `ExecCreate` + `StartAndAttach` surface incrementally (no new Docker-Engine verb — Conductor decision #17), Docker overrides for a true live tail, and every other provider inherits the interface-default buffered replay. Lines fan out through `IRunOutputBus` (`InMemoryRunOutputBus`), a direct sibling of IM9's `InMemoryBuildEventBus`: per-run ring buffer for late-subscriber catch-up, bounded per-subscriber channels that drop rather than backpressure the runner, and a terminal marker that drains then closes — the same terminal-stop contract as `RunEventStream`. `RunOutputRedactor` masks `ANDY_TOKEN` (literal + env-echo shapes) before any line reaches the wire. Shape (2) can still be layered on later as a thin alias over the same bus should a pure container-logs consumer need it; the bus contract is deliberately runtime-agnostic so that swap requires no runner change.

## Consequences

### Positive

- **Stable contract surface.** `CreateRunRequest`, `Run` (response), and the four NATS subjects are pinned by AP11's OpenAPI spec. AQ1 (config schema) + AQ2 (exit codes) pin the andy-cli boundary. Any of these breaking is a contract change with a coordinated cross-repo PR, not a surprise.
- **Provider-agnostic execution.** `IHeadlessRunner` only needs `IContainerService.ExecAsync` and `IContainerService.GetContainerAsync`; all 9 infrastructure providers (Docker / Apple / AWS / Azure / GCP / Civo / DigitalOcean / Fly.io / Hetzner) inherit the AP runtime for free.
- **Cancellation works across request scopes.** `IRunCancellationRegistry` is the in-process replacement for what would otherwise need a NATS control channel; we can swap it for distributed signalling (e.g. cancel commands published to a per-runner subject) without touching callers.
- **Run-scoped credentials revoke automatically.** AP10's `ITokenIssuer.RevokeAsync` runs in `HeadlessRunner.TerminateAsync`'s `finally`-equivalent so every terminal observation (Succeeded / Failed / Cancelled / Timeout) frees the token. The `SecretsScope.RunScoped` enum on the bound `EnvironmentProfile` (Epic X) drives this lifecycle.

### Negative

- **andy-cli is a hard dependency.** A container that ships without `andy-cli` on `$PATH` cannot host a Headless run; provisioning succeeds but the runner exits non-zero. Mitigated by the `EnvironmentProfile.BaseImageRef` contract (X4): the image bound to a `HeadlessContainer` profile is expected to ship andy-cli. Future work: pre-flight check before spawn.
- **Synchronous dispatch limits throughput.** A long-running headless agent ties up the request thread until exit. The 201 with terminal state is the most visible symptom. Moving dispatch off the request thread is a follow-up; the seam is `IRunModeDispatcher.DispatchAsync`'s return shape, which already supports an async `Started` outcome.
- **In-process cancel registry doesn't survive a restart.** A run that's mid-flight when andy-containers restarts has no registry entry on the new process; cancelling falls through to "flip the row". Acceptable today (single-node deployments); an HA story would need to persist active-runner state.

## Cross-references

- **AP1 — Run entity + state machine:** `src/Andy.Containers/Models/Run.cs`.
- **AP2 — `/api/runs` controller:** `src/Andy.Containers.Api/Controllers/RunsController.cs`.
- **AP3 — configurator:** `src/Andy.Containers/Configurator/RunConfigurator.cs`. Spec → config mapping in `docs/run-configurator.md`.
- **AP4 — config builder:** `src/Andy.Containers/Configurator/HeadlessConfigBuilder.cs` (golden-file tests in AP12).
- **AP5 — mode dispatcher:** `src/Andy.Containers.Api/Services/RunModeDispatcher.cs`.
- **AP6 — headless runner:** `src/Andy.Containers.Api/Services/HeadlessRunner.cs`.
- **AP7 — cancel registry:** `src/Andy.Containers.Api/Services/IRunCancellationRegistry.cs`.
- **AP8 — MCP tools:** `src/Andy.Containers.Api/Mcp/RunsMcpTools.cs`.
- **AP9 — CLI + NDJSON event stream:** `src/Andy.Containers.Cli/Commands/RunCommands.cs`, `src/Andy.Containers.Api/Services/RunEventStream.cs`.
- **AP10 — secrets scope:** `src/Andy.Containers/Configurator/ITokenIssuer.cs`.
- **F4.1 — mid-run output stream:** `src/Andy.Containers/Storage/IRunOutputBus.cs`, `src/Andy.Containers.Infrastructure/Runs/Events/InMemoryRunOutputBus.cs`, `src/Andy.Containers.Api/Services/RunOutputSse.cs`, `src/Andy.Containers.Api/Services/RunOutputRedactor.cs`. Endpoint: `GET /api/runs/{id}/output`. See `docs/runs.md` "Mid-run output stream".
- **AP11 — OpenAPI:** `openapi/containers-api.yaml` (`/api/runs*` paths and schemas).
- **AP12 — golden-file conformance:** `tests/Andy.Containers.Api.Tests/Configurator/HeadlessConfigBuilderGoldenFileTests.cs`.
- **AQ1 (andy-cli) — headless config schema:** see the `andy-cli` repo for the JSON schema andy-containers writes against.
- **AQ2 (andy-cli) — exit-code contract:** see the `andy-cli` repo for the canonical `HeadlessExitCode` enum.

## Footnote

The mode names (`Headless` / `Terminal` / `Desktop`) align with the `EnvironmentProfile.Kind` enum from Epic X. A `HeadlessContainer` profile only backs `RunMode.Headless` runs; the pairing is enforced by the AP5 dispatcher and validated at workspace-create time via the X5 binding.
