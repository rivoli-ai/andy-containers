# Docker Setup

## Services

All services run in Docker via `docker compose up`:

```yaml
# Andy Containers services
postgres          -> localhost:5434   # PostgreSQL 16-alpine (maps to internal 5432)
api               -> localhost:5200   # API (HTTPS), localhost:5201 (HTTP)
web               -> localhost:4200   # Angular frontend (nginx)

# Dependent Andy services
andy-auth         -> localhost:5001   # Andy Auth (HTTPS), localhost:5002 (HTTP)
andy-rbac-api     -> localhost:7003   # Andy RBAC API (HTTPS), localhost:7004 (HTTP)
andy-rbac-web     -> localhost:5180   # Andy RBAC Web (HTTPS), localhost:5181 (HTTP)
```

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

### Andy Containers

| Service | HTTPS | HTTP | Internal |
|---------|-------|------|----------|
| API | 5200 | 5201 | 8443/8080 |
| Frontend | -- | 4200 | 80 |
| PostgreSQL | -- | 5434 | 5432 |

### Related Andy Services

| Service | Project | HTTPS | HTTP |
|---------|---------|-------|------|
| Andy Auth | andy-auth | 5001 | 5002 |
| Andy RBAC API | andy-rbac | 7003 | 7004 |
| Andy RBAC Web | andy-rbac | 5180 | 5181 |

## Database

- **Engine**: PostgreSQL 16-alpine
- **External port**: 5434 (maps to internal 5432)
- **Schema**: Auto-created on first startup via `EnsureCreatedAsync()`
- **Seed data**: Providers and templates seeded automatically by `DataSeeder`
- **Persistence**: Data persists across restarts via Docker volume
