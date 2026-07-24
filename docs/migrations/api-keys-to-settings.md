# Migrating provider API keys from `ApiKeyCredentials` to andy-settings

Tracks `rivoli-ai/conductor#946` (M1.5.4).

The legacy `ApiKeyCredentials` table in `andy-containers` was retired
once the proxy-by-default routing (M1.5.1–3) made it redundant.
Provider keys now live in `andy-settings` under the definition key
template `andy.models.providers.<slug>.apiKey` and reach the inside of
a container via the andy-models proxy with a per-container service
token. The container never sees a raw provider key.

This document is the operator runbook for migrating off the old table
before the `DropApiKeyCredentials` migration runs.

## When this matters

You need to run the export + import steps below **only** if your
production database already has rows in `ApiKeyCredentials`. Fresh
installs and dev environments started after M1.5 are unaffected — the
write path stopped landing rows when M1.5.1 shipped.

To check on a deployed database:

```sql
SELECT COUNT(*) FROM "ApiKeyCredentials";
```

A zero means there is nothing to migrate; deploy the new revision and
the `DropApiKeyCredentials` migration runs cleanly.

## Migration steps

1. **Export the existing rows.** Each row maps to one
   `andy-settings` secret definition. The export must include the
   plaintext `ApiKey` value, which is encrypted at rest under
   `EncryptedValue` using the same `IDataProtector` purpose the legacy
   `ApiKeyService` configured. Decrypt with that protector before
   writing to andy-settings.

   ```sql
   SELECT "Id", "OwnerId", "Provider", "EncryptedValue", "BaseUrl"
     FROM "ApiKeyCredentials"
     ORDER BY "OwnerId";
   ```

2. **For each row, write to andy-settings.** The definition key
   template is `andy.models.providers.<slug>.apiKey` where `<slug>`
   maps from the legacy `Provider` enum:

   | Legacy `Provider`   | andy-settings definition key                    |
   | ------------------- | ----------------------------------------------- |
   | `Anthropic` (0)     | `andy.models.providers.anthropic.apiKey`        |
   | `OpenAI` (1)        | `andy.models.providers.openai.apiKey`           |
   | `Google` (2)        | `andy.models.providers.google.apiKey`           |
   | `Dashscope` (3)     | `andy.models.providers.alibaba.apiKey`          |
   | `Custom` (4)        | `andy.models.providers.openai-compatible.apiKey`|
   | `OpenRouter` (5)    | `andy.models.providers.openrouter.apiKey`       |
   | `Ollama` (6)        | *no migration — Ollama is keyless*              |
   | `OpenAiCompatible` (7) | `andy.models.providers.openai-compatible.apiKey` |

   Use the andy-settings API:

   ```
   POST /api/secrets/andy.models.providers.<slug>.apiKey
   Authorization: Bearer <admin-token>
   Content-Type: application/json

   {
     "ScopeType": "Machine",
     "ScopeId": null,
     "Value": "<decrypted plaintext>"
   }
   ```

   Run once per `(slug)` — the most recent row wins if multiple rows
   exist for the same provider.

3. **Verify** by reading the key-status endpoint on andy-models. It
   never echoes the value; it just confirms a key is configured:

   ```
   GET /api/providers/<slug>/key-status
   ```

   The response `configured` field should flip to `true` immediately,
   driven by andy-settings' NATS event invalidating andy-models'
   `IProviderKeyResolver` cache (`SettingsBackedProviderKeyResolver`).

4. **Deploy the revision containing the
   `20260512173330_DropApiKeyCredentials` migration.** Running
   migrations drops the table. Existing containers keep running; new
   containers go through the proxy-routing path.

## Rollback

The migration's `Down` method recreates the table schema but cannot
restore data. If you must roll back, restore from the export captured
in step 1.

## What changed in the source

- `ApiKeyService`, `IApiKeyService`, `ApiKeyValidationService`,
  `IApiKeyValidationService`, `ApiKeysController`, `ApiKeyCredential`
  model — deleted.
- `ContainerOrchestrationService` no longer takes `IApiKeyService`.
  The credential-resolution branch was removed; the only env vars
  derived from `CodeAssistantConfig` are now `ApiBaseUrl` (for
  Ollama / OpenAI-compatible self-hosted backends) and `ModelName`.
- `ContainersMcpTools` no longer exposes `StoreApiKey` / `ListApiKeys`
  / `DeleteApiKey` / `ValidateApiKey`. Operators manage keys via
  andy-settings directly.
- DbContext: `DbSet<ApiKeyCredential> ApiKeyCredentials` removed.
- EF migration `20260512173330_DropApiKeyCredentials` drops the table.

## Current management API

Issue #313 later restored the Conductor-facing `/api/apikeys` CRUD and
validation contract as a thin control-plane facade. It does **not** restore
`ApiKeyCredentials` or local plaintext/ciphertext storage:

- raw values are written to the user-scoped andy-settings secret
  `andy.models.providers.<slug>.apiKey`;
- `ApiKeyRegistrations` contains only the label, provider, model/base URL,
  masked suffix, and validation timestamps;
- `ApiKeyAuditRecords` is append-only metadata history and remains queryable
  after deletion;
- responses never include the raw value.
