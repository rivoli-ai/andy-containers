# Agent runs

A **run** is the execution of an agent against a delegation contract inside a provisioned container. Conductor (or an MCP/CLI client) submits a run; andy-containers persists it, configures andy-cli, dispatches it to the right execution surface, observes its lifecycle, and emits NATS events so downstream services (andy-issues, andy-tasks, audit) can react.

The architectural decision is recorded in [ADR 0003](adr/0003-agent-run-execution.md). The configurator's spec → config mapping lives in [run-configurator.md](run-configurator.md). This page is the operator + integrator concept reference: the lifecycle, the surfaces, the event contract, and how to drive the system from each entry point.

## Lifecycle

```
        Pending ──┐
                  ├──> Provisioning ──> Running ──> Succeeded
                  │                                  ─> Failed
                  │                                  ─> Cancelled
                  │                                  ─> Timeout
                  └─────────────────> Cancelled / Failed (pre-spawn)
```

| Status | Meaning |
|---|---|
| `Pending` | Row persisted; AP5 dispatcher hasn't picked it up yet. |
| `Provisioning` | AP5 has selected/created a container; AP6 is about to spawn andy-cli. |
| `Running` | andy-cli is in flight. |
| `Succeeded` | Exit code 0. |
| `Failed` | Exit code 1, 2, 5, or unmapped non-zero. |
| `Cancelled` | Exit code 3, OR an external cancel signal observed before exit. |
| `Timeout` | Exit code 4 (AQ3 self-timeout) OR ExecAsync's outer watchdog fired. |

The four terminal states are mutually exclusive and one-way: `Run.TransitionTo` rejects backward edges and disallowed sideways moves at the entity layer.

## Submitting a run

Three transports, identical wire shape:

### HTTP (Conductor + everything else)

```
POST /api/runs
Content-Type: application/json

{
  "agentId": "triage-agent",
  "agentRevision": 3,
  "mode": "Headless",
  "environmentProfileId": "1f0e2d3c-4b5a-4c6d-9e8f-7a6b5c4d3e2f",
  "workspaceRef": { "workspaceId": "...", "branch": "main" },
  "policyId": null,
  "correlationId": null
}
```

Returns 201 with the [`Run`](https://github.com/rivoli-ai/andy-containers/blob/main/openapi/containers-api.yaml) response body. Today the response can already carry a terminal status because AP5's dispatcher is synchronous (a follow-up moves it off the request thread).

### MCP

```
run.create  ({ agentId, mode, environmentProfileId, workspaceId?, branch?, policyId?, correlationId? })
```

Discoverable via `WithToolsFromAssembly()` in `Program.cs`. Permission-gated on `run:write`.

### CLI

```
andy-containers-cli runs create triage-agent \
  --mode Headless \
  --environment-profile 1f0e2d3c-4b5a-4c6d-9e8f-7a6b5c4d3e2f \
  --workspace ... --branch main
```

`runs get`, `runs cancel`, and `runs events` are the matching observers / mutators.

## Modes

| Mode | Surface | What runs in the container |
|---|---|---|
| `Headless` | `IHeadlessRunner` spawns `andy-cli run --headless --config <path>`. | `andy-cli` reads the AQ1 config, executes the agent, exits with an AQ2 code. andy-containers maps the code to a `RunStatus` + writes a terminal event. |
| `Terminal` | Caller binds a TTY via `/api/containers/{id}/terminal`. AP5 returns `Attachable`. | Operator-driven; andy-containers exposes the exec surface but doesn't drive the agent. |
| `Desktop` | Not implemented. AP5 returns `NotImplemented`. | Routing reserved for VNC-attached agents (Epic AF). |

Modes pair with `EnvironmentProfile.Kind` via Epic X. A `HeadlessContainer` profile only backs `RunMode.Headless` runs; the pairing is enforced at workspace-create time (X5).

## Per-run git branch (F6.1)

After the dispatcher selects the container (the `Run.ContainerId` assignment
point, post-clone), `RunBranchService` checks out a deterministic per-run
branch **`andy/run/{runId}`** in every *cloned* repo of the container, off the
workspace's `GitBranch` base. A retry checks out an existing run branch
without resetting its ref; a first dispatch creates it with `git checkout -b`
through
`IInfrastructureProvider.ExecAsync` (the same exec surface `GitCloneService`
uses — ARCHITECTURE §16, **not** a Docker-Engine verb, decision #17). The
chosen name is persisted into `Run.WorkspaceRef.Branch` so it survives
container teardown and late subscribers can resolve it; no schema migration is
needed (`WorkspaceRef.Branch` already exists on the model).

Branch creation is **best-effort per repo**: a repo that isn't a git checkout
(or whose checkout fails) is logged and skipped — it never aborts the dispatch
(mirrors GitCloneService's "a failed clone doesn't fail the container"). Each
successful checkout emits a `RunBranchCheckedOut` container event.

The run's changes are then retrievable as a unified patch via
[`GET /api/containers/{id}/git/diff`](api-reference.md#get-apicontainersidgitdiff)
(`git diff <base>` of the run branch + working tree), aggregated across repos or
scoped to one via `repoId`.

### Commit-derived deliverable artifacts

After a successful headless run, each cloned repository with staged changes is
checkpointed on `andy/run/{runId}`. The same guarded shell transaction then
writes a bundle under:

```
/workspace/.andy/outputs/deliverables/{runId}/{repositoryId}/
  checkpoint.patch
  changed-files.tsv
  manifest.json
```

`checkpoint.patch` is a binary-capable `git show` of the exact checkpoint
commit; `changed-files.tsv` records Git status plus every changed source, test,
documentation, and asset path; `manifest.json` binds the bundle to the run,
repository, branch, and commit SHA. Deleted files remain represented by the
patch and manifest. A no-change run does not fabricate a bundle.
During collection, older run-scoped bundles are excluded so a later no-change
task cannot inherit stale deliverables merely because they remain on the
shared container filesystem.

The normal recursive output collector discovers these files alongside
`run-summary.md`, hashes them, assigns MIME types (`text/x-diff`,
`text/tab-separated-values`, `application/json`), uploads their bytes to
andy-docs, and writes their typed `DocsRef`s into `Run.OutputArtifacts` and the
terminal event. The document link targets the concrete run id—not the hosting
container id—so downstream `output_doc_refs` and `prior_context.artifact_refs`
refer to the correct attempt. `.andy/inputs`, pre-existing `.andy/outputs`, and
untracked backup-shaped files remain excluded from the Git commit itself.

## Cancellation

```
POST /api/runs/{id}/cancel
```

Behaviour depends on whether a runner is in flight:

- **Active runner** — controller signals via `IRunCancellationRegistry`. The runner's `ExecAsync` aborts (linked CTS), its catch-OCE branch transitions the run to `Cancelled`, appends `andy.containers.events.run.{id}.cancelled` to the outbox, saves, and disposes its registration. The cancel endpoint awaits that disposal up to **30 s** before returning.
- **No active runner** (Pending row, server restart, runner already exited) — controller flips the row directly and emits the cancelled event.

Either way, exactly one terminal subject lands in the outbox. The 30 s grace is for hung Docker exec streams that don't honour the linked CTS; the runner's catch-OCE path normally resolves in <100 ms.

`POST /api/runs/{id}/cancel` returns:
- **200** with the run DTO (state reflects committed terminal write).
- **404** if the run is unknown.
- **409** if the run is already terminal.

## Event stream

Every lifecycle observation appends a row to `OutboxEntries` in the same EF
transaction as its state change. The `OutboxDispatcher` drains rows to NATS
at-least-once (ADR 0001). Subjects cover the whole attempt:

```
andy.containers.events.run.{runId}.queued
andy.containers.events.run.{runId}.provisioning
andy.containers.events.run.{runId}.ready
andy.containers.events.run.{runId}.running
andy.containers.events.run.{runId}.progress
andy.containers.events.run.{runId}.finished
andy.containers.events.run.{runId}.failed
andy.containers.events.run.{runId}.cancelled
andy.containers.events.run.{runId}.timeout
```

Live stdout/stderr is also published directly (ephemeral, not through the
transactional outbox) on
`andy.containers.events.run.{runId}.output`. Its payload uses the same
`attempt_id` and monotonic `sequence` as the lifecycle and SSE streams; NATS
durable consumers provide transport replay while the SSE ring provides
attach-before/after and `Last-Event-ID` replay for HTTP clients.

Payload (`RunEventPayload`):

```json
{
  "run_id":           "<uuid>",
  "attempt_id":       "<uuid>",
  "sequence":         639204260290755790,
  "occurred_at":      "2026-07-23T17:53:49Z",
  "story_id":         "<uuid|null>",
  "status":           "Succeeded|Failed|Cancelled|Timeout",
  "exit_code":        12,
  "duration_seconds": 2.5,
  "progress":         { "message": "Agent execution started.", "percent": 0 },
  "output":           { "stream": "stdout", "line": "Iteration 2/4" },
  "schema_version":   5
}
```

For HTTP / MCP / CLI consumers that want a per-run filtered view of the same stream:

- **HTTP**: `GET /api/runs/{id}/events` returns NDJSON (one `RunEvent` per line, flushed). Send `Last-Event-ID: <sequence>` to resume after the last observation. It closes when the run hits terminal (with a final drain pass).
- **MCP**: `run.events` tool — `IAsyncEnumerable<RunEventDto>`, identical semantics.
- **CLI**: `andy-containers-cli runs events <id>` colour-codes each line.

The shared implementation lives in `RunEventStream.AsyncEnumerate`. Its cursor
uses the payload sequence, not timestamps, so two events created in one
transaction cannot hide each other. `attempt_id` identifies a concrete retry;
`correlation_id` remains the root that groups related attempts.

### Mid-run output stream (F4.1, rivoli-ai/conductor#1934)

For a live line-by-line view in addition to the lifecycle/progress stream,
andy-containers exposes the agent's stdout/stderr as it is produced:

```
GET /api/runs/{id}/output      (text/event-stream, run:read)
GET /api/containers/{id}/logs  (text/event-stream, container:read)
```

Wire format (SSE), one frame per output line, mirroring the build-progress SSE bus (IM9 / `InMemoryBuildEventBus`):

```
id: <sequence>
event: log
data: {"runId":"...","attemptId":"...","sequence":639204260290755800,"stream":"stdout","line":"Iteration 2/4","timestamp":"2026-..."}

```

- `id:` and `data.sequence` are the shared monotonic per-run sequence; `data.attemptId` matches lifecycle NATS/NDJSON events. A reconnecting client sends the last id it saw in `Last-Event-ID`; the server replays buffered lines **after** that id. Sequence gaps are expected when lifecycle events occupy ids. If the requested id has aged out of the bounded ring buffer, replay restarts from the oldest buffered line and the client reconciles via the run snapshot.
- `stream` distinguishes `stdout` from `stderr`.
- The container-scoped route accepts `follow`, `tail`, and `since`; an idle
  following connection emits an SSE heartbeat comment every 15 seconds.
- Container-log authorization is enforced before streaming. Fleet lifecycle
  replay from `GET /api/containers/events` is independently filtered
  server-side to containers visible to the principal.
- The stream **closes** once the run reaches a terminal status and the buffer is drained — same terminal-stop contract as `RunEventStream`. A subscriber attaching after the run is already terminal replays the buffer then closes immediately (never hangs). Unknown run id → `404`.
- Run-scoped tokens (`ANDY_TOKEN`, see *Run-scoped credentials* below) are **redacted** (`RunOutputRedactor`) before any line reaches the wire — both the literal bearer (known to the runner via the idempotent `ITokenIssuer.MintAsync`) and the defensive `ANDY_TOKEN=<value>` / `export` / JSON env-echo shapes.

Mechanics: `HeadlessRunner` drives the container's **streaming** exec (`IContainerService.ExecStreamingAsync` → `IInfrastructureProvider.ExecStreamingAsync`) instead of the buffered overload when an `IRunOutputBus` is wired. The Docker provider reads the multiplexed exec stream chunk-by-chunk and emits each complete line; providers that can't stream incrementally (Apple, cloud) fall back to the interface-default which replays the final buffered output line-by-line. Either way no new Docker-Engine verb is introduced (decision #17 — the same `ExecCreate` + `StartAndAttach` surface). Each line is published, redacted, to `IRunOutputBus` (`InMemoryRunOutputBus` — a per-run ring buffer + multicast fan-out, a direct sibling of `InMemoryBuildEventBus`); the runner marks the stream terminal on every exit path. The SSE serialisation lives in `RunOutputSse` so the wire format, `Last-Event-ID` resumption and terminal-stop are defined once. See ADR 0003.

## Permissions

| Scope | Granted to | Used by |
|---|---|---|
| `run:write` | Admin, Editor | `POST /api/runs`, `run.create`, `runs create` |
| `run:read` | Admin, Editor, Viewer | `GET /api/runs/{id}`, `GET /api/runs/{id}/events`, `GET /api/runs/{id}/output`, `run.get`, `run.events`, `runs get`, `runs events` |
| `run:execute` | Admin, Editor | `POST /api/runs/{id}/cancel`, `run.cancel`, `runs cancel` |

Catalog source of truth: `src/Andy.Containers/Models/Permissions.cs` (AP8 added the run scopes).

## Run-scoped credentials

When the bound profile carries `SecretsScope.RunScoped`, AP10's `ITokenIssuer` mints a token at config time and injects three env vars into the agent's environment via the AQ1 config:

- `ANDY_TOKEN` — bearer token, run-scoped.
- `ANDY_PROXY_URL` — egress mediator (`Secrets:ProxyUrl` in appsettings).
- `ANDY_MCP_URL` — platform MCP server (`Secrets:McpUrl`).

The token is revoked on every terminal observation (Succeeded / Failed / Cancelled / Timeout) — the post-condition "no live run-scoped token after a run is terminal" holds regardless of exit path.

## Failure modes

| Symptom | Likely cause | Where to look |
|---|---|---|
| 201 with `Status: Pending` and never advances | Configurator failed; row persisted but AP5 didn't dispatch. | Application logs at `RunConfigurator: ...`. |
| 201 with `Status: Failed` and `error: "Run {id} has no ContainerId"` | AP5 dispatcher couldn't select/provision a container. | Workspace's `DefaultContainer` exists? Profile compatible with workspace? |
| Cancel returns 200 but the row stays `Running` | Runner ignored the linked CTS for >30 s; controller forced the transition. | `RunsController` log: "cancel grace expired before runner terminal write". |
| Stream closes immediately with no events | Run was already terminal when the stream opened. The `RunEventStream` final-drain pass yielded everything in one tick then closed. | This is correct behaviour, not a bug. |
| Stream returns 404 | Run id is unknown. | `GET /api/runs/{id}` — does the row exist? |

## Cross-references

- **ADR 0003** — [Agent run execution](adr/0003-agent-run-execution.md).
- **Configurator** — [Spec → config mapping](run-configurator.md).
- **EnvironmentProfile catalog** — [environment-profiles.md](environment-profiles.md).
- **OpenAPI** — `openapi/containers-api.yaml` (`/api/runs*` paths and schemas).
- **Cross-service flow** — Epic AO (rivoli-ai/conductor#669) covers the conductor → andy-containers → andy-issues round-trip.
