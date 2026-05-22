---
title: Runtime Backends
order: 4
tags: [docker, apple-containers]
---

# Runtime Backends

Andy Containers abstracts the underlying container runtime so you can target different backends without changing your workload definitions.

## Supported Backends

### Docker

The most common backend. Connects to a Docker Engine via the Unix socket or TCP.

- **Best for:** Linux servers, CI/CD, local development.
- **Requirements:** Docker Engine 24.0+ with API version 1.43+.
- **Configuration:** Set the `DOCKER_HOST` environment variable or provide a socket path in the provider configuration.

### Apple Containers

Native container runtime for macOS leveraging Apple's virtualization framework.

- **Best for:** macOS developers who need Linux containers without Docker Desktop.
- **Requirements:** macOS 14+ on Apple Silicon.
- **Features:** Rosetta translation for x86 images, seamless filesystem sharing, and low overhead.

## Provider Configuration

Providers are registered at the organization level. Each workspace selects one provider to use:

```json
{
  "name": "docker-prod",
  "type": "docker",
  "endpoint": "unix:///var/run/docker.sock",
  "credentials": {
    "registry": "ghcr.io",
    "username": "robot",
    "password": "***"
  }
}
```

## Switching Backends

You can migrate a workspace to a different provider by updating its `providerId`. Existing runs continue on the old provider, while new runs target the new one.

## Resource Quotas

Each provider can enforce quotas:

| Quota        | Description                         |
|--------------|-------------------------------------|
| CPU          | Max vCPUs per run.                  |
| Memory       | Max RAM per run.                    |
| Disk         | Max ephemeral disk per run.         |
| Concurrent   | Max simultaneous runs per workspace.|

Contact your organization administrator to request quota increases.
