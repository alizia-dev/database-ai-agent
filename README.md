# Database AI Agent

Talk to your business database with AI, securely. Connect an existing SQL Server, PostgreSQL or MySQL database,
approve exactly which stored procedures the assistant may call, and ask questions in natural language.
Customer data is not stored as a copy of the source database.

- **Single-tenant**: one deployment per customer organization (separate database, secrets and infrastructure).
- **Backend is the authority**: the LLM proposes tool calls; the API validates authentication, subscription,
  permissions, tool approval and parameters before any approved stored procedure runs.

## Stack

ASP.NET Core (.NET 10), EF Core + PostgreSQL/pgvector (platform DB), Dapper (customer DBs), Serilog, OpenTelemetry,
Angular 22 + Angular Material, Redis, Docker.

## Quick start

```bash
cp .env.example .env    # set passwords
docker compose up --build
```

See [docs/Development.md](docs/Development.md) for the local inner loop, tests and migrations.

## Documentation

- [Implementation plan and phase status](docs/ImplementationPlan.md)
- [Architecture](docs/Architecture.md)
- [Development](docs/Development.md)
- Product specification: [docs/claude-code-prompt.md](docs/claude-code-prompt.md)

The remaining guides (Deployment, Security, connectors, AI providers, RAG, tool calling, billing, API, onboarding)
are written in the phase that implements each area.
