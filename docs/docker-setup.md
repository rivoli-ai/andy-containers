# Docker Setup

## Services

```yaml
# docker compose up
postgres    → localhost:5434   # PostgreSQL 16
api         → localhost:5200   # API (HTTPS), localhost:5201 (HTTP)
web         → localhost:4200   # Angular frontend (nginx)
```

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

| Service | HTTPS | HTTP | Internal |
|---------|-------|------|----------|
| API | 5200 | 5201 | 8443/8080 |
| Frontend | — | 4200 | 80 |
| PostgreSQL | — | 5434 | 5432 |

## Related Services

| Service | Project | HTTPS | HTTP |
|---------|---------|-------|------|
| Andy Auth | andy-auth | 5001 | 5002 |
| Andy RBAC API | andy-rbac | 7003 | 7004 |
| Andy RBAC Web | andy-rbac | 5180 | 5181 |
| Andy Code Index API | andy-code-index | 5101 | 5102 |
| Andy Code Index Web | andy-code-index | — | 4201 |
