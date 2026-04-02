# Getting Started

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for PostgreSQL and local container provider)
- [.NET 8.0 SDK](https://dot.net/download) (for building the API)
- [Node.js 20+](https://nodejs.org/) (for the Angular frontend)

## Option 1: Docker Compose (Recommended)

The fastest way to get everything running. All three services (Andy Auth, Andy RBAC, Andy Containers) run together:

```bash
git clone https://github.com/rivoli-ai/andy-containers.git
cd andy-containers
docker compose up --build
```

This starts:

| Service | URL | Description |
|---------|-----|-------------|
| **Frontend** | http://localhost:4200 | Angular 18 web UI |
| **API (HTTPS)** | https://localhost:5200 | REST / MCP API |
| **API (HTTP)** | http://localhost:5201 | HTTP access |
| **PostgreSQL** | localhost:5434 | Database (maps to internal 5432) |
| **Andy Auth** | https://localhost:5001 | OAuth 2.0 / OIDC server |
| **Andy RBAC API** | https://localhost:7003 | RBAC permission server |
| **Andy RBAC Web** | https://localhost:5180 | RBAC admin UI |

!!! note
    The API auto-generates a self-signed HTTPS certificate. Your browser may show a cert warning -- this is expected for local development.

!!! note
    Self-signed certificates are stored in the `./certs` directory. Docker socket is mounted for container-in-container management. Data Protection keys are persisted via a Docker volume.

## Option 2: Local Development

For active development with hot-reload:

```bash
# 1. Start PostgreSQL
docker compose up -d postgres

# 2. Start Andy Auth (in the andy-auth repo)
dotnet run --project src/Andy.Auth.Server --urls "https://localhost:5001"

# 3. Start Andy RBAC (in the andy-rbac repo)
dotnet run --project src/Andy.Rbac.Web --urls "https://localhost:7003"

# 4. Start the API
dotnet run --project src/Andy.Containers.Api

# 5. Start the frontend (in a new terminal)
cd src/andy-containers-web
npm install
npx ng serve
```

- API: **https://localhost:5200**
- Frontend: **https://localhost:4200**

## Option 3: CLI Only

```bash
# Build the CLI
dotnet build src/Andy.Containers.Cli

# Authenticate via OAuth Device Flow
dotnet run --project src/Andy.Containers.Cli -- auth login

# Or authenticate with a token (dev mode)
dotnet run --project src/Andy.Containers.Cli -- auth login --token dev-token

# List containers
dotnet run --project src/Andy.Containers.Cli -- list

# Create a container
dotnet run --project src/Andy.Containers.Cli -- create --template dotnet-8-alpine --name my-dev

# Connect via SSH
dotnet run --project src/Andy.Containers.Cli -- ssh <container-id>
```

## First Steps

1. Open http://localhost:4200
2. Go to **Templates** -- browse available container templates (12 built-in, including 4 VNC desktop variants)
3. Click **New Container** -- pick a template, name it, and optionally select a code assistant
4. Open the **Terminal** -- you are inside a container with dev tools pre-installed (18 terminal themes available)
5. For desktop templates, click the **VNC** tab -- full XFCE4 desktop via noVNC
6. Configure **Settings** -- add API keys for code assistants (Anthropic, OpenAI, OpenRouter, Google, etc.)

## Corporate Proxy / SSL Inspection

If you are behind a corporate proxy, place your root CA certificate in the `certs/` directory:

```bash
# Export your corporate root CA
security find-certificate -a -c "YourCA" -p /Library/Keychains/System.keychain > certs/CorpRootCA.crt

# Rebuild
docker compose build --no-cache
docker compose up
```

See `certs/README.md` for detailed instructions.
