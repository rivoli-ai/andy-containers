# CLI Reference

The `andy-containers` CLI is the user-facing entry point to the same surface served by REST (`/api/*`) and MCP. The full transport-by-transport coverage map lives in [API / MCP / CLI parity](api-mcp-cli-parity.md).

## Installation

```bash
dotnet tool install -g Andy.Containers.Cli
```

Or from source:

```bash
dotnet run --project src/Andy.Containers.Cli -- <command>
```

Built on **System.CommandLine** (parsing) and **Spectre.Console** (output).

## Global options

| Option | Default | Description |
|---|---|---|
| `--api-url` | `https://localhost:5200` | API server URL. Persisted via `auth login --api-url`. |
| `--format` / `-o` | `table` | Output format. `table` is the default human view; `json` emits one record per response, suitable for piping to `jq`. Supported on commands listed below. |
| `--help` | — | Show help for any command. |
| `--version` | — | Print CLI version. |

`--format` is honoured by: `environments {list,get}`, `workspace {list,get,create}`, `templates {list,info}`, `providers {list,health}`. `templates definition` always prints raw YAML to stdout (no markup, no envelope) so `> file.yaml` produces a clean file.

## Exit codes

The CLI follows the Epic AN exit-code contract:

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Generic error |
| `2` | Usage error (bad flag, malformed GUID, invalid enum value) |
| `3` | Authentication required / token expired |
| `4` | Resource not found |

## Authentication

```bash
# OAuth Device Flow (like gh auth login)
andy-containers auth login

# Direct token (development)
andy-containers auth login --token <access-token> --api-url https://localhost:5200

# Check status
andy-containers auth status

# Sign out
andy-containers auth logout
```

Credentials are stored in `~/.andy/credentials.json` with permission bits `600`.

## Containers

```bash
# List
andy-containers list

# Create
andy-containers create --name my-dev --template dotnet-8-alpine
andy-containers create --name my-dev --template dotnet-8-alpine --provider local-docker
andy-containers create --name ai-dev --template dotnet-8-alpine \
  --code-assistant Aider --model gpt-4o

# Inspect
andy-containers info <id>
andy-containers stats <id>          # CPU / RAM / disk with bars

# Lifecycle
andy-containers start <id>
andy-containers stop <id>
andy-containers destroy <id>

# Run a command (one-shot)
andy-containers exec <id> "dotnet --version"

# Interactive shell
andy-containers connect <id>        # prefers SSH, falls back to web terminal
```

`create` flags: `--name` (required), `--template` (required), `--provider`, `--code-assistant`, `--model`, `--base-url`.

## Workspaces

```bash
andy-containers workspace list [--owner <id>] [--organization <guid>] [-o json]
andy-containers workspace get <id> [-o json]

# --environment-profile is required (X5 governance anchor: each
# workspace pins one EnvironmentProfile slug for life).
andy-containers workspace create <name> \
  --environment-profile headless-container \
  [--description ...] [--organization <guid>] [--team <guid>] \
  [--git-repo <url>] [--branch <name>] [-o json]

andy-containers workspace delete <id>
```

`update` is intentionally absent — the API's `UpdateWorkspaceDto` doesn't expose the governance fields and operators rarely need it from CLI.

## Runs (Epic AP)

```bash
# Submit a run. --environment-profile is required.
andy-containers runs create <agent-slug> \
  --environment-profile <guid> \
  [--mode Headless|Terminal|Desktop] \
  [--agent-revision <int>] \
  [--workspace <guid> [--branch <name>]] \
  [--policy <guid>] \
  [--correlation-id <guid>]

andy-containers runs get <id>
andy-containers runs cancel <id>
andy-containers runs events <id>     # NDJSON stream until terminal state
```

`runs events` follows the run until it hits a terminal state (`finished` / `failed` / `cancelled` / `timeout`) and prints colour-coded lifecycle events with timestamps.

## Environment profiles (Epic X)

```bash
andy-containers environments list [--kind HeadlessContainer|Terminal|Desktop] [-o json]
andy-containers environments get <code> [-o json]    # e.g. 'headless-container'
```

Lookup is by slug — slugs are the stable identifier; ids are not exposed at the CLI.

## Templates

```bash
andy-containers templates list [--scope Global|Organization|Team|User] [--search <text>] [-o json]
andy-containers templates info <code> [-o json]
andy-containers templates definition <code-or-id>     # raw YAML to stdout
```

`templates definition` accepts either a slug or a GUID. CRUD + publish + image-build remain admin-only via REST/UI — see the [parity matrix](api-mcp-cli-parity.md#templates).

## Providers

```bash
andy-containers providers list [--organization <guid>] [-o json]

# Live probe — issues a real health check against the underlying provider.
andy-containers providers health <provider-id> [-o json]
```

CRUD remains admin-only via REST. Only ops-shaped operations (`list`, `health`) live on the CLI.

## Command summary

| Command | Description |
|---|---|
| `auth login` | OAuth Device Flow or direct-token sign-in |
| `auth logout` | Clear stored credentials |
| `auth status` | Show current user and token expiry |
| `list` | List containers |
| `create` | Create a container from a template |
| `info <id>` | Show container details |
| `stats <id>` | CPU / RAM / disk usage |
| `start <id>` / `stop <id>` / `destroy <id>` | Container lifecycle |
| `exec <id> <cmd>` | Run a command in a container |
| `connect <id>` | Interactive shell (SSH preferred, web terminal fallback) |
| `workspace list` / `get` / `create` / `delete` | Workspace CRUD (no update — see above) |
| `runs create` / `get` / `cancel` / `events` | Agent-run lifecycle (Epic AP) |
| `environments list` / `get` | EnvironmentProfile catalog (Epic X) |
| `templates list` / `info` / `definition` | Template catalog browse |
| `providers list` / `health` | Infrastructure provider list + live probe |

## Cross-references

- [API / MCP / CLI parity](api-mcp-cli-parity.md) — what is/isn't surfaced on each transport
- [Run docs](runs.md) — agent-run lifecycle and event semantics
- [Run configurator](run-configurator.md) — AQ1/AQ2 contract with `andy-cli`
- ADR 0002 — environment profiles (`docs/adr/0002-environment-profiles.md`)
- ADR 0003 — agent run execution (`docs/adr/0003-agent-run-execution.md`)
