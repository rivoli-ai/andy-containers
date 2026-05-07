# YAML Configuration

> All container templates, infrastructure providers, and dependencies are defined as YAML files.
> The database serves as the runtime store, synced from YAML sources.

## Design Principle

**YAML is the source of truth.** Configuration flows:

```
YAML files (git-managed) → API import → Database (runtime)
                         ← API export ← Database
```

This means:
- Templates, providers, and dependencies can be version-controlled in git
- Changes are reviewed via pull requests
- The database is populated from YAML on startup (seeding) or via CLI import
- The API and Web UI can also create/modify configs, which can be exported back to YAML

## Template Definition (YAML)

```yaml
# templates/full-stack.yaml
code: full-stack
name: Full Stack Development
description: Complete development environment with .NET, Python, Node.js, and Angular
version: "1.0.0"
base_image: ubuntu:24.04
catalog_scope: global
ide_type: code-server

gpu:
  required: false
  preferred: false

resources:
  cpu_cores: 4
  memory_mb: 8192
  disk_gb: 40

dependencies:
  - type: sdk
    name: dotnet-sdk
    version: "8.0.*"
    auto_update: true
    update_policy: patch

  - type: runtime
    name: python
    version: ">=3.12,<4.0"
    auto_update: true
    update_policy: minor

  - type: tool
    name: node
    version: "20.x"
    auto_update: true
    update_policy: minor

  - type: tool
    name: angular-cli
    version: "latest"
    auto_update: true
    update_policy: major

  - type: tool
    name: git
    version: "latest"
    auto_update: true
    update_policy: patch

  - type: tool
    name: code-server
    version: "latest"
    auto_update: true
    update_policy: minor

environment:
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"
  NODE_ENV: development

ports:
  8080: code-server
  3000: angular-dev-server
  5000: dotnet-api

scripts:
  init: |
    # Runs once when the container is first created
    git config --global init.defaultBranch main
  setup: |
    # Runs each time the container starts
    echo "Container ready"
  teardown: |
    # Runs before the container is stopped
    echo "Saving state..."

tags:
  - dotnet
  - python
  - node
  - angular
  - full-stack
```

## Imperative-style fields (M1.9 / Epic IM)

The fields below are **additive** to the declarative `dependencies:` model documented above. The declarative form covers most cases (typed dependencies the build engine knows how to install with version policies). The imperative fields are the escape hatch when the dependency abstraction doesn't fit — for example installing a code-assistant CLI via `npm install -g @anthropic-ai/claude-code`.

```yaml
# templates/global/conductor-terminal-claude-code.yaml
code: conductor-terminal-claude-code
name: Conductor Terminal — Claude Code
version: "1.0.0"

# Choose one of: base_image, from (deprecated alias), or extends.
base_image: ubuntu:22.04          # preferred
# from: ubuntu:22.04              # deprecated alias of base_image
extends: conductor-terminal-base  # optional — inherit base from a parent template

# Existing declarative model still works:
dependencies:
  - { type: tool, name: bash }
  - { type: tool, name: git }

# New imperative fields:
packages:                          # OS packages installed via the base image's package manager
  - curl
  - ca-certificates

files:                             # files copied into the image during build
  - source: install-assistants.sh  # multipart-upload logical name
    dest: /opt/conductor/install-assistants.sh
    mode: 0755                     # octal (or "0755" string form)

install:                           # shell commands run in order after files are copied
  - npm install -g @anthropic-ai/claude-code
  - chmod +x /opt/conductor/install-assistants.sh

entrypoint: /opt/conductor/entrypoint.sh

markers:                           # free-form metadata about what's baked into the image
  baked-assistants:
    - claude-code
```

### Field reference

| Field | Type | Required | Notes |
|---|---|---|---|
| `extends` | string (template code) | conditional | Optional. Resolved at register-time by walking the templates table. Cycles (A→B→A and longer) are rejected before any build is queued. The chain depth is capped at 16. |
| `from` | string (OCI ref) | conditional | **Deprecated** alias of `base_image:`. Emits a parse-time warning. Specifying both `base_image:` and `from:` is rejected as ambiguous. |
| `base_image` | string (OCI ref) | conditional | Preferred. Required unless `from:` or `extends:` is supplied. |
| `packages` | list of strings | optional | OS package names. The build backend picks the package manager (`apt-get`/`yum`/`apk`) based on the base image. |
| `files` | list of `{source, dest, mode?}` | optional | `source` is the multipart-upload logical name (the `files[<name>]` token). `dest` must be an absolute path. `mode` is octal in `[0, 07777]`. |
| `install` | list of strings | optional | Shell command lines run after `packages` are installed and `files` are copied. Each entry is one line passed to the build engine. |
| `entrypoint` | string | optional | Container `ENTRYPOINT`. Single-string form only in IM4; list-of-strings form is reserved for a future story. |
| `markers` | object (free-form) | optional | Caller-defined key/value metadata. Surfaced via `GET /api/images` so launch UIs can label images without introspecting the filesystem. |

### `extends:` resolution

`extends:` references another template by `code`. At register-time:

1. The parser captures the value verbatim.
2. The registration pipeline walks the chain via `TemplateExtendsCycleDetector`, looking each parent up in the templates table.
3. Cycles are rejected — the error message lists the full path so the offending template is easy to find.
4. Missing parents are rejected — the error names the unresolved code.
5. The chain depth is capped at 16; deeper chains are rejected with the same path-listing error.

If `extends:` is specified without `base_image:` / `from:`, the base image is inherited from the parent transitively.

### `from:` deprecation

`from:` is accepted **only** for backward compatibility with the issue body that triggered Epic IM (`#1022`). Use `base_image:` instead. The parser emits a single warning per template register; the parser does not strip `from:` from the YAML, so re-exported templates round-trip the deprecated key — operators should migrate explicitly.

## Provider Definition (YAML)

```yaml
# providers/local-docker.yaml
code: local-docker
name: Local Docker
type: docker
region: local
enabled: true

connection:
  endpoint: unix:///var/run/docker.sock
  # For remote Docker:
  # endpoint: tcp://docker-host:2376
  # tls_cert_path: /certs/cert.pem
  # tls_key_path: /certs/key.pem
  # tls_ca_path: /certs/ca.pem

capabilities:
  architectures: [arm64, amd64]
  operating_systems: [linux]
  max_cpu_cores: 8
  max_memory_mb: 16384
  max_disk_gb: 100
  gpu: false
  volume_mount: true
  port_forwarding: true
  exec: true
  streaming: true
  offline_build: true
```

```yaml
# providers/apple-container-local.yaml
code: apple-container-local
name: Local Apple Container
type: apple-container
region: local
enabled: true

connection:
  # Uses the macOS `container` CLI tool
  cli_path: /usr/local/bin/container

capabilities:
  architectures: [arm64]
  operating_systems: [linux]
  max_cpu_cores: 8
  max_memory_mb: 16384
  max_disk_gb: 50
  gpu: false
  volume_mount: true
  port_forwarding: true
  exec: true
  streaming: true
  offline_build: true
```

```yaml
# providers/azure-aci-prod.yaml
code: azure-aci-prod
name: Azure Container Instances (Production)
type: azure-aci
region: westeurope
enabled: true

connection:
  subscription_id: "${AZURE_SUBSCRIPTION_ID}"
  resource_group: andy-containers-prod
  # Authentication via Azure.Identity (managed identity, CLI, env vars)

capabilities:
  architectures: [amd64]
  operating_systems: [linux]
  max_cpu_cores: 4
  max_memory_mb: 16384
  max_disk_gb: 50
  gpu: true
  gpu_skus:
    - vendor: nvidia
      model: Tesla T4
      memory_mb: 16384
      count: 1
    - vendor: nvidia
      model: Tesla V100
      memory_mb: 16384
      count: 1
  volume_mount: true
  port_forwarding: true
  exec: true
  streaming: false
  offline_build: false
```

## Workspace Definition (YAML)

```yaml
# workspaces/my-project.yaml
name: My Project Workspace
description: Development workspace for my-project
git_repository_url: https://github.com/rivoli-ai/my-project.git
git_branch: main
template_code: full-stack

# Override template defaults
resources:
  cpu_cores: 8
  memory_mb: 16384

environment:
  PROJECT_NAME: my-project
  CUSTOM_VAR: custom-value
```

## File Organization

```
config/
├── templates/
│   ├── global/
│   │   ├── dotnet-8-vscode.yaml
│   │   ├── python-3.12-vscode.yaml
│   │   ├── angular-18-vscode.yaml
│   │   ├── full-stack.yaml
│   │   ├── full-stack-gpu.yaml
│   │   ├── andy-cli-dev.yaml
│   │   ├── agent-sandbox.yaml
│   │   └── agent-sandbox-ui.yaml
│   ├── organizations/
│   │   └── {org-code}/
│   │       └── {template-code}.yaml
│   ├── teams/
│   │   └── {team-code}/
│   │       └── {template-code}.yaml
│   └── users/
│       └── {user-id}/
│           └── {template-code}.yaml
├── providers/
│   ├── local-docker.yaml
│   ├── apple-container-local.yaml
│   ├── rivoli-eu-west.yaml
│   ├── azure-aci-prod.yaml
│   └── azure-aca-prod.yaml
└── workspaces/
    └── {workspace-name}.yaml
```

## CLI Commands for YAML Management

```bash
# Import templates from YAML directory
andy-containers templates import ./config/templates/

# Export all templates to YAML
andy-containers templates export ./config/templates/

# Import a single provider
andy-containers providers import ./config/providers/local-docker.yaml

# Validate YAML configuration
andy-containers config validate ./config/

# Sync database from YAML (idempotent)
andy-containers config sync ./config/

# Show diff between YAML and database
andy-containers config diff ./config/
```
