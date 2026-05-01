# Registry credentials

Andy Containers' platform and template images live on **`ghcr.io/rivoli-ai/*`** as **private** packages. Pulling requires a GitHub personal access token (PAT) scoped to `read:packages`. This page covers the lifecycle: minting, storing, rotating.

## Image inventory

Published by `.github/workflows/release.yml` on every `v*.*.*` tag:

| Repo | What it contains |
|---|---|
| `ghcr.io/rivoli-ai/andy-containers-api` | Platform API + in-process workers |
| `ghcr.io/rivoli-ai/templates/base` | Ubuntu base for downstream templates |
| `ghcr.io/rivoli-ai/templates/desktop` | Desktop + VNC + noVNC + dev tools |
| `ghcr.io/rivoli-ai/templates/desktop-alpine-dotnet8` | Alpine + .NET 8 SDK desktop |
| `ghcr.io/rivoli-ai/templates/desktop-alpine-dotnet10` | Alpine + .NET 10 SDK desktop |
| `ghcr.io/rivoli-ai/templates/desktop-dotnet` | Ubuntu + .NET SDK desktop |
| `ghcr.io/rivoli-ai/templates/desktop-python` | Ubuntu + Python desktop |
| `ghcr.io/rivoli-ai/templates/devpilot-desktop` | DevPilot-shaped desktop variant |
| `ghcr.io/rivoli-ai/templates/full-stack` | Full-stack template (built on `templates/base`) |

All multi-arch (`linux/amd64`, `linux/arm64`). All carry SBOM and provenance attestations.

## Minting a `read:packages` PAT

1. Sign in at https://github.com/settings/tokens with the org's bot account (e.g., `rivoli-bot`). Do **not** mint PATs against a personal account for production use.
2. New token (classic) → **Note**: `rivoli-ghcr-readonly-<purpose>-<yyyy-mm>`. Example: `rivoli-ghcr-readonly-prod-2026-04`.
3. **Expiration**: 90 days. Mark the rotation date on the on-call calendar.
4. **Scopes**: tick **only** `read:packages`. No other scope.
5. Generate, copy the value, store it in the secrets backend (1Password vault `Rivoli/Production/GHCR`).

Fine-grained PATs work too but the org has to enable them per-package; classic is the path of least resistance for now.

## Storing the PAT

### In GitHub Actions (CI smoke job)

Set as an organisation-level secret:

```sh
gh secret set RIVOLI_GHCR_READONLY_PAT --org rivoli-ai --visibility selected --repos rivoli-ai/andy-containers
```

The smoke job in `.github/workflows/release.yml` reads this; without it, the job fails with a clear error pointing here.

### In a Kubernetes workload cluster

Create a `kubernetes.io/dockerconfigjson` Secret in the `andy-system` namespace; the namespace reconciler (RC11) propagates it to every `org-*` namespace.

```sh
kubectl create secret docker-registry andy-registry-pull \
  --namespace=andy-system \
  --docker-server=ghcr.io \
  --docker-username=rivoli-bot \
  --docker-password="$(op read 'op://Rivoli/Production/GHCR/credential')"
```

The Helm chart's `imagePullSecrets` value should default to `[{ name: andy-registry-pull }]`.

### In a developer's local environment

For local pulls (rare; only needed if you're testing a published image rather than building locally):

```sh
echo "$PAT" | docker login ghcr.io -u rivoli-bot --password-stdin
```

## Rotation

Rotate every 90 days, or immediately on any of the following:

- A previous PAT was logged or otherwise leaked
- A maintainer with PAT access leaves
- A compromised dev environment is suspected

Rotation procedure:

1. **Mint** the new PAT (steps above), with the new month suffix in the name.
2. **Stage** alongside the old one. Both are valid simultaneously during the cutover window.
3. **Update** the secret backend (1Password) with the new value.
4. **Roll** the org-level workflow secret: `gh secret set RIVOLI_GHCR_READONLY_PAT --org rivoli-ai ...` with the new value.
5. **Roll** every workload cluster's `andy-registry-pull` Secret. Pods restart on next reconcile (RC11 picks up the rotation by `resourceVersion` change).
6. **Verify** by triggering a smoke job (`gh workflow run release.yml`) and watching the `smoke` job pass.
7. **Revoke** the old PAT in GitHub settings only after step 6 is green.

If a PAT is suspected leaked, **revoke first, then re-mint**. Customers will see brief `ImagePullBackOff` until the new PAT is rolled to their cluster.

## Auditing access

GitHub logs every package pull against the PAT. Pull volume per token can be reviewed at `https://github.com/orgs/rivoli-ai/packages` → individual package → `Insights` tab.

If a single PAT shows pulls from unexpected geographies or volumes, treat it as a leak: revoke, rotate, audit downstream usage.

## Why private?

Two reasons (also captured in RC1 #199):

1. Template images bundle vendored package mirrors and tooling we don't want fingerprinted by competitors or harvested for supply-chain attacks.
2. The platform image carries embedded build metadata + Angular bundles; keeping it private narrows the attack surface for pre-release security work.

The `andy-cli` repo (separate) remains the public-facing distribution path.
