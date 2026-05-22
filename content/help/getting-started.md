---
title: Getting Started
order: 1
tags: [onboarding, quickstart]
---

# Getting Started

Welcome to **Andy Containers** — the container orchestration and workspace management platform for Rivoli AI.

## What is Andy Containers?

Andy Containers lets you define, build, run, and manage containerized workloads through a unified API and UI. Whether you need isolated development environments, reproducible build pipelines, or ephemeral compute, Andy Containers provides the primitives.

## Quick Start

1. **Create an Organization** — All resources live inside an organization. Use the `/api/organizations` endpoint or the Conductor app to create one.

2. **Add a Runtime Provider** — Configure a runtime backend such as Docker or Apple Containers so workloads have somewhere to run.

3. **Create a Workspace** — Workspaces are the top-level boundary for your containers. Each workspace maps to a runtime and holds images, templates, and runs.

4. **Push or Build an Image** — Upload a container image or use a template to build one inside the platform.

5. **Start a Run** — Execute your image as a run. Monitor logs, attach terminals, and inspect exit status through the API.

## Authentication

All API endpoints require a valid bearer token. Obtain one through the Rivoli AI identity provider and include it in the `Authorization` header:

```
Authorization: Bearer <token>
```

## Next Steps

- Read about [Workspaces](workspaces) to understand lifecycle and isolation.
- Explore [Templates](templates) for reusable image definitions.
- Learn about [Runtime Backends](runtime-backends) to configure where containers execute.
