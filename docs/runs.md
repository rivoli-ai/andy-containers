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

Every terminal observation appends a row to the `OutboxEntries` table in the same EF transaction as the state-machine transition. The `OutboxDispatcher` background service drains pending rows to NATS at-least-once (per ADR 0001). Subjects:

```
andy.containers.events.run.{runId}.finished
andy.containers.events.run.{runId}.failed
andy.containers.events.run.{runId}.cancelled
andy.containers.events.run.{runId}.timeout
```

Payload (`RunEventPayload`):

```json
{
  "run_id":           "<uuid>",
  "story_id":         "<uuid|null>",
  "status":           "Succeeded|Failed|Cancelled|Timeout",
  "exit_code":        12,
  "duration_seconds": 2.5
}
```

For HTTP / MCP / CLI consumers that want a per-run filtered view of the same stream:

- **HTTP**: `GET /api/runs/{id}/events` returns NDJSON (one `RunEvent` per line, flushed). Closes when the run hits terminal (with a final drain pass).
- **MCP**: `run.events` tool — `IAsyncEnumerable<RunEventDto>`, identical semantics.
- **CLI**: `andy-containers-cli runs events <id>` colour-codes each line.

The shared implementation lives in `RunEventStream.AsyncEnumerate` so all three surfaces have one polling loop and one terminal-stop policy.

## Permissions

| Scope | Granted to | Used by |
|---|---|---|
| `run:write` | Admin, Editor | `POST /api/runs`, `run.create`, `runs create` |
| `run:read` | Admin, Editor, Viewer | `GET /api/runs/{id}`, `GET /api/runs/{id}/events`, `run.get`, `run.events`, `runs get`, `runs events` |
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
