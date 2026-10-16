# Architecture

## Principle

The LLM is the reasoning/orchestration component only. The backend is the security and execution authority.
The customer database is the source of truth. RAG provides context. Tools provide controlled access to current data.
Billing controls product access.

## Deployment model: single-tenant, one deployment per customer

One deployment serves exactly one organization (workspace). Each customer gets its own:

- platform database (PostgreSQL + pgvector),
- secrets (JWT key, encryption key, AI/billing keys) in its own secret store,
- compute (API, workers, web) and Redis.

There is no shared-schema multi-tenancy: no `TenantId` columns, tenant resolution or tenant query filters.
Workspace configuration lives in a single `Organization` row with a fixed id (`00000000-0000-0000-0000-000000000001`),
protected by the `ck_organization_singleton` check constraint. All configuration is environment-driven, so the
same build artifact is deployed for every customer.

## Solution layout

```text
src/
  DatabaseAIAgent.Domain          Entities, value objects, invariants. No dependencies.
  DatabaseAIAgent.Application     Use cases and abstractions (ports): IClock, repositories, providers, queues.
  DatabaseAIAgent.Infrastructure  EF Core platform DB (PostgreSQL), time, queue/cache adapters.
  DatabaseAIAgent.Security        Identity, tokens, RBAC, credential encryption.
  DatabaseAIAgent.Billing         Plans, subscriptions, entitlements, Stripe/PayPal providers.
  DatabaseAIAgent.Database        Customer database providers (SQL Server, PostgreSQL, MySQL) via Dapper; discovery.
  DatabaseAIAgent.Tools           Tool generation, approval, execution pipeline.
  DatabaseAIAgent.Rag             Knowledge ingestion, chunking, IVectorStore (pgvector).
  DatabaseAIAgent.Agent           Agent orchestrator and AI provider adapters.
  DatabaseAIAgent.Api             ASP.NET Core host (composition root): HTTP, SignalR, middleware.
  DatabaseAIAgent.Workers         Background host (composition root): analysis, embeddings, webhooks, aggregation.
client/database-ai-agent-web      Angular 22 + Angular Material SPA.
tests/
  DatabaseAIAgent.UnitTests
  DatabaseAIAgent.ArchitectureTests  Enforces the dependency rules below.
  DatabaseAIAgent.Api.Tests          In-memory API pipeline tests (WebApplicationFactory).
```

### Dependency rules

```text
Api, Workers ──► every module ──► Application ──► Domain
```

- Domain references no project and no framework infrastructure (EF Core, ASP.NET Core).
- Application references only Domain.
- Feature modules and Infrastructure reference only Application and Domain. They never reference each other;
  cross-module collaboration goes through interfaces declared in Application and implemented by the owning module.
- Nothing references the host projects.

Both declared `ProjectReference`s and compiled assembly references are checked by the architecture tests.

## Request pipeline (API)

1. Forwarded headers (trusted proxies are configured per deployment)
2. Correlation ID: accepts a safe inbound `X-Correlation-ID`, otherwise generates one; echoed in responses and logs
3. Serilog request logging
4. Exception handler: generic ProblemDetails plus `correlationId`; internals are logged, never returned
5. Security headers (nosniff, DENY framing, no-referrer, strict CSP for JSON)
6. HSTS and HTTPS redirect (non-development)
7. CORS: explicit origins only, no wildcards (validated at startup)
8. Global rate limiter (per client IP, configurable)
9. Controllers and health checks (`/health/live`, `/health/ready`)

Kestrel enforces a global request body limit (`ApiSecurity:MaxRequestBodyBytes`); upload endpoints set their own limits.

## Data flow (target)

```text
Angular ─► API ─► Auth ─► Subscription/Entitlement ─► Permission ─► Usage limit
        ─► Agent ─► RAG (metadata/knowledge) ─► LLM ─► tool call
        ─► backend validation ─► approved stored procedure ─► customer DB
        ─► result (row/size-limited) ─► LLM ─► streamed answer ─► Angular
```

Transactional customer data is never copied into the platform database; only metadata and knowledge are stored.

## Long-running work

Never inside an HTTP request: API ─► job queue (abstraction) ─► Workers.

## Observability

Serilog structured logs (compact JSON in production), correlation IDs, OpenTelemetry traces/metrics
(ASP.NET Core, HttpClient, runtime, `DatabaseAIAgent.*` sources/meters), and OTLP export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
