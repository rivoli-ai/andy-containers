# CLI Reference

## Installation

```bash
dotnet tool install -g Andy.Containers.Cli
```

Or run from source:

```bash
dotnet run --project src/Andy.Containers.Cli -- <command>
```

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

## Container Management

```bash
# List all containers
andy-containers list

# Create a container
andy-containers create --template dotnet-8-alpine --name my-dev

# Create with code assistant and model
andy-containers create --template dotnet-8-alpine --name ai-dev \
  --code-assistant Aider --model gpt-4o --base-url https://openrouter.ai/api/v1

# Show container details
andy-containers info <id>

# Start / Stop / Destroy
andy-containers start <id>
andy-containers stop <id>
andy-containers destroy <id>

# Execute a command
andy-containers exec <id> "dotnet --version"

# Show resource usage
andy-containers stats <id>
```

## Terminal Connect

```bash
# SSH into a running container (native terminal)
andy-containers connect <id>
```

Uses SSH when available (port 22 auto-mapped). Falls back to suggesting `docker exec` if SSH isn't available.

## Global Options

| Option | Default | Description |
|--------|---------|-------------|
| `--api-url` | https://localhost:5200 | API server URL |
| `--help` | — | Show help |
| `--version` | — | Show version |

## Credentials

Stored in `~/.andy/credentials.json` with Unix file permissions 600.
