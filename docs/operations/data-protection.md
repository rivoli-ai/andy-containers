# Data Protection keys

ASP.NET Core's [Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/introduction) subsystem encrypts every payload that needs at-rest protection inside the API: cookies, anti-forgery tokens, OAuth state parameters, etc. The key ring rotates every 90 days by default; if the key ring is lost, every previously-issued payload becomes unreadable and users get logged out.

## Where the keys live

**Postgres `DataProtectionKeys` table.** Schema added by EF migration `20260506045845_AddDataProtectionKeys`. One row per active key; XML payload is the key material. Wired in `Program.cs` via:

```csharp
builder.Services.AddDataProtection()
    .SetApplicationName("andy-containers")
    .PersistKeysToDbContext<ContainersDbContext>();
```

`SetApplicationName` is **load-bearing** — two providers that share a key store but disagree on the application name will not decrypt each other's payloads. Don't change this string without a planned mass-logout event.

### Why not the filesystem?

Older deploys (pre-RC2 #200) mounted `/root/.aspnet/DataProtection-Keys` from a Docker volume. That works for a single API container but breaks the moment you scale to N replicas behind a Service: an RWO PVC can't be shared, RWX isn't universally available, and per-pod ephemeral volumes mean every pod issues payloads only it can decrypt. The DB-backed store side-steps the problem — every replica reads the same `DataProtectionKeys` table.

The legacy `dataprotection_keys` Docker volume was removed from `docker-compose.yml` in RC2.

### Encryption at rest

The `Xml` column ships unwrapped — the key material is at-rest-protected by **whatever protects Postgres**:

- Hosted Postgres: rely on the provider's at-rest encryption (RDS/Cloud SQL/Aiven all encrypt by default).
- Self-hosted: encrypted volume (LUKS / dm-crypt) or Postgres TDE.
- Local dev: not encrypted; not a concern because dev data isn't sensitive.

If your security review requires explicit KEK wrapping (envelope encryption with an external KMS), see RC16 (#214) — out of scope for RC2.

## Rotation

The framework rotates automatically every 90 days. There's nothing to schedule — the runtime walks the key ring on each operation and creates a new key when the active one is approaching expiry. A new row appears in `DataProtectionKeys`; old rows are kept around to decrypt still-valid payloads issued under previous keys.

## Recovery from key loss

If the entire `DataProtectionKeys` table is lost (e.g., a botched migration, a bad backup restore):

1. **Every existing cookie / anti-forgery token becomes invalid.** Users get logged out and must sign in again. This is the same recovery posture as losing the disk-based key ring.
2. The framework auto-generates a fresh key on next startup. No operator action required.
3. If multiple replicas are running during the loss window, expect brief 401/403s while pods catch up.

For deliberate mass logout (e.g., compromised key suspected, post-incident reset), `TRUNCATE DataProtectionKeys` then restart the API pods.

## Migration from disk-based keys

If you have an existing single-process deploy with keys on `/root/.aspnet/DataProtection-Keys`, two paths:

### Option A — accept a logout

Deploy the new version. The framework generates a fresh key on first request. Every cached cookie issued by the old version becomes invalid. Users sign in again.

This is the right choice for low-traffic deploys or planned-maintenance windows.

### Option B — preserve key ring (no script yet)

Spec'd in RC2 (#200) but not implemented in v1. The intended subcommand is `dotnet Andy.Containers.Api.dll migrate-data-protection-keys --from <path>`, which would parse the XML files under the volume and INSERT them into the `DataProtectionKeys` table. Punted to a follow-up because:

- The two-stage rollout pattern (run both stores side-by-side for one rotation cycle, then drop the disk store) is cleaner and avoids a one-shot script that has to handle every edge case.
- Most existing deploys are single-process compose where a logout is acceptable.

If the migration script becomes blocking, file a follow-up issue against #200 and link this section.

## Reference

- `src/Andy.Containers.Api/Program.cs` — `AddDataProtection().PersistKeysToDbContext<>()` registration.
- `src/Andy.Containers.Infrastructure/Data/ContainersDbContext.cs` — implements `IDataProtectionKeyContext`.
- `src/Andy.Containers.Infrastructure/Migrations/20260506045845_AddDataProtectionKeys.cs` — table migration.
- `tests/Andy.Containers.Api.Tests/DataProtection/DataProtectionKeyStoreTests.cs` — cross-replica + persistence contracts.
- Issue [#200](https://github.com/rivoli-ai/andy-containers/issues/200) — RC2 story.
- Issue [#202](https://github.com/rivoli-ai/andy-containers/issues/202) — RC4 (Helm chart skeleton) — first consumer.
