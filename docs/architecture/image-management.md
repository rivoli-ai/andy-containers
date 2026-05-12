# Image Management — Architecture

> **Status:** Draft RFC for Epic IM. This document is the IM1 deliverable and is the source of truth that the rest of the epic implements against. Open the corresponding pull request for proposed changes.

## Scope

This document covers how `andy-containers` manages **customer-built dev container images** — the images Conductor (or any other client) submits a YAML spec for, that get built, signed, and pushed to a registry so that workspace launches can pull them.

### Out of scope

- **Publishing `andy-containers` itself** (the API server image, the migration job image, the web UI image). That pipeline is owned by Epic RC; it pushes to `ghcr.io/rivoli-ai/*` and is unrelated to customer image management.
- **Workspace runtime networking** to pull from registries (DNS, egress allowlists, image-pull secrets at the orchestrator level). That's a provider concern (`KubernetesInfrastructureProvider`, `DockerProvider`, `AppleContainersProvider`).
- **Tenant lifecycle** (creating/deleting tenants, per-tenant deployment of `andy-auth`/`andy-rbac`). Owned by Epic TC (`andy-tenants`).

## The deployment matrix

`andy-containers` is the single API surface for image management. Below it sits a registry adapter, a build backend, an identity bridge, and a pull-credential broker. Each abstraction has different concrete implementations depending on the deployment mode.

| Mode | Registry | Build backend | Auth | RBAC source |
|---|---|---|---|---|
| **Solo** (one laptop, embedded) | Local zot, supervised by Conductor's `ServiceOrchestrator`, configured by `andy-containers` | Apple Containers > Docker BuildKit on the laptop | None (`localhost:5050`, anonymous) | None |
| **Team-local** (5–20 teammates, no cloud backend) | One shared zot reachable via Tailscale (NAS / always-on box). Not P2P — Dragonfly/Spegel are designed for thousands of nodes, not laptops. | Each laptop's local engine | OIDC (Tailscale headers) or htpasswd | Shared zot ACL (Cosign signing optional) |
| **Single-tenant cloud** (customer's own cloud) | Whatever the customer mandates — JFrog Artifactory, Azure Container Registry, Amazon ECR, Harbor, Google Artifact Registry. **No embedded zot.** | Often "no Docker daemon on dev laptops" — ACR Tasks / Cloud Build / CodeBuild / BuildKit-on-cluster | AAD token / STS / Artifactory access token / GAR workload identity | `andy-rbac` AND the customer registry's RBAC (both gate, neither alone is sufficient) |
| **Multi-tenant Rivoli Cloud** | zot scale-out cluster (S3 storage + DynamoDB-compatible metadata + HAProxy front, proven topology in zot v2.1+) | Hosted BuildKit-on-cluster pool | `andy-auth` → JWT → zot OIDC | `andy-rbac` with prefix-isolated repos `tenant-<id>/...` |

The point of the matrix: **the API contract `andy-containers` exposes to Conductor, the CLI, and the MCP gateway is identical across all four modes.** Only the internal adapter wiring changes. A Conductor that talks to a solo-mode `andy-containers` and a Conductor that talks to a Rivoli-Cloud-tenant `andy-containers` issue the same HTTP requests; the difference is invisible to the client.

## zot ownership in embedded mode

This is a hybrid:

| Concern | Owner |
|---|---|
| zot process lifecycle (start, supervise, restart, expose health) | **Conductor** — `ZotServiceConfig` registered in `ServiceOrchestrator`, see [#1009 in `rivoli-ai/conductor`](https://github.com/rivoli-ai/conductor/issues/1009). |
| zot runtime config (storage path, ACL, OIDC, port) | **`andy-containers`** |
| zot HTTP API access (push, pull, list, delete) | **`andy-containers`** — the only consumer |

In embedded mode, zot's runtime config is effectively static (anonymous on `localhost:5050`, storage in the user's library), so Conductor keeps shipping the bootstrap config it ships today and `andy-containers` consumes zot without needing to mutate it. The "`andy-containers` configures and accesses" rule is enforced by **convention**: no other component is allowed to talk to zot's HTTP API or write its config file. There is no enforcement code in v1; we revisit when multi-tenancy lands.

In cloud modes (Rivoli Cloud / single-tenant customer), Conductor is uninvolved — `andy-containers` runs in the cluster, zot scale-out runs in the cluster (or is replaced by the customer's mandated registry), and the Conductor desktop app simply targets the cloud `andy-containers` URL.

## Abstractions

These interfaces live in `Andy.Containers.Abstractions` (story IM2). Default implementations live in `Andy.Containers.Infrastructure`; concrete adapters live in per-vendor projects (`Andy.Containers.Registry.Zot`, `Andy.Containers.Registry.Artifactory`, etc.).

### `IRegistryAdapter`

```csharp
public interface IRegistryAdapter
{
    string RegistryId { get; }
    Task<RegistryReference> PushAsync(BuildArtifact artifact, CancellationToken ct);
    Task<IReadOnlyList<RegistryReference>> ListAsync(string repoPathPrefix, CancellationToken ct);
    Task DeleteAsync(RegistryReference reference, CancellationToken ct);
    Task<bool> ExistsAsync(string digest, CancellationToken ct);
    // Vendor-specific extensions live in subinterfaces:
    //   IPolicyProbe (Xray, ECR Inspector, ACR content trust)
    //   ISigningTarget (Cosign keyless / key-pair)
}
```

Default OCI-distribution-v1.1 client covers `push`/`list`/`delete` for any OCI-conformant registry (zot, Artifactory, ACR, ECR, Harbor, GAR). Per-vendor extensions handle policy probes (Xray, ECR Enhanced/Inspector), lifecycle, and access management — these are control-plane and not specced in the OCI distribution spec.

### `IBuildBackend`

```csharp
public interface IBuildBackend
{
    string BackendId { get; }
    BuildBackendCapabilities Capabilities { get; }
    Task<BuildArtifact> BuildAsync(
        TemplateSpec spec,
        IBuildContext context,
        IProgress<BuildProgressEvent> progress,
        CancellationToken ct);
}
```

Implementations:

- **`LocalBuildBackend`** (Phase 1) — wraps `apple-container build` (preferred where available, macOS 26+) or `docker buildx build`. Engine detected at startup, logged.
- **`AcrTasksBackend`** (Phase 3) — submits build to ACR Tasks; no Docker on caller. ACR Tasks accepts a YAML task spec and source context (Git/local/tar).
- **`CloudBuildBackend`** (Phase 3) — Google Cloud Build trigger.
- **`CodeBuildBackend`** (Phase 3) — AWS CodeBuild project.
- **`BuildKitOnClusterBackend`** (Phase 3) — rootless BuildKit pod with `--frontend dockerfile`. Covers "no Docker daemon on dev laptops" customer policies.

### `IRegistryUploader`

`IRegistryAdapter.PushAsync` reads the digest authoritatively from the registry's HTTP API. The bytes themselves are uploaded by an `IRegistryUploader`:

```csharp
public interface IRegistryUploader
{
    Task PushAsync(string localReference, string remoteReference, CancellationToken ct);
}
```

The split exists because *which CLI can push a built image* depends on *which engine built it*. Apple Containers and Docker maintain separate local image stores; an image built with `container build` is invisible to `docker push` and vice-versa.

Implementations:

- **`DockerCliUploader`** (IM6) — shells `docker tag` then `docker push`. Used when the build engine is Docker BuildKit.
- **`AppleContainersUploader`** (P1F3, rivoli-ai/andy-containers#276) — shells `container images tag` then `container images push`. Used when the build engine is Apple Containers (macOS 26+).
- **`EngineAwareRegistryUploader`** — the composite registered as `IRegistryUploader` in DI. Resolves `IBuildEngineDetector` on first push, caches the choice, and dispatches to the right concrete uploader. Throws `RegistryUploadException("EngineAwareRegistryUploader.NoEngine")` when no engine is detected.

The adapter never sees the uploader split — it just calls `IRegistryUploader.PushAsync`, gets bytes pushed, and then asks the registry for the digest via HEAD.

### `IRegistryConfiguration`

```csharp
public interface IRegistryConfiguration
{
    IReadOnlyList<RegistryConfigEntry> Registries { get; }   // ordered; first is "primary push target"
    RegistryConfigEntry GetByIdOrThrow(string registryId);
    string PrimaryRegistryId { get; }
}
```

In solo mode, the list has one entry (managed local zot). In single-tenant cloud mode, the list has the customer-mandated registry. In multi-tenant Rivoli Cloud, the list has the Rivoli zot scale-out plus optional pull-through caches.

### `IIdentityBridge`

Translates `andy-auth` JWT claims into the registry's native auth:

| Registry | Translation |
|---|---|
| zot | Pass-through — `andy-auth` JWT acts as zot's OIDC bearer |
| Artifactory | Token exchange — exchange `andy-auth` ID token for an Artifactory scoped access token via REST |
| ACR | AAD token (managed identity / SP) — usually issued upstream of `andy-containers`, not bridged |
| ECR | STS `AssumeRoleWithWebIdentity` from the `andy-containers` IRSA / Pod Identity |
| Harbor | OIDC bearer; group claims mapped to Harbor project roles |
| GAR | Workload identity federation; `andy-auth` ID token → GCP STS → service-account access token |

### `IPullCredentialBroker`

When a workspace launches, the orchestrator (DockerProvider / AppleContainersProvider / KubernetesInfrastructureProvider) needs creds to pull the image. The broker mints short-lived creds appropriate to the registry:

- zot embedded — no-op (anonymous on `localhost:5050`)
- ECR — mints a 12h `docker login` password via `ecr:GetAuthorizationToken`; refreshes if the workspace runs longer
- ACR — mints an ACR-scoped token from the workspace's managed identity, or returns the imagePullSecret reference for `kubectl`
- Artifactory — mints a scoped access token bound to a single repo path
- Harbor — robot account credential

## Image identity

**The canonical key is the OCI digest, not the reference.** Same bytes → same `sha256:abc...` in every registry. A reference (`localhost:5050/foo:1.2.3`, `mycorp.jfrog.io/docker-local/foo:1.2.3`, `tenant-x.azurecr.io/foo:1.2.3`) is registry-specific and many-to-one against the digest.

DB schema (story IM3, layered on top of the existing `Images` table; old rows get a digest column populated by a backfill migration):

```
BuildArtifact
  Id (uuid, PK)
  Digest (string, unique)              -- "sha256:abc..."
  MediaType (string)                   -- "application/vnd.oci.image.manifest.v1+json"
  SizeBytes (long)
  SpecHash (string, indexed)           -- content-addressable hash of the source spec
  TemplateId (uuid, FK to Templates)
  BuildBackendId (string)              -- "local-docker" / "acr-tasks" / etc.
  BuiltBy (string)                     -- user id / service identity
  BuiltAt (timestamp)
  BuildLog (text, nullable)            -- captured stdout/stderr on failure (or pointer to S3)

RegistryReference
  Id (uuid, PK)
  BuildArtifactId (uuid, FK to BuildArtifact)
  RegistryId (string)                  -- "local-zot" / "mycorp-artifactory" / ...
  RepoPath (string)                    -- "conductor-terminal-claude-code"
  Tag (string)                         -- "sha256-abc12345" or "v1.2.3"
  PushedAt (timestamp)
  PushedBy (string)
  -- composite unique on (RegistryId, RepoPath, Tag)

ImageSignature
  Id (uuid, PK)
  BuildArtifactId (uuid, FK)
  Format (enum: cosign-keyless | cosign-keypair | notation-v2)
  PayloadDigest (string)               -- digest of the signed payload
  CertificateChain (text, nullable)    -- for keyless: Fulcio cert
  TransparencyLogEntry (string, null)  -- Rekor entry UUID
  SignedAt (timestamp)
```

`SpecHash` is the **idempotency key**: if the same spec hashes to the same value and a `BuildArtifact` already exists for it in the primary registry, the build is skipped and the existing reference is returned. `Digest` is the **audit/dedup key**: any signing, scanning, or trust-policy decision anchors on digest, never on tag.

## Spec hashing for content-addressability

The hash input is the **canonical-JSON representation of the parsed YAML spec, plus the digests of any uploaded files referenced in the spec**. Whitespace and key ordering in the YAML do not affect the hash. Re-uploading the same spec returns the existing template ID without writing new rows.

```
specHash = sha256(canonicalJSON(parsedSpec) || sortedFileDigests)
```

The canonical-JSON normalization rules:

- Keys sorted lexicographically at every level
- Numbers in JSON canonical form (no leading zeros, exponent normalization)
- Strings UTF-8, no escape variations
- No trailing whitespace

This is well-trodden — borrowed from JCS (RFC 8785). A reference implementation lives in `Andy.Containers/Crypto/CanonicalJson.cs` (to be added in IM3).

## YAML template extensions for M1.9

Today's `docs/YAML-CONFIGURATION.md` defines templates declaratively (`base_image:`, `dependencies:` as a typed list). M1.9's spec format adds imperative fields for cases the dependency abstraction doesn't cover (e.g., `npm install -g @anthropic-ai/claude-code`). They are **complementary, not replacements**:

```yaml
# templates/global/conductor-terminal-claude-code.yaml
code: conductor-terminal-claude-code
name: Conductor Terminal — Claude Code
version: "1.0.0"

# Existing declarative fields still work:
base_image: ubuntu:22.04
dependencies:
  - { type: tool, name: bash }
  - { type: tool, name: git }
  - { type: tool, name: curl }

# New imperative fields (M1.9):
extends: conductor-terminal-base    # optional; resolved before build by spec hash
files:
  - source: ./scripts/install-assistants.sh
    dest: /opt/conductor/install-assistants.sh
    mode: 0755
install:
  - npm install -g @anthropic-ai/claude-code
entrypoint: /opt/conductor/entrypoint.sh
markers:
  baked-assistants: [claude-code]
```

Story IM4 expands `docs/YAML-CONFIGURATION.md` with the new field reference, semantics for `extends:` (resolved by `specHash` against the primary registry; build the parent first if it's not cached), and a deprecation note that `from:` is an alias for `base_image:` accepted only for backward compatibility with the M1.9 issue body.

## API surface (preview)

Full OpenAPI spec lands in story IM5. Preview:

| Endpoint | Purpose |
|---|---|
| `POST /api/templates` (multipart: `spec.yaml` part + zero-or-more `files[]` parts) | Register a YAML template. Idempotent on `specHash`. Returns `{ templateId, specHash }`. |
| `GET /api/templates/{templateId}` | Return template metadata. |
| `POST /api/images/{templateId}/build` | Trigger a build. Returns `{ buildId, status: "queued" }`. **Async** — see SSE below. If `specHash` already resolves to a `BuildArtifact` in the primary registry, returns the existing artifact reference immediately with `status: "cached"`. |
| `GET /api/images/build/{buildId}` | Build status snapshot: `{ status, digest?, references[], buildLog? }`. |
| `GET /api/images/build/{buildId}/events` | **Server-Sent Events** stream of build progress (`step-start`, `step-stdout`, `step-error`, `complete`). |
| `GET /api/images` | List all `BuildArtifact`s. Each entry includes `references[]` per registry. |
| `GET /api/images/{digest}` | Get a single artifact by digest. |
| `DELETE /api/images/{digest}/references/{referenceId}` | Untag a reference (does not delete the artifact). Admin-only. |

### Decisions baked into this API shape

| Decision | Rationale |
|---|---|
| **Async builds** (queued + SSE for progress) | Builds run minutes; sync HTTP responses time out badly; Conductor's UI streams nicely |
| **Multipart upload for files** (not inline-base64, not local-path) | Cleanest, matches `docker build -f` semantics, doesn't bake in the embedded co-resident assumption |
| **Template-id always required for build** | Per the team's earlier direction — never call build with a YAML body, register the template first |
| **`specHash` idempotent on register** | Re-uploading the same spec returns the existing template ID, no duplicate rows |
| **Build response shape is registry-aware** (`{ digest, references[] }`) | The API works the same in solo and cloud modes; the references array changes |
| **Content-addressable cache hit** on register-then-build | Skip rebuild if `specHash → digest` is already known in the primary registry |

## Cross-cutting security implications

Worth being explicit about, because they bite people:

1. **Tag mutation under admission.** A movable tag (`:latest`, `:v1`) passes admission once; the next pull resolves to a different digest. Production manifests should always reference by digest (`@sha256:...`). Workspace launches in customer environments should pin to digest by default; we may add a `pin-by-digest` mode flag in workspace creation later.
2. **Re-scan revocation.** ECR Enhanced (Inspector) and Artifactory Xray re-evaluate already-pushed images. A previously-allowed image can become policy-blocked overnight. Mitigation: cache to local zot pull-through, but understand the risk window.
3. **Sigstore egress.** Public Fulcio/Rekor live at `*.sigstore.dev`. Air-gapped or strict egress-allowlist environments break keyless verification silently. Story IM12 ships keyless signing; air-gapped customers will need a private Sigstore stack (deferred — file when first asked).
4. **Registry token expiry.** ECR's `docker login` password expires every 12 hours. Long-running workspaces (multi-day agent runs) will fail to re-pull. The pull-credential broker has to refresh ahead of expiry — story IM16 owns this on the ECR adapter.
5. **OCI 1.1 referrers fallback.** Older registries (Artifactory pre-2023, Harbor < 2.8) lack the `/referrers` API. Cosign signatures appear missing if the client doesn't fall back to the tag-based scheme. Default our adapters to fallback-on.
6. **Notary v1 dead.** Harbor 2.9+ removed it. We will not support Notary v1; only Cosign and Notation v2.
7. **ACR admin user.** Often enabled by default in Azure templates — a single shared root credential. Our ACR adapter will refuse to use it.

## Phasing rationale

| Phase | Stories | Why these, why now |
|---|---|---|
| **0 — contract** | IM1–IM5 | Lock the API and abstractions before any code. Cheap to change here, expensive later. |
| **1 — local zot** | IM6–IM11 | M1.9 critical path. Conductor needs this to ship the code-assistant container runtime. |
| **2 — supply chain** | IM12–IM13 | Signing and scanning are table-stakes for any cloud customer demo. Land before Phase 3. |
| **3 — external adapters** | IM14–IM19 | Deferred until first cloud customer asks. Order driven by sales conversations. |
| **4 — caching & team-local** | IM20–IM22 | Deferred. Most teams that hit this scale will have moved to cloud mode anyway. Filed when the first non-cloud team-local customer asks. |
| **5 — multi-tenant Rivoli Cloud** | IM23–IM25 | Deferred. Depends on Epic RC progress (Helm chart, Kubernetes provider, multi-tenant isolation) — image storage is the *easiest* of those problems; we land it last. |

## Open questions / acknowledged unknowns

These are intentionally not answered in this memo; they will be tightened in the stories that depend on them.

1. **Multi-tenant repo prefixing scheme** — `tenant-<id>/` vs `t-<slug>/` vs `<slug>.tenant/`. Epic RC's tenant model will inform this; story IM24 picks the format then.
2. **Per-tenant storage quota and GC policy** — needs cost model. Deferred to IM25.
3. **`PullCredentialBroker` concrete contract** — it's an interface today; the first non-zot adapter (Phase 3) shapes it. May need to revise IM2's interface signature when we get there.
4. **`extends:` cycle detection** — what happens if A extends B extends A? Story IM4 should specify "build fails with cycle error before any build is queued."
5. **Build-log storage at scale** — embedding in the DB row is fine for Phase 1; cloud modes will need S3/blob storage. IM23 territory.

## References

- Research brief that informed the deployment matrix and abstractions: see PR description for the source-URL list.
- Existing template/provider YAML model: `docs/YAML-CONFIGURATION.md`.
- Epic RC partition: see Epic RC body in `rivoli-ai/andy-containers#198`.
- zot supervised by Conductor: `rivoli-ai/conductor#1009`.
- Parent customer-facing feature (M1.9 container code-assistant runtime): `rivoli-ai/conductor#1008`.
