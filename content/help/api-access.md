---
title: API Access
order: 5
tags: [api, integrations]
---

# API Access

Andy Containers exposes a RESTful API for all operations. You can interact with it using HTTP clients, the Conductor app, or the CLI.

## Base URL

```
https://api.andy.ai/api/
```

## Authentication

Include a bearer token in every request:

```bash
curl https://api.andy.ai/api/workspaces \
  -H "Authorization: Bearer <token>"
```

Tokens are scoped to your identity and carry organization membership claims.

## Key Endpoints

| Endpoint                        | Description                     |
|---------------------------------|---------------------------------|
| `GET /api/workspaces`           | List workspaces.                |
| `POST /api/workspaces`          | Create a workspace.             |
| `GET /api/images`               | List images in a workspace.     |
| `POST /api/images/build`        | Build an image from a template. |
| `GET /api/runs`                 | List runs.                      |
| `POST /api/runs`                | Start a new run.                |
| `GET /api/runs/{id}/logs`       | Stream run logs.                |
| `GET /api/runs/{id}/terminal`   | Attach to an interactive terminal. |

## Pagination

List endpoints support cursor-based pagination using `cursor` and `limit` query parameters:

```bash
curl "https://api.andy.ai/api/runs?limit=20&cursor=eyJpZCI6InJ1bl8xMjMifQ"
```

## Rate Limits

- **Authenticated:** 1,000 requests per minute.
- **Unauthenticated:** Not allowed.

Rate limit headers are included in every response:

```
X-RateLimit-Limit: 1000
X-RateLimit-Remaining: 987
X-RateLimit-Reset: 1716301200
```

## Webhooks

Subscribe to workspace events via webhooks. Configure a webhook URL in your organization settings, and Andy Containers will POST event payloads for run state changes, image build completions, and quota alerts.

## SDKs

- **C# / .NET:** `Andy.Containers.Client` NuGet package.
- **Swift:** `ContainersClient` framework (bundled with Conductor).
- **Python:** `andy-containers` PyPI package.
- **CLI:** `andy containers` command group.

## Getting Help

If you encounter issues, consult the [Getting Started](getting-started) guide or reach out via the `#containers-support` Slack channel.
