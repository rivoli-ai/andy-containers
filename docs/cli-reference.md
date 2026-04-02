# CLI Reference

## Installation

```bash
dotnet tool install -g Andy.Containers.Cli
```

Or run from source:

```bash
dotnet run --project src/Andy.Containers.Cli -- <command>
```

The CLI is built with **System.CommandLine** and **Spectre.Console** for rich terminal output.

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
andy-containers create --name my-dev --template dotnet-8-alpine

# Create with a specific provider
andy-containers create --name my-dev --template dotnet-8-alpine --provider local-docker

# Create with code assistant and model
andy-containers create --name ai-dev --template dotnet-8-alpine \
  --code-assistant Aider --model gpt-4o

# Show container details
andy-containers info <id>

# Start / Stop / Destroy
andy-containers start <id>
andy-containers stop <id>
andy-containers destroy <id>

# Execute a command
andy-containers exec <id> "dotnet --version"

# SSH into a running container
andy-containers ssh <id>

# Show resource usage (CPU, RAM, disk with visual bars)
andy-containers stats <id>
```

## Template Catalog

```bash
# List all available templates
andy-containers templates list

# Show template details
andy-containers templates info <code>
```

## Provider Management

```bash
# List infrastructure providers
andy-containers providers list
```

## Command Summary

| Command | Description |
|---------|-------------|
| `auth login` | Authenticate via OAuth Device Flow or direct token |
| `auth logout` | Clear stored credentials |
| `auth status` | Show current user and token expiry |
| `list` | List all containers with status and uptime |
| `create` | Create a new container from a template |
| `start <id>` | Start a stopped container |
| `stop <id>` | Stop a running container |
| `destroy <id>` | Permanently destroy a container |
| `exec <id> <cmd>` | Execute a command in a container |
| `ssh <id>` | SSH into a running container |
| `info <id>` | Show detailed container information |
| `stats <id>` | Show CPU, RAM, and disk usage |
| `templates list` | Browse the template catalog |
| `templates info <code>` | Show template details |
| `providers list` | List infrastructure providers |

## Global Options

| Option | Default | Description |
|--------|---------|-------------|
| `--api-url` | https://localhost:5200 | API server URL |
| `--help` | -- | Show help |
| `--version` | -- | Show version |

## Credentials

Stored in `~/.andy/credentials.json` with Unix file permissions 600.
