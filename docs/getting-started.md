# Getting Started

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for PostgreSQL and local container provider)
- [.NET 8.0 SDK](https://dot.net/download) (for building the API)
- [Node.js 20+](https://nodejs.org/) (for the Angular frontend)

## Option 1: Docker Compose (Recommended)

The fastest way to get everything running:

```bash
git clone https://github.com/rivoli-ai/andy-containers.git
cd andy-containers
docker compose up --build
```

This starts:

- **Frontend** at http://localhost:4200
- **API** at https://localhost:5200
- **PostgreSQL** at localhost:5434

!!! note
    The API auto-generates a self-signed HTTPS certificate. Your browser may show a cert warning — this is expected for local development.

## Option 2: Local Development

For active development with hot-reload:

```bash
# 1. Start PostgreSQL
docker compose up -d postgres

# 2. Start the API
dotnet run --project src/Andy.Containers.Api

# 3. Start the frontend (in a new terminal)
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

# Authenticate (dev mode with token)
dotnet run --project src/Andy.Containers.Cli -- auth login --token dev-token

# List containers
dotnet run --project src/Andy.Containers.Cli -- list

# Create a container
dotnet run --project src/Andy.Containers.Cli -- create --template dotnet-8-alpine --name my-dev

# Connect via SSH
dotnet run --project src/Andy.Containers.Cli -- connect <container-id>
```

## First Steps

1. Open http://localhost:4200
2. Go to **Templates** — browse available container templates
3. Click **New Container** — pick a template, name it, and create
4. Open the **Terminal** — you're inside a container with dev tools pre-installed
5. Configure **Settings** — add API keys for code assistants (Anthropic, OpenAI, etc.)

## Corporate Proxy / SSL Inspection

If you're behind a corporate proxy, place your root CA certificate in the `certs/` directory:

```bash
# Export your corporate root CA
security find-certificate -a -c "YourCA" -p /Library/Keychains/System.keychain > certs/CorpRootCA.crt

# Rebuild
docker compose build --no-cache
docker compose up
```

See `certs/README.md` for detailed instructions.
