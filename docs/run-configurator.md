# Run configurator: spec → config mapping

The **run configurator** turns an `AgentSpec` (resolved from andy-agents Epic W) plus a `Run` (the AP1 entity) into a `HeadlessRunConfig` written to disk for `andy-cli` to consume. It's the join point between the platform's idea of a run (state machine, lifecycle events, governance) and the agent runtime's idea of a run (instructions, model, tools, limits, output sink).

This page documents the field-by-field mapping. The contract is locked: the JSON shape must match AQ1 (the schema in the `andy-cli` repo) byte-for-byte; AP12's golden-file tests catch drift the moment a property is renamed, retyped, or added.

## Pipeline

```
RunsController.Create
   │
   │  CreateRunRequest → Run row (Pending)
   ▼
IRunConfigurator.ConfigureAsync(run, ct)
   │
   ├── IAndyAgentsClient.GetAgentAsync(run.AgentId, run.AgentRevision)   →  AgentSpec
   │
   ├── ITokenIssuer.MintAsync(run.Id)                                    →  RunToken
   │   (idempotent — configurator may retry without orphan tokens)
   │
   ├── MergeRunSecrets(spec.EnvVars, token, secrets)                     →  AgentSpec'
   │   (ANDY_TOKEN / ANDY_PROXY_URL / ANDY_MCP_URL injected here;
   │    agent's own EnvVars win on collision)
   │
   ├── IHeadlessConfigBuilder.Build(run, spec')                          →  HeadlessRunConfig
   │   (validates AQ1 closures: provider enum, transport oneOf,
   │    required-string non-empty, run id non-empty)
   │
   └── IHeadlessConfigWriter.WriteAsync(config)                          →  on-disk path
       (atomic write, returns the path AP6 spawns andy-cli against)
```

A failure at any stage returns a structured `RunConfiguratorResult.Fail(reason)` rather than throwing — the controller logs the reason and leaves the row Pending so a future retry can succeed. AP6 inspects `Run.ContainerId` at spawn time; a row with no container short-circuits to `Failed`.

## Field mapping

The builder lives in `src/Andy.Containers/Configurator/HeadlessConfigBuilder.cs`. The mapping below is exhaustive; AP12 fixtures exercise every combination of optional fields.

### Run-level fields (`HeadlessRunConfig`)

| Output field | Source | Notes |
|---|---|---|
| `schema_version` | constant `1` | Bump in lockstep with AQ1's schema. |
| `run_id` | `Run.Id` | Must be non-empty (`ArgumentException` otherwise). |
| `policy_id` | `Run.PolicyId` | Optional. Null when no policy bound. |

### Agent block (`HeadlessAgent`)

| Output field | Source | Notes |
|---|---|---|
| `agent.slug` | `AgentSpec.Slug` | Required. |
| `agent.revision` | `AgentSpec.Revision` | Optional pin; null = head. |
| `agent.instructions` | `AgentSpec.Instructions` | Required, min length 1 — empty throws. |
| `agent.output_format` | `AgentSpec.OutputFormat` | Optional. Hint to the agent for structured output (e.g. `json-triage-output-v1`). |

### Model block (`HeadlessModel`)

| Output field | Source | Notes |
|---|---|---|
| `model.provider` | `AgentSpec.Model.Provider` | Required, must be in `{anthropic, openai, google, cerebras, local}`. Unknown throws. |
| `model.id` | `AgentSpec.Model.Id` | Required, non-empty. |
| `model.api_key_ref` | `AgentSpec.Model.ApiKeyRef` | Optional. Format: `env:NAME` or `secret:provider/path`. Elided when null (per AQ1 schema). |

### Tools (`HeadlessTool[]`)

Each `AgentSpecTool` maps to one entry, discriminated by `Transport`:

- **MCP** — `transport: "mcp"`, `endpoint: <url>`. Endpoint required; missing throws.
- **CLI** — `transport: "cli"`, `binary: <path>`, `command: <string[] | null>`. Binary required; missing throws. Command elided when empty.

Unknown transports (anything other than `"mcp"` or `"cli"`) throw at the builder layer — the schema enforces the same `oneOf` shape.

### Workspace (`HeadlessWorkspace`)

| Output field | Source | Notes |
|---|---|---|
| `workspace.root` | constant `"/workspace"` | Container-side mount point. |
| `workspace.branch` | `Run.WorkspaceRef.Branch` | Optional; flows from the workspace ref the caller specified at submit time. |

### Output (`HeadlessOutput`)

| Output field | Source | Notes |
|---|---|---|
| `output.file` | constant `"/workspace/.andy-run/output.json"` | Path andy-cli atomically writes the agent's structured response to. Read by Conductor / consumers. |
| `output.stream` | constant `"stdout"` | Where structured events are emitted (parsed by AP6). |

### Event sink (`HeadlessEventSink`)

| Output field | Source | Notes |
|---|---|---|
| `event_sink.nats_subject` | `andy.containers.events.run.{Run.Id}.progress` | Where AQ3 (the andy-cli runtime) pushes structured progress events. AP6's terminal subjects (`finished` / `failed` / `cancelled` / `timeout`) sit on the same prefix. |

### Boundaries (`string[] | null`)

`AgentSpec.Boundaries` round-trips when present, elided when empty/null. Format is policy-engine-defined (`branch:feature/*`, `fs:/workspace/**`, `read-only`, etc.); the configurator does not interpret values.

### EnvVars (`Dictionary<string,string> | null`)

Three layers compose the final dictionary in `RunConfigurator.MergeRunSecrets`:

1. **Platform-injected (AP10):**
    - `ANDY_TOKEN` from `ITokenIssuer.MintAsync(run.Id)`.
    - `ANDY_PROXY_URL` from `Secrets:ProxyUrl` in appsettings (skipped when null/empty).
    - `ANDY_MCP_URL` from `Secrets:McpUrl` (skipped when null/empty).
2. **Agent-supplied (`AgentSpec.EnvVars`):** merged on top.
3. **Collision rule:** the agent's value wins. An agent author who pins `ANDY_TOKEN` for a test fixture is not silently overridden by the platform — the collision is intentional, not the platform's call to break.

The merged dictionary becomes `HeadlessRunConfig.EnvVars`. Empty maps elide to null per AQ1's "absent vs. null" handling.

### Limits (`HeadlessLimits`)

| Output field | Source | Default |
|---|---|---|
| `limits.max_iterations` | `AgentSpec.Limits.MaxIterations` | 100 |
| `limits.timeout_seconds` | `AgentSpec.Limits.TimeoutSeconds` | 900 |

`timeout_seconds` couples to AP6's outer watchdog: `ExecAsync` is given `timeout_seconds + 30 s` as its ceiling, so the AQ3 internal timeout (exit code 4) fires first and we observe a `Timeout` outcome rather than a watchdog-killed `Failed`.

## Validation closures

The builder enforces these up-front so AP6 never has to load-and-reject a config it just wrote:

| Closure | Throws when |
|---|---|
| `Run.Id` non-empty | `Run.Id == Guid.Empty` |
| `agent.instructions` non-empty | null / whitespace |
| `model.provider` in allowlist | unknown provider |
| `model.id` non-empty | null / whitespace |
| MCP tool has `endpoint` | null / whitespace |
| CLI tool has `binary` | null / whitespace |
| Tool transport in `{"mcp", "cli"}` | unknown transport |

All throw `ArgumentException` with a message naming the offending field. `RunConfigurator.ConfigureAsync` catches and converts to `RunConfiguratorResult.Fail`.

## Snapshot conformance

`tests/Andy.Containers.Api.Tests/Configurator/HeadlessConfigBuilderGoldenFileTests.cs` (AP12) builds the config for three representative agent types — `triage`, `plan`, `execute` — and compares the serialised JSON byte-for-byte against fixtures under `tests/.../Configurator/__snapshots__/`. Each fixture exercises a different combination of optional fields:

- **triage** — full suite (api_key_ref, boundaries, mcp + cli tools).
- **plan** — null api_key_ref + null boundaries (elision path).
- **execute** — env_vars + multiple mcp tools + cli tool with binary.

When a snapshot diff fails, treat it as a contract change: inspect the diff, decide whether the change is intentional, and update both the fixture and the AQ1 schema in lockstep.

## Cross-references

- **AP3** — `IRunConfigurator` / `RunConfigurator`.
- **AP4** — `IHeadlessConfigBuilder` / `HeadlessConfigBuilder` (validation closures).
- **AP10** — `ITokenIssuer` / `SecretsOptions` / `EnvVarNames` (platform env-var injection).
- **AP12** — `HeadlessConfigBuilderGoldenFileTests` (golden-file snapshots).
- **AQ1 (andy-cli)** — JSON schema source of truth in the andy-cli repo.
