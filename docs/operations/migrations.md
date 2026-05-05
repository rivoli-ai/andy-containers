# Database migrations

Andy Containers can apply EF Core migrations two ways: in-process at API
startup, or out-of-band via the `andy-containers-api migrate` console
entry. Pick the mode that matches your deployment shape.

## When to use each

| Deployment shape | Mode | Why |
|---|---|---|
| `docker compose` (single API container) | Startup migration | One process, no race. Default — no extra wiring needed. |
| `dotnet run` (developer machine) | Startup migration | Same. |
| Kubernetes / Helm (multi-replica rollout) | Migrate Job | N replicas booting in parallel would race on `MigrateAsync`. The Job runs once before the rollout. |
| CI integration test | Either | Tests typically use the startup path with SQLite. |

## Startup migration (default)

`Database:MigrateOnStartup` is `true` in `appsettings.json`. On startup,
the API:

1. Resolves the configured provider (`Database:Provider`).
2. Calls `MigrationEntryPoint.ApplyMigrationsAsync`, which dispatches to
   `SqliteMigrationBootstrap.EnsureSchemaAsync` for SQLite (handles the
   legacy-schema-without-migration-history case from #883) or
   `MigrateAsync` for Postgres.
3. Continues the normal startup flow (data seeders, Kestrel bind, etc.).

To opt out — for instance in Kubernetes where the Helm chart provides a
pre-upgrade Job — set:

```yaml
env:
  - name: Database__MigrateOnStartup
    value: "false"
```

The API then skips the migration block but still runs the data seeders
(`DataSeeder`, `EnvironmentProfileSeeder`, `ThemeSeeder`) — those are
idempotent and safe on every pod start.

## `migrate` console entry

```bash
dotnet Andy.Containers.Api.dll migrate
```

(or, in a built container image, `andy-containers-api migrate`).

Builds a minimal host (no Kestrel, no workers, no seeders), applies
pending migrations, exits.

- Exit `0` on success.
- Exit non-zero on any migration failure. A single JSON line is written
  to stderr with `{ "event": "migration_failed", "provider": "...",
  "error": "...", "message": "..." }` so Helm hook log consumers can
  branch on it without regex.

The CLI inherits the same configuration chain as the API: `appsettings.*`,
environment variables (`Database__Provider`,
`ConnectionStrings__DefaultConnection`), and `--Key=Value` command-line
overrides. A Helm Job typically passes the connection string via env vars
sourced from a Secret.

## Helm Job pattern (RC6)

The chart in `charts/andy-containers/` (RC4–RC6) installs a
`pre-install,pre-upgrade` hook Job that runs `andy-containers-api migrate`
against the same Postgres the API pods will connect to. The API
Deployment sets `Database__MigrateOnStartup=false`, so pods boot without
touching the schema.

Out of scope here:

- `migrate --rollback <name>` — rolling back a migration is an out-of-band
  ops task, not a CLI subcommand.
- Online schema changes (zero-downtime DDL) — orthogonal concern.

## Reference

- `src/Andy.Containers.Api/MigrationEntryPoint.cs` — the entry point.
- `src/Andy.Containers.Api/Services/SqliteMigrationBootstrap.cs` — legacy
  SQLite history bootstrap (#883).
- `tests/Andy.Containers.Api.Tests/MigrationEntryPointTests.cs` — exit
  code + stderr envelope contracts.
- Issue [#201](https://github.com/rivoli-ai/andy-containers/issues/201) — RC3 story.
- Issue [#204](https://github.com/rivoli-ai/andy-containers/issues/204) — RC6 (Helm
  Job) consumer.
