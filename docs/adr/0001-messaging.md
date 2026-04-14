# ADR 0001 — Messaging

**Status.** Adopts the ecosystem messaging ADR authored in andy-tasks ([`andy-tasks/docs/adr/0001-messaging.md`](https://github.com/rivoli-ai/andy-tasks/blob/main/docs/adr/0001-messaging.md)). This document is a thin reference that enumerates the subjects andy-containers participates in and restates the two ecosystem-wide rules that matter to this service.

## What we publish

| Subject | Payload | When |
|---|---|---|
| `andy.containers.events.run.<runId>.finished` | `{ runId, storyId?, status, exitCode?, durationSeconds? }` | Container exits with status `success`. |
| `andy.containers.events.run.<runId>.failed` | `{ runId, storyId?, status, exitCode?, durationSeconds? }` | Container exits with non-zero status, or provisioning fails. |
| `andy.containers.events.run.<runId>.cancelled` | `{ runId, storyId?, status, durationSeconds? }` | Container destroyed externally (operator action, scheduled cleanup). |

`storyId` is propagated when the caller (typically andy-issues' `SandboxService`) stamps it on the container at create time. When absent, consumers know the run was not correlated to a backlog story.

Future kinds — `session.status`, `agent.stdout`, `build.progress` — will be added as additional subjects under `andy.containers.events.*` when the use cases land, not in this foundation PR.

## What we subscribe to

Nothing in this PR. The scaffolding supports `SubscribeAsync` but no consumer is registered yet. andy-issues Story 15.6 tracks its correlating consumer.

## Rules we inherit from the ecosystem ADR

1. **Commands on HTTP, events on NATS.** REST / MCP / gRPC remain the command path into this service. NATS is strictly for past-tense events fanning *out* to other services.
2. **Transactional outbox.** Domain changes and outbox rows commit together in a single EF transaction. The `OutboxDispatcher` background worker drains pending rows to the bus. At-least-once delivery.
3. **Consumers are idempotent by `msg-id`.** The outbox row id *is* the `MsgId` header, so reprocessing the same row never produces two downstream effects.
4. **No self-subscription.** andy-containers never subscribes to `andy.containers.events.*`. Cross-service events belong on the bus; intra-service notifications belong on in-process `IHostedService` / event handlers.
5. **Generation cap.** Headers carry a `Generation` counter, incremented each time a message is emitted in response to another. The bus drops messages with `Generation > 10` as a runtime circuit breaker.

## What this repo contributes

This PR lands the **foundation** only:

- `IMessageBus` + `MessageHeaders` + `IncomingMessage` + `SubscriptionOptions` in `Andy.Containers.Messaging`.
- `InMemoryMessageBus` as the default provider — no NATS dependency yet.
- `OutboxEntry` entity + EF configuration + migration.
- `OutboxDispatcher` hosted service.
- DI extension `AddContainersMessaging()` that selects the provider from `Messaging:Provider` (currently only `InMemory`).

Follow-up PRs referenced from andy-containers#78 add:

- **NATS provider wire-up.** `NatsMessageBus`, `NatsStreamProvisioner`, `docker-compose.yml` `nats:2-alpine` service, integration tests env-gated by `ANDY_CONTAINERS_TEST_NATS`.
- **Publisher wiring.** Hook `OrchestrationService` / `ProvisioningWorker` lifecycle transitions to emit `run.*` events via the outbox. Thread `storyId` through `CreateContainerRequest`.
