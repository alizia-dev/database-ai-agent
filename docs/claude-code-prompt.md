# Claude Code Prompt: Database AI Agent (Single-Tenant)

You are the lead software architect, senior .NET engineer, Angular engineer, AI engineer, security engineer, DevOps engineer, QA engineer, and product engineer for this project.

Build a **production-ready, commercially deployable platform** called **Database AI Agent**.

The product lets a business connect its existing databases and talk to its business data through a secure AI chat interface. It must work with CRM, ERP, accounting, inventory, banking/fintech, healthcare, ecommerce, facility management, sales, support and other business systems.

The customer must NOT migrate their database into our platform. The platform connects securely to the existing database, discovers approved tables and stored procedures, creates AI tools from approved stored procedures, indexes database/business metadata for RAG, and lets authorized users ask natural-language questions.

## IMPORTANT: SINGLE-TENANT ARCHITECTURE (NO MULTI-TENANCY)

This system is **single-tenant**. One deployment serves one organization (one workspace).

- Do NOT implement Tenant, TenantUser, TenantId columns, tenant resolution middleware, tenant query filters, or tenant-isolation tests.
- Use a single `Organization` / `WorkspaceSettings` record (singleton) for workspace-level configuration (name, industry, assistant name, timezone, currency, date format, business description, retention policies).
- Users belong directly to the application; roles/permissions apply at the application level.
- Data separation for different customers is achieved by **separate deployments** (separate database, secrets, and infrastructure per customer), not by shared-schema isolation. Keep configuration environment-driven so the same codebase can be deployed per customer.
- Everything else in this spec (security, least privilege, billing, RAG, tools, audit) still applies.

Never allow: unauthorized users reaching database credentials, disabled/unapproved tools, billing information, audit logs, or other users' conversations (users see only their own conversations unless they hold an admin permission).

---

## 1. Product Flow

```text
Register -> Verify Email -> Choose Subscription -> Payment -> Workspace Activated
-> Connect Existing Database -> Analyze (tables, columns, relationships, stored procedures, parameters, metadata)
-> Generate AI Tools -> Admin Approval -> Create RAG Knowledge -> AI Chat
-> LLM -> Tool Calling -> Approved Stored Procedure -> Customer Database -> AI Response
```

Transactional business data stays in the customer's database. Do NOT copy it into the platform.

## 2. Technology Stack

**Backend:** C#, latest stable .NET (.NET 10 if available), ASP.NET Core Web API, EF Core (platform DB), Dapper (customer DB / tool execution), ASP.NET Core Identity (or equivalent), SignalR (streaming chat/status), FluentValidation, Serilog, OpenTelemetry, Polly, Redis where appropriate.

**Frontend:** latest stable Angular, TypeScript, Angular Material (or equivalent professional UI library), RxJS, SignalR client, responsive (desktop-first, mobile-friendly).

**Platform DB:** PostgreSQL + pgvector for embeddings (behind `IVectorStore`).

**Customer DB support (initial):** SQL Server, PostgreSQL, MySQL, behind `ICustomerDatabaseProvider` (`SqlServerDatabaseProvider`, `PostgresDatabaseProvider`, `MySqlDatabaseProvider`).

**AI providers (initial):** OpenAI, Anthropic, Qwen-compatible APIs. Abstractions: `IChatModel`, `IEmbeddingModel`, `IToolCallingModel`. Design so private/local LLMs can be added for enterprise.

## 3. Architecture

Clean/Modular Architecture. No god classes.

```text
src/
  DatabaseAIAgent.Api
  DatabaseAIAgent.Application
  DatabaseAIAgent.Domain
  DatabaseAIAgent.Infrastructure
  DatabaseAIAgent.Agent
  DatabaseAIAgent.Rag
  DatabaseAIAgent.Tools
  DatabaseAIAgent.Database
  DatabaseAIAgent.Billing
  DatabaseAIAgent.Security
  DatabaseAIAgent.Workers
client/
  database-ai-agent-web/
```

## 4. Core Entities (no tenant entities)

```text
User, Role, Permission, Organization (singleton settings), Subscription, Plan, Payment, Invoice,
DatabaseConnection, DatabaseProvider, DatabaseSchema, DatabaseTable, DatabaseColumn,
DatabaseRelationship, StoredProcedure, StoredProcedureParameter, AiTool, ToolPermission,
KnowledgeDocument, KnowledgeChunk, Embedding, Conversation, ConversationMessage,
UsageRecord, AuditLog, ApiKey, WebhookEvent
```

## 5. Authentication

Email/password registration, email verification, secure login/logout, password reset and change, session/token management (refresh tokens if JWT), account lockout, rate limiting, secure password hashing, MFA-ready architecture, optional Google/Microsoft OAuth architecture. Never store plaintext passwords.

## 6. Access Model

- **Visitor:** landing page, product explanation, screenshots, demo video, pricing, docs, features, security info.
- **Registered but unpaid:** login, dashboard, demo environment, pricing, docs. Cannot connect a real database, execute real tools, or use production chat.
- **Subscribed:** connect database, analyze schema, configure tools, create RAG knowledge, use AI chat, manage users, view usage, manage subscription per plan.

## 7. Subscription Plans (configurable, never hard-coded; editable from admin)

- **Free/Demo ($0):** product tour, sample database, sample chat, limited demo questions, no production DB.
- **Starter ($49/mo):** 1 database connection, 5 users, 25 AI conversations/day, 20 approved tools, 10,000 tool executions/month, RAG, SQL Server/Postgres/MySQL, basic audit logs, email support.
- **Professional ($149/mo):** up to 3 database connections, 15 users, 200 conversations/day, 100 tools, 100,000 executions/month, advanced RAG, analytics, RBAC, advanced audit logs, multiple AI providers, priority support.
- **Business ($399/mo):** up to 10 database connections, 50 users, 500 conversations/day, 500 tools, 500,000 executions/month, advanced permissions, custom terminology, custom tool configuration, API access, advanced analytics, priority support, dedicated onboarding.
- **Enterprise (custom):** negotiated connections/users, SSO, MFA, private deployment, VPC/private networking, customer-managed infrastructure, local/private LLM, custom models/integrations, dedicated support, SLA, security review, custom retention.

Annual billing about 20% discount (Starter $470/yr, Professional $1,430/yr, Business $3,830/yr). Configurable trial (default 7 days; payment method requirement configurable). Pricing page must load plans from the API.

## 8. Billing Architecture

Interfaces: `IPaymentProvider`, `ISubscriptionService`, `IInvoiceService`, `IBillingService`.

- **Stripe (primary, default):** Checkout, subscriptions, webhooks, failed payments, trials, upgrades, downgrades, cancellation, refunds, customer portal, cards (Visa/Mastercard/Amex), Apple Pay/Google Pay where available. Never store raw card numbers/CVV; use Stripe-hosted components.
- **PayPal (secondary):** subscriptions, webhooks, status sync, cancellation, refunds where supported.
- Visa/Mastercard are card networks, not gateways. The UI says "Pay with Card (Visa, Mastercard, Amex...)".
- Store `StripeProductId`, `StripeMonthlyPriceId`, `StripeAnnualPriceId` per plan in DB/config. No hard-coded Price IDs.
- Flow: Register -> Choose Plan -> Checkout -> Provider -> Webhook -> Verify -> Activate Subscription -> Entitlements -> Production features. **Never activate based solely on a frontend redirect.** Show "Payment processing..." while the webhook is delayed.
- Webhooks: secure signature verification, idempotent, store processed event IDs, never process twice. Handle checkout completed, subscription created/updated/cancelled, invoice paid/failed, payment succeeded/failed.

## 9. Database Connection Security (most sensitive area)

- Inputs: server, database, username, password, or connection string.
- Never store connection strings in plaintext. Encrypt at rest, with secure key management (Azure Key Vault, AWS Secrets Manager, env/secret injection).
- Never return credentials via API; never log passwords, connection strings, tokens, API keys, or secrets.
- Test connection before saving: validate credentials, provider, minimum permissions. Recommend a dedicated read-only DB user with EXECUTE permission on approved stored procedures only (not broad SELECT). Provide instructions for creating that account.
- Support cloud databases directly. Design a future secure Connector/Agent for private-network databases so internal databases need not be exposed publicly.

## 10. Discovery

Discover databases, schemas, tables, columns, primary/foreign keys, indexes, relationships, views, stored procedures and parameters (name, schema, description, parameter types, required/optional, result columns if discoverable, permissions, execution duration). Store **metadata only**.

## 11. AI Tool Generation and Approval

Generate tool definitions (JSON schema) from stored procedures, e.g. `get_customer_orders` with `customerId`, `fromDate`, `toDate`.

- Never auto-expose procedures. Default state is **Disabled** until an admin approves.
- Admin sees: procedure, description, parameters, risk level, read/write classification, AI tool name and description. Admin can approve, reject, disable, edit description, configure permissions.
- Classification: READ, WRITE, DELETE, ADMIN. MVP enables only READ by default. WRITE/DELETE require explicit approval, user permission, a confirmation dialog ("You're about to change John Smith's credit limit from $25,000 to $50,000. [Confirm] [Cancel]"), and audit logging.
- Every tool has: Tool ID, Name, Description, Risk Level, Operation Type, Stored Procedure, Allowed Roles, Enabled, Timeout, Max Rows, Max Result Size.
- Execution pipeline: authentication -> authorization -> subscription -> tool enabled -> tool permission -> parameter validation -> timeout -> execution -> audit.

## 12. RAG

Index database metadata, table/column descriptions, relationships, stored procedure and tool descriptions, business terminology, business rules, customer documentation. RAG is NOT for current transactional data; current data comes from tools.

Knowledge types: Database, Business, Tool, Documentation, Custom. Admins can upload PDF, DOCX, TXT, Markdown, CSV (validated, malware-scan architecture). Admins define terminology (e.g. "Overdue" = invoice unpaid after due date) stored in RAG.

## 13. AI Agent

Real tool-using agent: user question -> conversation context -> RAG retrieval -> available tools -> LLM -> tool selection -> validation -> permission check -> execution -> database -> tool result -> LLM -> final response. Supports multiple tool calls per request.

- **Never trust the LLM.** The backend validates every tool call. The LLM cannot bypass permissions, execute arbitrary SQL, modify connection strings, use disabled tools, or access unauthorized data.
- **No arbitrary SQL by default** (LLM -> approved tool -> stored procedure -> DB). If NL-to-SQL is added later it must be read-only, restricted, validated, timeout- and row-limited, and audited.
- **Prompt injection defense:** clearly separate SYSTEM INSTRUCTIONS, USER INPUT, RAG DATA, DATABASE DATA, TOOL RESULTS. Retrieved/database content is data, never instructions. Strict tool policies.
- **Limits (configurable):** MaxIterations=8, MaxToolCallsPerRequest=10, MaxRowsPerTool=500, MaxExecutionTime, MaxTokens, MaxContextSize. Prevent infinite loops.
- **Honesty:** don't invent info. If unsupported say: "I couldn't find enough information in your connected business data to answer that confidently." Distinguish known-from-database, known-from-knowledge-base, inference, unknown. Show source tool and data-retrieved timestamp where possible. Never expose chain-of-thought, only safe status messages.
- **Cost control:** per-workspace AI budget, monthly limits, per-request token limits, model selection by plan, result filtering/pagination, summaries, conversation summarization, safe caching. Never send massive result sets to the LLM; minimize data sent to external LLMs.
- Platform-level AI keys now; customer-owned keys and local models for enterprise later. Never expose provider keys to customers.

## 14. Security

HTTPS only, encryption at rest, encrypted secrets, secure cookies, CSRF where applicable, restricted CORS, rate limiting, input/output validation, RBAC, audit logs, secure headers, SQL injection/XSS/SSRF protection, request size limits, file upload validation, query timeouts, max rows. Never trust `userId`, `role`, or `subscriptionId` from the frontend; derive from authenticated context. Errors shown to users are safe and generic ("We could not execute this request. Please try again or contact your administrator."); details go to internal logs with correlation IDs.

## 15. RBAC

Roles: Owner, Admin, Manager, Analyst, Viewer (no PlatformAdmin/tenant roles). Configurable permissions: `database.view/manage/analyze`, `tools.view/manage/execute`, `knowledge.view/manage`, `chat.use`, `billing.view/manage`, `users.view/manage`, `audit.view`.

## 16. Audit, Usage, Plan Enforcement

- **Audit:** user, timestamp, IP, action, tool, redacted parameters, database, stored procedure, status, duration, result metadata, AI model, token usage, cost estimate. Admin audit UI.
- **Usage:** AI requests, tool calls, DB executions, tokens, estimated cost, RAG searches, documents, users, API calls (UserId, Timestamp, UsageType, Quantity, Model, EstimatedCost). Monthly aggregation.
- **Enforcement (server-side only):** authenticate -> subscription -> entitlement -> permission -> usage limit -> execute. Example: Starter at 9,999/10,000 executions is allowed once more; the next is denied with "Monthly tool execution limit reached."

## 17. Database Protection

Command timeout, max result rows, max result size, max concurrent executions, rate limiting, circuit breaker. Timeouts are cancelled, logged, and return a safe error. Health checks run on a schedule per plan (connection status, last success, average tool time, failed executions, latency); don't poll constantly.

## 18. Background Workers and Queue

Workers handle database analysis, embedding generation, document processing, usage aggregation, health checks, webhook processing, cleanup. Never run long analysis inside an HTTP request. API -> Job Queue -> Worker, with an abstraction allowing Redis/RabbitMQ/Azure Service Bus/SQS.

## 19. API Keys

Workspace-scoped API keys: hashed storage, shown once, create/revoke/rotate, last-used.

## 20. Observability

Structured logging (Serilog), correlation IDs, OpenTelemetry metrics and tracing, error tracking, AI usage and tool execution metrics.

## 21. Frontend

Professional B2B SaaS look: clarity, trust, security, business usability. Avoid generic AI-style UI, heavy gradients, clutter, and developer-only screens.

**Routes:** `/`, `/pricing`, `/demo`, `/security`, `/login`, `/register`, `/forgot-password`, `/app`, `/app/dashboard`, `/app/database`, `/app/database/connect`, `/app/database/schema`, `/app/tools`, `/app/knowledge`, `/app/chat`, `/app/conversations`, `/app/usage`, `/app/billing`, `/app/team`, `/app/audit`, `/app/settings`.

- **Landing:** hero "Talk to Your Business Database with AI"; sections: how it works, features, supported databases, AI providers, security, pricing, demo, FAQ, CTA ("Try the Demo", "Start Free").
- **Pricing:** plans from API, monthly/yearly toggle (20% annual discount).
- **Billing page:** current plan, status, next billing date, payment method, invoices, usage, upgrade/downgrade/cancel (Stripe customer portal; equivalent PayPal flow).
- **Chat:** streaming, markdown, tables, code blocks, tool execution indicators, loading/error states, history, new/rename/delete/export conversation, feedback. Status examples: "Analyzing your question...", "Calling GetOutstandingInvoices...", "Found 27 invoices...".
- **Dashboard:** active users, AI requests, tool executions, database connections, usage, estimated AI cost, subscription. Also users, databases, tables, procedures, tools, knowledge, conversations, usage, billing, audit, settings.
- **Security Center:** DB encryption, access controls, audit logs, AI data handling, credential protection. No unsupported compliance claims.
- **Onboarding wizard (10 steps, with progress):** workspace setup, industry, connect DB, test connection, analyze, review procedures, approve tools, configure knowledge, invite team, start chat.
- **Customer-customizable AI:** assistant name, business description, industry, terminology, response style, timezone, currency, date format.
- Support: help center, contact support, docs, FAQ.

## 22. Demo Environment

Registered unpaid users get an isolated seeded Demo CRM database (Customers, Products, Orders, OrderItems, Invoices, Payments, Inventory) that never touches real customer data. Stored procedures: GetCustomers, GetCustomerOrders, GetTopCustomers, GetSalesSummary, GetOutstandingInvoices, GetLowStockProducts, GetProductSales, GetCustomerPurchaseHistory. Demo tools auto-created. Seed demo questions: customer count, top 10 customers, last month's sales, customers owing over $10,000, low stock, no purchase in 90 days, total outstanding balance, best-selling products.

## 23. Admin Area, Retention, Privacy

- Admin area (for the workspace Owner/Admin): users, subscription, usage, AI usage, tool executions, system health, failed payments, security events. Admins do not see other users' private conversations unless explicitly permitted and audited.
- Configurable retention for conversations, audit, usage, documents. Data export and account deletion workflow. Deleting the workspace removes users, conversations, RAG documents, embeddings, metadata, credentials and usage per policy. **Never delete the customer's external database.**
- Privacy-ready (export, deletion, retention, consent, privacy policy/ToS/cookie policy hooks, audit trail). Do not claim legal compliance automatically. State clearly: "Customer data is not stored as a copy of the source database."

## 24. Delivery Modes

Support self-service SaaS, assisted onboarding (our team configures DB/tools), and enterprise service (security assessment, architecture review, custom deployment, SLA).

## 25. Configuration, Docker, Deployment

- `appsettings.json`, `.Development.json`, `.Production.json`; secrets via environment variables / secret manager, never in source control.
- `.env.example` with placeholders: `DATABASE_CONNECTION_STRING`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `QWEN_API_KEY`, `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `PAYPAL_CLIENT_ID`, `PAYPAL_CLIENT_SECRET`, `JWT_SECRET`, `ENCRYPTION_KEY`, `REDIS_CONNECTION_STRING`, `POSTGRES_CONNECTION_STRING`.
- `Dockerfile` and `docker-compose.yml` (API, Angular, PostgreSQL, Redis, demo SQL Server) for reproducible local dev.
- EF Core migrations for the platform DB; seed roles, permissions, plans, demo data. Never change production schema manually.
- Primary deployment docs for **Azure** (Static Web Apps/App Service, Azure Database for PostgreSQL, Azure Cache for Redis, Key Vault), with AWS (S3+CloudFront, ECS/Fargate, RDS, ElastiCache, Secrets Manager) as an alternative. Keep the architecture cloud-neutral and document the per-customer deployment procedure.

## 26. Testing

- **Unit:** permissions, tool generation, tool validation, billing logic, subscription entitlement, RAG retrieval, AI provider abstraction.
- **Integration:** SQL Server, PostgreSQL, MySQL, Stripe webhook, PayPal webhook, AI provider, pgvector.
- **Security:** unauthorized tool, disabled tool, unauthorized database, invalid/expired subscription, prompt injection, SQL injection, XSS, SSRF, file upload attacks, rate limit abuse, unauthorized access to credentials/billing/audit.
- **E2E:** register -> verify -> login -> choose plan -> pay -> webhook -> subscription active -> connect DB -> analyze -> approve tool -> chat -> tool execution -> AI answer.

## 27. Documentation

README.md, Architecture.md, Development.md, Deployment.md, Security.md, DatabaseConnectors.md, AIProviders.md, RAG.md, ToolCalling.md, Billing.md, Stripe.md, PayPal.md, Troubleshooting.md, API.md, CustomerOnboarding.md (no MultiTenancy.md; document the single-tenant, per-customer deployment model instead in Architecture.md/Deployment.md).

## 28. Code Quality

SOLID, Clean Architecture, DRY, dependency injection, async/await with cancellation tokens, nullable reference types, structured logging, meaningful names, small focused classes, unit-testable. Avoid god classes, static global state, hardcoded secrets/user IDs/plan IDs/Stripe IDs/AI providers, and arbitrary SQL execution.

## 29. No Mocks Rule

Do not build mock screens. The system must work end to end: Angular -> ASP.NET Core API -> Auth -> Subscription -> DB Connection -> Discovery -> Stored Procedure Discovery -> Tool Generation -> Approval -> RAG -> LLM -> Tool Calling -> Stored Procedure -> Actual Customer Data -> AI Response -> Angular Chat. No fake responses and no hardcoded customer data, except the isolated demo environment.

## 30. Development Process (work in phases; do not generate everything blindly)

1. Architecture and solution setup
2. Authentication, users, roles/permissions (single-tenant)
3. Billing and subscriptions
4. Database connectors
5. Schema and stored-procedure discovery
6. Tool registry and generation
7. RAG
8. AI provider abstraction
9. Agent and tool calling
10. Angular chat
11. Security, audit, usage
12. Production deployment
13. Full end-to-end testing

Do not proceed to the next phase until the current one builds and tests pass. After each phase run `dotnet build`, `dotnet test`, and the Angular build/tests, fix failures, and report: Completed, Files changed, Migrations, APIs created, Tests created, Known issues, Next phase.

## 31. Final Acceptance Test

Must work without mocks (except the demo environment): visitor sees landing page and demo -> registers -> verifies email -> logs in -> uses isolated demo -> chooses Professional -> pays via Stripe -> webhook confirms -> subscription Active -> dashboard -> connects SQL Server (credentials stored encrypted) -> metadata and stored procedures discovered -> reviews and approves read-only procedures -> tools generated -> metadata indexed into RAG -> asks "Which customers owe more than $10,000?" -> agent retrieves knowledge, selects a tool -> backend validates tool, permission, subscription -> stored procedure runs against real data -> LLM answers -> Angular streams the response with safe tool status -> usage and audit recorded -> plan limits enforced -> unauthorized users cannot access credentials, tools, billing, or others' conversations -> upgrade/downgrade/cancel work -> failed payment handled -> admin can view usage.

## 32. Architectural Principle

The LLM is the reasoning/orchestration component only. The backend is the security and execution authority. The database is the source of truth. RAG provides context. Tools provide controlled access to current data. Billing controls product access.

## 33. Decision Priorities

1. Security 2. Least privilege 3. Maintainability 4. Provider abstraction 5. Production reliability 6. Simplicity over unnecessary complexity. Never expose secrets, never trust the LLM with authorization, never allow arbitrary production database access.

## Start Now

Act as technical lead. Do not just explain; build. First inspect the repository, determine what already exists, and preserve working functionality (do not replace working implementations unnecessarily). Then produce an implementation plan mapped to the phases above and begin Phase 1.
