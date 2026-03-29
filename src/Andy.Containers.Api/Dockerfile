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

RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY src/Andy.Containers.Api/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# Keep SSL env vars at runtime for outbound HTTPS calls
ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    SSL_CERT_DIR=/etc/ssl/certs

EXPOSE 8080

ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["dotnet", "Andy.Containers.Api.dll"]
