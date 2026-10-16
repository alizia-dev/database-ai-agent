# Billing

Billing controls product access. Plans are data, payments are hosted by the provider, and **access changes only
when a verified provider webhook says so**. A browser returning from checkout never activates anything.

## Concepts

| Entity | Purpose |
|---|---|
| `Plan` | Code, prices (display only), limits, feature flags, pricing-page highlights, Stripe product/price IDs, PayPal plan IDs. |
| `Subscription` | The workspace's subscription with one provider: status, interval, period, trial, cancel-at-period-end. Updated only from provider events, in order (older events are ignored). |
| `Invoice` / `Payment` | Local copies of provider invoices and payment attempts. Only `https` links are kept; there is never any card data. |
| `WebhookEvent` | Every verified webhook, stored once per provider event ID (unique index). It gives idempotency, an audit trail, and retry. |
| `BillingAccount` | Singleton: Stripe customer ID, billing email, pending-checkout marker. |

## Plans (configurable, never hard-coded)

Plans are seeded from `Billing:Plans` in configuration on first start (the defaults in `appsettings.json` follow the
product specification):

| Plan | Monthly | Annual | Connections | Users | Conversations/day | Tools | Executions/month |
|---|---|---|---|---|---|---|---|
| Free / Demo | $0 | $0 | 0 (demo only) | 1 | 10 | 8 | 500 |
| Starter | $49 | $470 | 1 | 5 | 25 | 20 | 10,000 |
| Professional | $149 | $1,430 | 3 | 15 | 200 | 100 | 100,000 |
| Business | $399 | $3,830 | 10 | 50 | 500 | 500 | 500,000 |
| Enterprise | custom | custom | negotiated | negotiated | negotiated | negotiated | negotiated |

- Seeding inserts missing plans and **never overwrites** existing ones. It only fills provider IDs that are still
  empty, so a deployment can supply price IDs later through environment variables, e.g.
  `Billing__Plans__2__StripeMonthlyPriceId` (index 2 is Professional).
- Editing plans through the API (`PUT /api/billing/plans/{code}`) is **disabled by default**
  (`Billing:AllowPlanEditing=false`). A customer Owner must not be able to raise their own limits. Enable it only in
  operator-managed deployments.
- Displayed prices are informational. The amount charged is defined by the provider price/plan the IDs point to, so
  keep them consistent.
- The pricing page reads `GET /api/billing/plans`; it never contains prices.

## Entitlements

`IEntitlementService` (Application layer) is the single server-side source of truth for plan-based access:

- A subscription that is `Active` or `Trialing` grants its plan.
- `PastDue` keeps access for `Billing:PastDueGraceDays` (default 7) after the first failed renewal, and the UI warns.
- Anything else (no subscription, `Canceled`, `Unpaid`, `Paused`, `Incomplete`, expired) falls back to the free plan.
- Endpoints gate on features with `[RequireFeature(PlanFeatures.ProductionAccess)]`. A blocked request returns 403
  with `code: "billing.upgrade_required"`. Limits (users, connections, executions) are enforced by the modules that own
  them, using the same entitlements (usage metering arrives in Phase 11).

## Flows

**Checkout.** `POST /api/billing/checkout { planCode, interval, provider }` (requires `billing.manage`):

1. The server picks the provider price ID and builds success/cancel URLs from `App:PublicUrl`; nothing comes from
   the client.
2. The first subscription of a workspace gets a `Billing:TrialDays` trial (default 7). Whether a card is required for
   the trial is set by `Billing:TrialRequiresPaymentMethod`.
3. The response contains the provider-hosted URL. The SPA redirects only to known provider hosts.
4. On return, the billing page shows **Payment processing…** and polls until the webhook-confirmed subscription
   appears (up to 2 minutes, then it explains what to expect).

**Change plan** (`POST /api/billing/change-plan`): Stripe swaps the subscription price with proration; PayPal returns
an approval URL (plan revision). Either way, the new state arrives by webhook.

**Cancel** (`POST /api/billing/cancel { atPeriodEnd: true }`): Stripe cancels at period end and can be undone with
`POST /api/billing/resume`. PayPal cancels immediately (PayPal has no scheduled cancellation).

**Manage payment method and invoices** (`POST /api/billing/portal`): Stripe customer portal, or PayPal autopay settings.

**Failed payments**: the provider moves the subscription to past due, the UI shows a banner with an "Update payment
method" link, access continues through the grace period, and the subscription is later canceled by the provider
according to its dunning settings.

**Refunds** are an operator action, issued in the Stripe or PayPal dashboard (`IPaymentProvider.RefundAsync` exists for
operator tooling). The refund webhook updates the local payment record. Customers cannot refund themselves.

## Webhooks

| Provider | Endpoint | Verification |
|---|---|---|
| Stripe | `POST /api/billing/webhooks/stripe` | `Stripe-Signature` HMAC-SHA256 with `Stripe:WebhookSecret`; timestamps older than `Stripe:WebhookToleranceSeconds` (300) are rejected (replay protection). |
| PayPal | `POST /api/billing/webhooks/paypal` | PayPal `verify-webhook-signature` API with `PayPal:WebhookId`. |

Processing works as follows:

1. Verify the signature (otherwise 400). A disabled provider returns 404.
2. Store the event once. A redelivered or concurrent duplicate is acknowledged with 200 and not reapplied.
3. Ignore events that do not belong to this deployment. An event belongs here only if its `account_reference`
   metadata matches `Billing:AccountReference`, or it concerns a customer or subscription this deployment already knows.
4. Apply the event and mark it processed in the same database save. If applying fails (e.g. an unknown price ID), the
   changes are discarded, the event is stored as `Failed`, and the endpoint returns 500 so the provider redelivers.
   The Workers host (`WebhookRetryService`) also retries failed events every minute, up to 5 attempts.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Billing:AccountReference` | | Required when a provider is configured. Unique per deployment. |
| `Billing:TrialDays` | 7 | 0 disables trials. |
| `Billing:TrialRequiresPaymentMethod` | true | Stripe only. |
| `Billing:PastDueGraceDays` | 7 | |
| `Billing:AllowPlanEditing` | false | Operator-managed deployments only. |
| `Billing:SalesContactEmail` | | Shown as "Contact sales" for Enterprise. |
| `Stripe:SecretKey`, `Stripe:WebhookSecret` | | Stripe is enabled when both are set. See [Stripe.md](Stripe.md). |
| `PayPal:Mode`, `ClientId`, `ClientSecret`, `WebhookId` | Sandbox | PayPal is enabled when all are set. See [PayPal.md](PayPal.md). |
