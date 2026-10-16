# Development

## Prerequisites

- .NET SDK 10.0.400+ (pinned in `global.json`)
- Node.js 24 and npm
- Docker Desktop (for PostgreSQL/pgvector, Redis and the demo SQL Server)

## First-time setup

```bash
cp .env.example .env            # fill in passwords/keys; never commit .env
dotnet tool restore             # restores dotnet-ef
cd client/database-ai-agent-web && npm ci
```

## Run everything in Docker

```bash
docker compose up --build
# Web: http://localhost:8081    API: http://localhost:5080    Health: http://localhost:5080/health/ready
```

## Run API and web locally (faster inner loop)

```bash
docker compose up -d postgres redis demo-sqlserver

# Supply secrets via user-secrets (never appsettings files):
dotnet user-secrets --project src/DatabaseAIAgent.Api set "ConnectionStrings:Platform" "Host=localhost;Port=5432;Database=database_ai_agent;Username=postgres;Password=<from .env>"

dotnet run --project src/DatabaseAIAgent.Api --launch-profile http     # http://localhost:5080, API docs at /scalar (Development only)
cd client/database-ai-agent-web && npm start                            # http://localhost:4200 (proxies /api to :5080)
```

In Development, migrations are applied automatically at startup (`PlatformDatabase:ApplyMigrationsOnStartup`).
In every other environment, apply migrations in the deployment pipeline (documented in Deployment.md, Phase 12). Never change the production schema by hand.

## First run: create the Owner

1. Open the web app and choose **Create the owner account** (`/register`). The first account becomes the workspace **Owner**; registration then closes (invitation only).
2. Open the verification email:
   - `docker compose`: Mailpit at http://localhost:8025
   - API run locally in Development: `.eml` files in `src/DatabaseAIAgent.Api/mail-pickup/` (`Email:Transport=PickupDirectory`)
3. Follow the link, sign in, and invite teammates from **Team**.

Development needs no JWT key (an ephemeral one is generated; you stay signed in across restarts because refresh
tokens are stored in the database). Every other environment requires `Jwt__SigningKey` (`JWT_SECRET`).

## Connecting a local database

In Development, loopback and private addresses are allowed (`DatabaseConnections:AllowLoopback/AllowPrivateNetworks`
in `appsettings.Development.json`), so you can connect to the compose demo SQL Server (`localhost,1433`) or any local
database. Credentials are encrypted with a per-machine development key (created on first use in your user profile).
Other environments must set `ENCRYPTION_KEY`.

## Build and test

```bash
dotnet build DatabaseAIAgent.slnx
dotnet test --solution DatabaseAIAgent.slnx

cd client/database-ai-agent-web
npx ng build
npx ng test --watch=false
```

Integration tests against real SQL Server, PostgreSQL and MySQL containers live in
`tests/DatabaseAIAgent.IntegrationTests`. They need Docker and skip themselves when it is unavailable.

Analyzer warnings are errors (`Directory.Build.props`). Package versions are managed centrally in `Directory.Packages.props`.

## Migrations

```bash
dotnet ef migrations add <Name> \
  --project src/DatabaseAIAgent.Infrastructure \
  --startup-project src/DatabaseAIAgent.Api \
  --output-dir Persistence/Migrations

# Produce an idempotent SQL script for review/deployment:
dotnet ef migrations script --idempotent \
  --project src/DatabaseAIAgent.Infrastructure --startup-project src/DatabaseAIAgent.Api -o artifacts/migrate.sql
```

Design-time tooling reads `POSTGRES_CONNECTION_STRING` (falls back to the local compose database).

## Configuration conventions

- `appsettings.json` holds non-secret defaults; `.Development.json` and `.Production.json` hold environment overrides.
- Secrets come from environment variables or a secret manager, e.g. `ConnectionStrings__Platform`, `ConnectionStrings__Redis`.
- `ApiSecurity:AllowedOrigins` must list exact origins (validated at startup; wildcards are rejected).
