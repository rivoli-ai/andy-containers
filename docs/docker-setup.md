# Docker Setup

## Services

All services run in Docker via `docker compose up`:

```yaml
# Andy Containers services (Docker port mappings — see docker-compose.yml)
postgres          -> localhost:7434   # PostgreSQL 16-alpine (maps to internal 5432)
api               -> localhost:7200   # API (HTTPS), localhost:7201 (HTTP)
web               -> localhost:6200   # Angular frontend (nginx)

# Dependent Andy services
andy-auth         -> localhost:5001   # Andy Auth (HTTPS), localhost:5002 (HTTP)
andy-rbac-api     -> localhost:7003   # Andy RBAC API (HTTPS), localhost:7004 (HTTP)
andy-rbac-web     -> localhost:5180   # Andy RBAC Web (HTTPS), localhost:5181 (HTTP)
```

For local (non-Docker) `dotnet run` the API and Angular dev server use the canonical local ports `5200/5201/4200` per [`config/registration.json`](../config/registration.json).

## Volumes and Mounts

| Mount | Purpose |
|-------|---------|
| `./certs` | Self-signed HTTPS certificates and corporate CA certs |
| `/var/run/docker.sock` | Docker socket for container-in-container management |
| `dataprotection-keys` | ASP.NET Core Data Protection key persistence volume |
| `postgres-data` | PostgreSQL data directory |

## Certificate Management

### Corporate Certificates

Place `.crt`/`.pem` files in the `certs/` directory at the repo root:

- **Build time**: Copied into Docker images via `COPY --from=certs` and trusted with `update-ca-certificates`
- **Runtime**: Mounted as a volume and trusted on container startup

### Self-Signed Dev Certificate

The API Dockerfile auto-generates a self-signed certificate at build time using `openssl`. No host setup required.

### SSL Environment Variables

All containers set these for corporate proxy compatibility:

```
SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt
DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0
NUGET_CERT_REVOCATION_MODE=off
DOTNET_NUGET_SIGNATURE_VERIFICATION=false
```

## Building

```bash
# Build all services
docker compose build

# Build with no cache (after cert changes)
docker compose build --no-cache

# Build specific service
docker compose build api
```

## Port Reference

### Andy Containers (Docker)

| Service | HTTPS | HTTP | Internal |
|---------|-------|------|----------|
| API | 7200 | 7201 | 8443/8080 |
| Frontend | -- | 6200 | 80 |
| PostgreSQL | -- | 7434 | 5432 |

Local (non-Docker) `dotnet run`: API `5200/5201`, Angular `4200`, Postgres `5434` — see `config/registration.json` for the canonical port map.

### Related Andy Services

| Service | Project | HTTPS | HTTP |
|---------|---------|-------|------|
| Andy Auth | andy-auth | 5001 | 5002 |
| Andy RBAC API | andy-rbac | 7003 | 7004 |
| Andy RBAC Web | andy-rbac | 5180 | 5181 |

## Embedded local registry (zot) — Docker Desktop push target

In Conductor's embedded mode, andy-containers re-hosts images into a
local [zot](https://zotregistry.dev/) registry by shelling out to the
Docker CLI (`docker tag` + `docker push`) for both template builds
(`LocalZotAdapter`) and the `POST /api/images/ensure-pull` rehost path
(`DockerCliImagePullService`).

### The Docker Desktop loopback gap

The embedded zot binds the host's loopback (`127.0.0.1:5050`) and
Conductor's HTTP reads talk to `http://localhost:5050` — both correct,
both on the host. But `docker push` runs **inside the Docker Desktop
VM**, where `localhost` is the VM, not the host. The VM has no route to
the host's loopback, so the push hangs and dies with:

```
Get "http://localhost:5050/v2/": net/http: request canceled while
waiting for connection (Client.Timeout exceeded while awaiting headers)
```

even though `curl http://localhost:5050/v2/` returns 200 from the host.

### The fix: rewrite the push target to `host.docker.internal`

`PushTargetHostResolver` rewrites the push/tag **target authority** from
a loopback host to `host.docker.internal` (which Docker Desktop routes
back to the host) whenever the daemon is Docker Desktop — i.e. Docker
engine on a non-Linux host. The HTTP API client keeps using `localhost`.
On native Linux Docker the daemon shares the host network namespace, so
`localhost` already works and **no rewrite happens** (the default `Auto`
mode is OS-aware).

Configure via the `ImageManagement:PushTarget` section:

| Key | Default | Meaning |
|-----|---------|---------|
| `ImageManagement:PushTarget:Mode` | `Auto` | `Auto` (rewrite only on Docker Desktop), `Never`, or `Always` |
| `ImageManagement:PushTarget:DockerDesktopHostAlias` | `host.docker.internal` | Host alias the VM can reach the host on |

### Required Docker Desktop setting: `insecure-registries`

`host.docker.internal:5050` is served over **plain HTTP**, and Docker
treats any non-`localhost` registry as **HTTPS** by default. Without the
host in the daemon's insecure-registries list, the rewritten push fails
with:

```
Get "https://host.docker.internal:5050/v2/": http: server gave HTTP
response to HTTPS client
```

andy-containers surfaces this loudly (it does not fail silently) — the
push error names the exact address and the entry to add. To resolve it,
add the registry to the Docker daemon's insecure-registries:

**Docker Desktop → Settings → Docker Engine**, add:

```json
{
  "insecure-registries": ["host.docker.internal:5050"]
}
```

then **Apply & Restart**. (CLI equivalent: add the entry to
`~/.docker/daemon.json` and restart Docker Desktop.)

> If the registry's port is dynamically allocated, use that port instead
> of `5050`. Conductor pins zot to `5050` by default
> (`ZotServiceConfig.usesDynamicPort = false`).

### (Optional) bind zot to a VM-reachable interface

On some Docker Desktop versions `host.docker.internal` is proxied to the
host's loopback and reaches a `127.0.0.1`-bound zot fine. If a future
Docker Desktop version routes `host.docker.internal` to the VM gateway
instead, the embedded zot must bind a VM-reachable interface
(`0.0.0.0:5050` instead of `127.0.0.1:5050`). That bind is owned by
Conductor's zot launch config (`config.template.json`), not by
andy-containers.

## Database

- **Engine**: PostgreSQL 16-alpine
- **External port (Docker)**: 7434 (maps to internal 5432). Local `dotnet run` connects on 5434.
- **Schema**: Auto-created on first startup via `EnsureCreatedAsync()`
- **Seed data**: Providers and templates seeded automatically by `DataSeeder`
- **Persistence**: Data persists across restarts via Docker volume
