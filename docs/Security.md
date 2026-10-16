# Security

This document grows with each phase. It describes what is implemented; it makes no compliance claims.
Customer data is not stored as a copy of the source database.

## Principles

- The backend is the security and execution authority. The browser and the LLM are never trusted for identity,
  role, permission or subscription decisions; these are derived from the validated access token and the database.
- Least privilege by default: every endpoint requires an authenticated user unless explicitly marked public, and
  sensitive actions require a named permission.
- User-facing errors are generic and carry a correlation ID; details go to internal logs only.

## Authentication (Phase 2)

| Concern | Implementation |
|---|---|
| Accounts | ASP.NET Core Identity on the platform database (`platform.users`, `platform.roles`, ...) |
| Password storage | Identity `PasswordHasher` (PBKDF2-HMAC-SHA512, salted, high iteration count). Plaintext passwords are never stored or logged. |
| Password policy | Minimum 12 characters, at least 4 distinct characters, maximum 128 (prevents hashing very large inputs). Length over composition rules, following NIST SP 800-63B. |
| Lockout | 5 failed attempts lock the account for 15 minutes. A successful password reset clears the lockout. |
| Rate limiting | Per-IP limits: 10/min on login, registration, verification, password reset and invitation endpoints; 30/min on refresh; 300/min globally. All configurable (`ApiSecurity:*`). |
| Account enumeration | Registration, resend-verification and forgot-password always return the same response. Unknown-email logins return the same error as wrong passwords, after equivalent hashing work. |
| Email verification | Required before first sign-in (`Auth:RequireVerifiedEmail`). Tokens are Data Protection tokens with a 2-day lifetime. |
| Password reset | Dedicated token provider with a 60-minute lifetime. Tokens are bound to the security stamp, so they stop working once the password changes (single use). A reset signs out every session. |
| Access tokens | JWT, HMAC-SHA256, 10-minute lifetime, issuer, audience and algorithm validated, 30-second clock skew. They carry identity only (`sub`, `email`, `name`, `role`, security stamp), never permissions. Kept in browser memory only. |
| Session validation | Each request checks that the user is still active and that the token's security stamp is current (cached up to 30 seconds). Password change, "sign out everywhere" and deactivation rotate the stamp. |
| Refresh tokens | 256-bit random opaque tokens; only the SHA-256 hash is stored. They are rotated on every use. Presenting an already-rotated token after a 10-second grace window revokes the whole token family (theft detection). Delivered only as an `HttpOnly; Secure; SameSite=Strict` cookie scoped to `/api/auth`. |
| CSRF | Cookie-authenticated endpoints (refresh, logout) require an `X-Requested-With` header, which cross-site forms cannot send. CORS allows explicitly configured origins only. Bearer-authenticated endpoints are not CSRF-prone. |
| Tokens in URLs | Invitation and verification tokens travel in POST bodies, never API URLs, so they do not reach access logs. Email links point to the web app, which posts them. |
| Signing key | `Jwt:SigningKey` (`JWT_SECRET`), at least 32 bytes, validated at startup. Development generates an ephemeral key if none is set; any other environment fails to start without one. |
| Data Protection keys | Persisted in `platform.data_protection_keys` so tokens survive restarts and work across instances. In production, also protect them at rest with a key-encryption key (Azure Key Vault / AWS KMS); see Deployment.md. |
| MFA / SSO readiness | Identity's two-factor and external-login stores and tables exist (`user_tokens`, `user_logins`). TOTP MFA and Google/Microsoft OAuth plug into these without schema changes; they are not enabled yet. |

## Authorization

- **Roles**: Owner, Admin, Manager, Analyst, Viewer. Each user holds exactly one.
- **Permissions**: `database.view/manage/analyze`, `tools.view/manage/execute`, `knowledge.view/manage`, `chat.use`,
  `billing.view/manage`, `users.view/manage`, `audit.view`, `settings.manage`.
- **Role to permission mapping** is stored in `platform.role_permissions` and editable (Team, then Roles &
  permissions). Owner always holds every permission. Only Owners can change the Admin role. Nobody can grant a
  permission they do not hold.
- **Enforcement**: `[RequirePermission("...")]` on endpoints. The check reads the user's role permissions from the
  database (cached up to 60 seconds per instance and invalidated immediately on change) and never trusts token claims.
- **Team rules**: actors manage only roles at or below their own. Nobody can change their own role or deactivate
  themselves. Only Owners can manage Owners. The last active Owner cannot be demoted or deactivated. Owner cannot
  be granted by invitation.
- **Single-tenant registration**: the first account to register becomes the Owner. After that, registration is by
  invitation only, unless `Auth:AllowPublicRegistration` is true (new users join as Viewer).

## Billing (Phase 3)

- No card data is ever handled. Checkout, payment-method updates and invoices live on Stripe- or PayPal-hosted
  pages. The SPA redirects only to known provider hosts.
- Subscriptions are activated only by **verified webhooks**: Stripe HMAC signatures with replay tolerance, PayPal
  signature verification through its API. Redirect URLs are built server-side from `App:PublicUrl`.
- Webhooks are idempotent: there is a unique (provider, event ID) index, and failed events are rolled back and retried.
- Events are scoped to this deployment by `Billing:AccountReference` (provider metadata / PayPal `custom_id`), or by an
  already-known customer or subscription.
- Plan-based access is enforced server-side (`IEntitlementService`, `[RequireFeature]`). Plan editing through the API
  is off by default, so customers cannot raise their own limits.
- Provider secrets come from configuration or a secret manager and are never returned by any API. Provider error
  details are logged with error codes, not shown to users.
- Invoice links from providers are stored only if they are `https`.

## Customer database connections (Phase 4)

- Credentials are encrypted with AES-256-GCM, bound to the connection ID, with key rotation support. The key comes
  from the secret manager (`ENCRYPTION_KEY`), and startup fails without it outside Development. Credentials are never
  returned by the API or logged.
- Connection strings are built only by driver builders from validated fields plus fixed safe options. Pasted strings
  are parsed and unsupported options dropped (e.g. SQL Server `AttachDBFilename`, MySQL `AllowLoadLocalInfile`).
- SSRF protection: the host syntax is restricted, and every resolved address is checked on save, test and each open.
  Metadata and link-local addresses are always blocked; loopback and private ranges are blocked unless explicitly
  allowed. Connection tests are rate limited.
- Each test inspects the account's privileges and warns when the account is not least-privilege. Driver error
  messages are mapped to safe categories.
- Connecting requires `database.manage` plus a plan with production access, and is limited by the plan's
  connection count.
- See [DatabaseConnectors.md](DatabaseConnectors.md).

## HTTP hardening (Phase 1)

Security headers (nosniff, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a restrictive CSP for JSON
responses), HSTS and HTTPS redirect outside Development, a request body size limit, and no `Server` header.
Correlation IDs are accepted from clients only if they are short and log-safe.

## Known limitations (tracked for later phases)

- The permission and session caches are per instance. With several API instances, a change made on one instance
  reaches the others within the cache lifetime (60 s / 30 s). Phase 11 can replace this with Redis pub/sub
  invalidation.
- Audit-log persistence of security events (sign-ins, lockouts, role changes) arrives with the audit module
  (Phase 11). Until then these events are written to structured logs.
