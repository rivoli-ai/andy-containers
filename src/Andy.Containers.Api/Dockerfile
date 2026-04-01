# ── Node build stage (Angular SPA) ────────────────────────────────────────────
FROM node:22-alpine AS node-build
WORKDIR /node-build
COPY src/andy-containers-web/package.json src/andy-containers-web/package-lock.json ./
RUN npm ci
COPY src/andy-containers-web/ ./
RUN npm run build

# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /build

# Trust corporate root CAs from the centralized certs/ directory
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates openssl && \
    rm -rf /var/lib/apt/lists/*

COPY --from=certs . /usr/local/share/ca-certificates/corporate/
RUN find /usr/local/share/ca-certificates/corporate/ -name '.git*' -delete 2>/dev/null || true && \
    find /usr/local/share/ca-certificates/corporate/ -name 'README.md' -delete 2>/dev/null || true && \
    update-ca-certificates

# SSL / NuGet hardening for corporate environments
ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    SSL_CERT_DIR=/etc/ssl/certs \
    DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0 \
    NUGET_CERT_REVOCATION_MODE=off \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NUGET_SIGNATURE_VERIFICATION=false

# Restore (cached layer — only .csproj files + build props)
COPY Directory.Build.props Directory.Packages.props ./
COPY proto/ proto/
COPY src/Andy.Containers/Andy.Containers.csproj src/Andy.Containers/
COPY src/Andy.Containers.Api/Andy.Containers.Api.csproj src/Andy.Containers.Api/
COPY src/Andy.Containers.Infrastructure/Andy.Containers.Infrastructure.csproj src/Andy.Containers.Infrastructure/
RUN dotnet restore src/Andy.Containers.Api/Andy.Containers.Api.csproj

# Copy source and publish
COPY . .
RUN dotnet publish src/Andy.Containers.Api/Andy.Containers.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl openssl && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY --from=node-build /node-build/dist/andy-containers-web/browser ./wwwroot

# Generate a self-signed dev certificate at build time so no host cert is needed.
# If a real cert is mounted at /https/aspnetapp.pfx via docker-compose, it takes precedence.
RUN mkdir -p /https && \
    openssl req -x509 -nodes -days 3650 -newkey rsa:2048 \
      -keyout /tmp/dev.key -out /tmp/dev.crt \
      -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,DNS:*.localhost,IP:127.0.0.1" && \
    openssl pkcs12 -export -out /https/aspnetapp.pfx \
      -inkey /tmp/dev.key -in /tmp/dev.crt -passout pass:devcert && \
    rm -f /tmp/dev.key /tmp/dev.crt

# Entrypoint: trust custom CAs at runtime, then start the app
RUN printf '#!/bin/sh\nset -e\nif ls /usr/local/share/ca-certificates/custom/*.crt 1>/dev/null 2>&1 || ls /usr/local/share/ca-certificates/custom/*.pem 1>/dev/null 2>&1; then\n    update-ca-certificates 2>/dev/null || true\nfi\nexec "$@"\n' > /docker-entrypoint.sh && \
    chmod +x /docker-entrypoint.sh

# Keep SSL env vars at runtime for outbound HTTPS calls
ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    SSL_CERT_DIR=/etc/ssl/certs \
    ASPNETCORE_Kestrel__Certificates__Default__Path=/https/aspnetapp.pfx \
    ASPNETCORE_Kestrel__Certificates__Default__Password=devcert

EXPOSE 8080

ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["dotnet", "Andy.Containers.Api.dll"]
