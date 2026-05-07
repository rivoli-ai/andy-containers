# zot Ownership Contract

> **Status:** RFC. IM11 deliverable. Companion to [`image-management.md`](image-management.md). Closes the Phase 1 design loop on Epic IM.

This document writes down the split-of-responsibilities for the zot OCI registry across the four `andy-containers` deployment modes. The split is **convention-enforced** in v1 — there's no code that prevents another component from reaching into zot directly — but the Phase 0 abstractions are designed so that violating the convention surfaces as design pressure (an extra `IRegistryAdapter` registration that wasn't there before, an HTTP call that bypasses `IRegistryAdapter.PushAsync`, etc.).

## tl;dr

| Concern | Owner |
|---|---|
| Process lifecycle (start, supervise, restart, expose health) | **Conductor** — `ZotServiceConfig` in `rivoli-ai/conductor#1009` |
| Runtime config (storage path, ACL, OIDC, port) | **`andy-containers`** |
| HTTP API access (push, pull, list, delete) | **`andy-containers`** — the only consumer |

In **embedded mode** (the M1.9 path), the runtime config is effectively static — anonymous on `localhost:5050`, storage in the user's library — so Conductor keeps shipping the bootstrap config it already ships and `andy-containers` consumes zot without needing to mutate it. The split is **convention** in v1; it becomes **code** when multi-tenant zot lands in Phase 5.

In **cloud modes** (Rivoli Cloud and single-tenant customer deployments) Conductor is uninvolved — `andy-containers` runs in a cluster, zot scale-out runs in a cluster (or is replaced by the customer's mandated registry), and the Conductor desktop app simply targets the cloud `andy-containers` URL.

## Why this split

zot is an opinionated process: it reads a JSON config file at startup, writes blobs to a configured storage path, and serves the OCI Distribution v1.1 HTTP API. Two pieces of work need to happen for it to be useful in our stack:

1. **Run the binary.** Start the process, restart on crash, expose its `/v2/` health endpoint, tear it down on shutdown.
2. **Tell it what to do.** Generate the config file, populate ACLs as tenants come and go, mint OIDC trust as the auth issuer rotates keys, write blobs through the HTTP API.

These are different concerns with different lifecycle implications. **Conductor's `ServiceOrchestrator` is excellent at #1** — it already supervises every other bundled service the same way (`andy-auth`, `andy-rbac`, `andy-models`, …). Reusing it for zot avoids reinventing service-supervision in `andy-containers`.

**But zot is `andy-containers`'s tool.** The container service is the only thing that ever needs to write a blob, mutate an ACL, push a manifest. Letting Conductor own zot's *config* would mean every multi-tenant policy decision has to round-trip through Conductor — wrong shape for a backend concern.

Hence the split: **Conductor owns the process; `andy-containers` owns the meaning.**

## Embedded mode (M1.9 — the only mode in v1)

```
┌──────────────────────────────────────────────────────────────────┐
│ Conductor (macOS app)                                            │
│                                                                  │
│  ServiceOrchestrator                                             │
│  ├── ZotServiceConfig (rivoli-ai/conductor#1009)                 │
│  │     spawns: zot serve <bootstrap config>                      │
│  │     storage: ~/Library/Application Support/.../registry/      │
│  │     port: localhost:5050                                      │
│  │     auth: anonymous (no OIDC, no htpasswd)                    │
│  ├── AndyAuthService                                             │
│  ├── AndyRbacService                                             │
│  └── AndyContainersService                                       │
│         │                                                        │
│         │ HTTP                                                   │
│         ▼                                                        │
│  andy-containers (.NET, embedded)                                │
│    └── LocalZotAdapter (rivoli-ai/andy-containers#260)           │
│           HTTP POST/HEAD/GET/DELETE → localhost:5050/v2/...      │
└──────────────────────────────────────────────────────────────────┘
```

### What Conductor does

- Ships the zot binary in `Conductor/Resources/Services/zot/` (fetched at build time by `scripts/fetch-zot.sh`).
- Materialises the bootstrap config from a templated file when the user first launches Conductor (`Conductor/Resources/Services/zot/config.template.json`), substituting the per-user storage path.
- Spawns `zot serve <config>` as a managed `ServiceProcessManager` child.
- Restarts zot on unexpected exit; surfaces health to the global health bar via `GET /v2/`.

### What `andy-containers` does

- Reads `RegistryConfigurationOptions:Registries` from its own appsettings.json.
- The default options ship `local-zot` pointing at `http://localhost:5050` — the address Conductor's bootstrap config binds to.
- Pushes built images via `LocalZotAdapter.PushAsync` (under the hood: `docker tag <local> localhost:5050/<repo>:<tag>` + `docker push <ref>`).
- Reads / lists / untags via the HTTP API.
- **Does not write to the bootstrap config file.** v1 has no multi-tenant policy to express; the static anonymous-on-localhost config is enough.

### What nobody does (in v1)

- Mutate the zot config file at runtime.
- Send `SIGHUP` to zot to reload config.
- Set up OIDC trust to `andy-auth`.
- Configure repository-level ACLs.

These are all in scope for **Phase 5 (multi-tenant Rivoli Cloud)**, where `andy-containers` will grow a config-writer that materialises the zot scale-out config from the tenants table.

## Single-tenant cloud (customer deployment)

zot is **not part of this picture**. The customer mandates a registry — Artifactory, ACR, ECR, Harbor, GAR — and `andy-containers` talks to it via the corresponding `IRegistryAdapter` (Phase 3 stories). Conductor doesn't supervise anything; the desktop app simply points its API URL at `https://andy-containers.<customer>.cloud`.

## Multi-tenant Rivoli Cloud (Phase 5)

zot scale-out cluster runs in Kubernetes via a Helm subchart added to Epic RC's chart (story IM23). `andy-containers` writes the cluster's config from its tenants table (story IM24). Conductor is uninvolved.

## Why convention-only enforcement in v1

Two reasons:

1. **There's no other consumer to police.** In v1, only `andy-containers` knows zot exists. There's no other component that could accidentally write to its config file or push a blob via the HTTP API. The risk of accidental coupling is low.
2. **Multi-tenant doesn't ship in Phase 1.** Until Phase 5 there's no real config to defend. Adding boilerplate (a sentinel file, a permission check, a wrapper service) before there's a violation to catch is premature.

When Phase 5 starts:

- The bootstrap config moves from "Conductor ships it" to "andy-containers materialises it from the tenants table at startup."
- `LocalZotAdapter` grows the ability to mutate the config + reload via `SIGHUP`.
- Architecture-guard tests verify nothing else in the codebase imports the zot HTTP client directly.

## How this split is observable today

- Look at `rivoli-ai/conductor#1009` — `ZotServiceConfig.swift`. That's all of Conductor's involvement: a `ServiceConfiguration` registration, a launch script, a bootstrap config template.
- Look at `rivoli-ai/andy-containers#260` — `LocalZotAdapter.cs`. That's all of andy-containers' involvement: an `IRegistryAdapter` that talks HTTP to `localhost:5050`.
- There is no third file. No code on either side writes the config or supervises the process from the wrong side of the split.

## Test that proves the contract

The IM11 round-trip integration test in `Andy.Containers.Integration.Tests/Build/EmbeddedRoundTripTests.cs` exercises the full pipeline against a real zot:

1. Boot zot in a Docker container on a random port (test fixture).
2. Configure `andy-containers` to point at that zot.
3. Register a template via `POST /api/templates/from-yaml`.
4. Trigger a build via `POST /api/images/{templateId}/build`.
5. Subscribe to the SSE event stream; assert ordered events ending in `complete`.
6. Re-trigger the same build; assert `status: cached` (no rebuild).
7. List images via `GET /api/images`; confirm one artifact, one reference.

The test is gated on Docker availability — it skips cleanly when no `docker` CLI is found, so CI environments without Docker still get the rest of the test suite.

## References

- IM1 architecture memo: [`image-management.md`](image-management.md)
- Conductor zot supervision: [`rivoli-ai/conductor#1009`](https://github.com/rivoli-ai/conductor/issues/1009)
- IM6 LocalZotAdapter: [`rivoli-ai/andy-containers#260`](https://github.com/rivoli-ai/andy-containers/issues/260)
- IM11 round-trip test: [`rivoli-ai/andy-containers#265`](https://github.com/rivoli-ai/andy-containers/issues/265)
- Phase 5 multi-tenant scale-out: deferred (IM23–IM25 in [`#249`](https://github.com/rivoli-ai/andy-containers/issues/249))
