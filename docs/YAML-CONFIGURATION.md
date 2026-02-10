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
