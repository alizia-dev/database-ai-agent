# API

Base path `/api`. JSON over HTTPS. Interactive reference: `/scalar` (Development only); OpenAPI document at `/openapi/v1.json`.

## Conventions

- **Authentication**: `Authorization: Bearer <access token>` on every endpoint unless marked *public*.
- **Errors**: RFC 7807 `application/problem+json` with `detail` (safe to display), `code` (stable identifier),
  `correlationId`, and for validation errors `errors: { field: [messages] }`. 5xx responses never include internals.
- **Correlation**: send `X-Correlation-ID` (letters, digits, `-_.`, up to 64 characters) or receive a generated one.
- **Enums** are serialized as strings (e.g. `"responseStyle": "Balanced"`).

## Authentication: `/api/auth` (public, rate limited)

| Method | Path | Body | Success | Notes |
|---|---|---|---|---|
| POST | `/register` | `{ email, password, displayName }` | 202 `{ message }` | First account becomes Owner; afterwards 403 `auth.registration_closed` (invitation only). Same response whether or not the email exists. |
| POST | `/verify-email` | `{ userId, token }` | 204 | 400 `auth.invalid_link` |
| POST | `/resend-verification` | `{ email }` | 202 | Always 202 |
| POST | `/login` | `{ email, password }` | 200 session | 401 `auth.invalid_credentials`, 403 `auth.email_not_verified` / `auth.account_disabled`, 429 `auth.locked_out` |
| POST | `/refresh` | none (cookie) | 200 session | Requires the `X-Requested-With` header. 401 `auth.invalid_refresh_token`; 409 `auth.refresh_in_progress` (retry once) |
| POST | `/logout` | none (cookie) | 204 | Requires the `X-Requested-With` header; revokes this device's token family |
| POST | `/forgot-password` | `{ email }` | 202 | Always 202 |
| POST | `/reset-password` | `{ email, token, newPassword }` | 204 | 400 `auth.invalid_link` / `auth.password_invalid` |

**Session** response: `{ accessToken, accessTokenExpiresAt, user: { id, email, displayName, role, permissions[] } }`.
The refresh token is set only as the `__Secure-daa_rt` cookie (`HttpOnly; Secure; SameSite=Strict; Path=/api/auth`).

## Invitations: `/api/invitations` (public, rate limited)

| Method | Path | Body | Success |
|---|---|---|---|
| POST | `/preview` | `{ token }` | 200 `{ email, role, workspaceName }`; 404 `invitations.invalid` |
| POST | `/accept` | `{ token, displayName, password }` | 200 session (signed in) |

## Account: `/api/account` (signed in)

| Method | Path | Body | Success |
|---|---|---|---|
| GET | `/me` | | 200 user |
| PUT | `/profile` | `{ displayName }` | 200 user |
| POST | `/change-password` | `{ currentPassword, newPassword }` | 200 session (other sessions are revoked) |
| POST | `/logout-everywhere` | | 204 |

## Team: `/api/team`

| Method | Path | Permission | Body |
|---|---|---|---|
| GET | `/members` | `users.view` | |
| PUT | `/members/{userId}/role` | `users.manage` | `{ role }` |
| POST | `/members/{userId}/deactivate` | `users.manage` | |
| POST | `/members/{userId}/reactivate` | `users.manage` | |
| GET | `/invitations` | `users.view` | |
| POST | `/invitations` | `users.manage` | `{ email, role }` → 201 |
| DELETE | `/invitations/{invitationId}` | `users.manage` | → 204 |

Rule violations return 403 (`users.owner_protected`, `users.cannot_manage_self`, `users.role_not_allowed`) or 409 (`users.last_owner`, `invitations.already_member`).

## Roles: `/api/roles`

| Method | Path | Permission | Body |
|---|---|---|---|
| GET | `/` | `users.view` | → `[{ name, permissions[], memberCount, editable }]` |
| GET | `/permissions` | `users.view` | → permission catalogue |
| PUT | `/{roleName}/permissions` | `users.manage` | `{ permissions[] }`; 403 `roles.not_editable` / `roles.cannot_grant` |

## Workspace: `/api/workspace/settings`

| Method | Permission | Body |
|---|---|---|
| GET | signed in | → `{ name, industry, assistantName, businessDescription, timeZone, currency, dateFormat, responseStyle, retention, updatedAt }` |
| PUT | `settings.manage` | same fields except `updatedAt`; 400 `workspace.invalid_settings` |

## Billing: `/api/billing`

| Method | Path | Permission | Body / notes |
|---|---|---|---|
| GET | `/plans` | public | → `{ plans[], trialDays, trialRequiresPaymentMethod, salesContactEmail }`. Provider price IDs are never exposed. |
| GET | `/entitlements` | signed in | → `{ planCode, planName, isFreePlan, subscriptionStatus, hasProductionAccess, limits, features[], trialEndsAt, currentPeriodEnd, cancelAtPeriodEnd, paymentPastDue }` |
| GET | `/subscription` | `billing.view` | → `{ entitlements, plan, subscription, checkoutPending, canManagePaymentMethods }` |
| GET | `/history` | `billing.view` | → `{ invoices[], payments[] }` |
| POST | `/checkout` | `billing.manage` | `{ planCode, interval: "Monthly"\|"Annual", provider: "Stripe"\|"PayPal" }` → `{ url }` (provider-hosted). 409 `billing.already_subscribed`, 400 `billing.plan_not_purchasable` / `billing.provider_unavailable` / `billing.plan_not_configured`, 503 `billing.provider_error` |
| POST | `/portal` | `billing.manage` | → `{ url }` |
| POST | `/change-plan` | `billing.manage` | `{ planCode, interval }` → `{ status: "processing"\|"approval_required", approvalUrl }` |
| POST | `/cancel` | `billing.manage` | `{ atPeriodEnd }` → 204 |
| POST | `/resume` | `billing.manage` | → 204 (Stripe, scheduled cancellations only) |
| PUT | `/plans/{code}` | `billing.manage` + `Billing:AllowPlanEditing` | Operator-only plan editing; 403 `billing.plan_editing_disabled` by default |
| POST | `/webhooks/stripe`, `/webhooks/paypal` | provider signature | 200 processed/ignored/duplicate, 400 invalid signature, 404 provider disabled, 500 failed (provider retries) |

Endpoints gated by plan features return 403 with `code: "billing.upgrade_required"`.

## System (public)

`GET /api/system/info` → `{ name, version, environment }`; `GET /health/live`; `GET /health/ready`.
