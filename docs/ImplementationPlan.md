# Implementation Plan

Source specification: [claude-code-prompt.md](claude-code-prompt.md). The platform is **single-tenant**: one deployment per customer.

Each phase must build and pass `dotnet test` and the Angular build/tests before the next phase starts.
Each phase ends with a report: Completed, Files changed, Migrations, APIs created, Tests created, Known issues, Next phase.

| # | Phase | Key deliverables | Status |
|---|-------|------------------|--------|
| 1 | Architecture and solution setup | Solution and layered projects, central package management, analyzers as errors, `Organization` singleton plus initial migration, API pipeline (Serilog, correlation IDs, safe ProblemDetails, security headers, CORS, rate limiting, body limits, health checks, OpenTelemetry), worker host, Angular 22 + Material shell with dev proxy, Docker/compose, `.env.example`, architecture tests | **Done** |
| 2 | Authentication, users, roles/permissions | ASP.NET Core Identity on the platform DB, register/verify email/login/logout, refresh-token rotation (httpOnly cookie), password reset/change, lockout, auth rate limits, MFA-ready, OAuth-ready; roles Owner/Admin/Manager/Analyst/Viewer; permission catalogue and policy-based authorization; workspace settings API; Angular auth pages, guards, interceptors | **Done** |
| 3 | Billing and subscriptions | `Plan` (DB-configured, admin-editable, Stripe price IDs in DB), `Subscription`, `Invoice`, `Payment`, `WebhookEvent` (idempotent); `IPaymentProvider` (Stripe, PayPal); Checkout, portal, webhooks with signature verification; entitlement service; trial config; pricing API and page; billing page | **Done** |
| 4 | Database connectors | `DatabaseConnection` with credentials encrypted at rest (`ISecretProtector` backed by an env key or Key Vault/Secrets Manager), `ICustomerDatabaseProvider` for SQL Server/PostgreSQL/MySQL (Dapper), connection test and permission check, SSRF guard on host input, least-privilege setup guide, connector UI | **Done** |
| 5 | Schema and stored-procedure discovery | Job queue abstraction (`IJobQueue`, Postgres-backed first, Redis/Service Bus/SQS adapters), analysis worker, metadata entities (schemas/tables/columns/relationships/procedures/parameters), schema browser UI | **Done** |
| 6 | Tool registry and generation | `AiTool` generation from procedures (JSON schema), READ/WRITE/DELETE/ADMIN classification and risk level, Disabled by default, admin approval workflow, `ToolPermission`, the execution pipeline (auth, authz, subscription, enabled, permission, validation, timeout, execute, audit), limits and circuit breaker | Next |
| 7 | RAG | `KnowledgeDocument`/`KnowledgeChunk`/`Embedding`, pgvector behind `IVectorStore`, metadata/tool/terminology indexing, document upload (validated, scan hook), retrieval API | |
| 8 | AI provider abstraction | `IChatModel`, `IEmbeddingModel`, `IToolCallingModel`; OpenAI, Anthropic, Qwen-compatible adapters; Polly resilience; per-plan model selection; token/cost accounting | |
| 9 | Agent and tool calling | Orchestrator loop (MaxIterations, MaxToolCalls, row and size limits), prompt-injection-safe context segmentation, backend validation of every tool call, WRITE confirmation flow, honesty and provenance rules, conversation summarization | |
| 10 | Angular chat and product UI | SignalR streaming chat with tool status, conversations CRUD and export, dashboard, onboarding wizard, landing/pricing/demo/security pages, team, settings | |
| 11 | Security, audit, usage | `AuditLog`, `UsageRecord` with monthly aggregation, server-side plan enforcement, API keys (hashed, rotate/revoke), admin audit UI, retention jobs, export/deletion workflow, demo environment seeding | |
| 12 | Production deployment | Azure (primary) and AWS (alternative) deployment docs/IaC, per-customer deployment procedure, secret management, CI pipeline | |
| 13 | Full end-to-end testing | Integration tests (Testcontainers: Postgres/pgvector, SQL Server, MySQL), Stripe/PayPal webhook tests, security tests, Playwright E2E acceptance flow | |

## Cross-cutting decisions (Phase 1)

- **Single-tenant**: a singleton `Organization` row (fixed id, enforced by a DB check constraint). No tenant columns, filters or middleware.
- **Layering** (enforced by `tests/DatabaseAIAgent.ArchitectureTests`): Domain depends on nothing; Application depends on Domain; feature modules (Agent, Rag, Tools, Database, Billing, Security) and Infrastructure depend only on Application/Domain and talk to each other through Application abstractions; Api and Workers are composition roots.
- **No MediatR/AutoMapper**: plain application services and explicit mapping, to avoid licensing and indirection.
- **Errors**: unhandled exceptions return a generic ProblemDetails with a correlation ID; details go to logs only.
- **Identity (Phase 2)**: ASP.NET Core Identity on the platform DB; `User`/`Role` derive from Identity's POCO base
  classes, the only framework dependency Domain is allowed (enforced by the architecture tests). JWT access tokens
  (memory only) + rotating refresh tokens (httpOnly cookie) with reuse detection; permissions resolved from the DB per
  request (cached), never from token claims. See [Security.md](Security.md).
- **Billing (Phase 3)**: provider-neutral `IPaymentProvider` (Stripe primary, PayPal secondary) translating webhooks into
  billing events applied by one applier; access only from verified webhooks; `IEntitlementService` + `[RequireFeature]`
  for plan gating. See [Billing.md](Billing.md).
- **Database connectors (Phase 4)**: `ICustomerDatabaseProvider` per engine, `ICustomerDatabaseConnector` to open
  connections by ID for later phases, AES-GCM `ISecretProtector`, and `HostAddressGuard` against SSRF. See
  [DatabaseConnectors.md](DatabaseConnectors.md).
- **Background jobs and discovery (Phase 5)**: database-backed `IJobQueue` (outbox semantics, conditional-update
  claiming, crashed-worker recovery, backoff); per-engine `ISchemaDiscoverer`; conservative routine behaviour
  analysis; catalog merge with stable procedure IDs.
- **Tests**: xUnit v3 on Microsoft.Testing.Platform; SQLite in-memory for fast persistence unit tests; PostgreSQL-specific behaviour is covered by integration tests (Phase 13 / Testcontainers).
