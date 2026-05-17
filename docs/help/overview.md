---
title: Andy Containers Overview
slug: andy-containers-overview
order: 1
tags: [containers, workspaces, runtime]
---

# Andy Containers Overview

Andy Containers is the container orchestration service for the Andy ecosystem. It owns workspaces, templates, and the lifecycle of every container Conductor launches — local (Docker/Apple Containers) or remote (Rivoli AI Cloud).

## What it does

- Creates and destroys workspaces from templates (image, ports, env, volumes, multiplexer).
- Tracks container state and exposes lifecycle events the Conductor UI consumes via SSE.
- Routes IDE attach and terminal sessions through a controlled Docker-API surface that enforces RBAC per verb.
- Manages container images: pull, list, prune. Pulls authenticate against `andy-mcp-proxy`-fronted registries when configured.
- Reconciles drift — containers killed outside Conductor are detected on the next poll and surfaced as stopped.

## Key concepts

- **Workspace** — one user-facing unit: a container, its template, and the volumes it's attached to. Has its own lifecycle independent of the underlying container.
- **Template** — YAML or registry-published definition; the source of truth for what a new workspace gets.
- **Runtime backend** — `docker-passthrough` (local Docker), `apple-containers` (local Apple Containers), or `cloud-tunnel` (Rivoli AI Cloud over outbound WSS). Same Docker-API verb set across all three.

## Where it fits

Conductor's Workspaces tab talks to Containers for every read and write. Agent runs execute *inside* workspace containers, so Containers is on the critical path for the demo. Depends on Auth, RBAC, and Settings.

## Configuration

Template directories, default runtime, and image-pull credentials live under `andy.containers.*` keys in `andy-settings`. Conductor exposes the editable surface in **Settings → Runtime Defaults**.

## Troubleshooting

- **Workspace stuck in `Provisioning`** — image pull is slow or failed. Check the container log; if pull is the cause, verify the registry credential ref in `andy-settings`.
- **IDE attach fails with "container not found"** — the container died between list and attach. Re-poll the workspace list; the UI auto-recovers.
- **Apple Containers backend disabled** — macOS version doesn't support `container` framework (15+ required) or the entitlement is missing.
