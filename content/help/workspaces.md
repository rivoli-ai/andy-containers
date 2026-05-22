---
title: Workspaces
order: 2
tags: [workspaces, containers]
---

# Workspaces

A **workspace** is the primary organizational boundary in Andy Containers. Every image, template, and run belongs to exactly one workspace.

## Workspace Lifecycle

| State    | Description                                           |
|----------|-------------------------------------------------------|
| Creating | Provisioning runtime resources (network, volumes).    |
| Active   | Ready to accept images, templates, and runs.          |
| Paused   | Existing runs are suspended; new runs are blocked.    |
| Archived | Read-only. Historical data retained, no new activity. |
| Deleting | Resources are being torn down.                        |

## Isolation Model

Workspaces provide logical isolation. Each workspace receives:

- A dedicated namespace on the runtime backend.
- Separate volume claims for persistent data.
- Independent network policies.
- Scoped credentials and secrets.

> **Tip:** For strong isolation between teams or customers, use separate workspaces rather than shared ones.

## Creating a Workspace

```bash
curl -X POST https://api.andy.ai/api/workspaces \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "ml-pipeline",
    "organizationId": "org_123",
    "providerId": "prov_docker_01",
    "description": "Workspace for ML training jobs"
  }'
```

## Managing Runs

A workspace accumulates runs over time. Use the API to list, filter, and clean up historical runs:

```bash
curl https://api.andy.ai/api/workspaces/ml-pipeline/runs \
  -H "Authorization: Bearer <token>"
```

## Deletion

Deleting a workspace is irreversible. All runs, images, templates, and volumes associated with the workspace are permanently removed after a grace period.
