# PayPal setup

PayPal is the secondary provider, using the Subscriptions REST API. The customer approves the subscription on
PayPal; activation arrives through verified webhooks. Calls use an OAuth client-credentials token (cached) and a
standard resilience pipeline (retries with backoff, circuit breaker, timeouts).

## 1. App credentials

In the PayPal Developer Dashboard, create a REST app (Sandbox first, then Live) and set:

```text
PAYPAL_MODE=Sandbox            # or Live
PAYPAL_CLIENT_ID=...
PAYPAL_CLIENT_SECRET=...
```

## 2. Products and billing plans

Create a Catalog Product, then a Billing Plan per plan and interval (monthly / yearly). If you offer a trial through
PayPal, configure it on the PayPal plan itself: the platform cannot add one at checkout. Put the plan IDs into the
catalogue:

```text
Billing__Plans__2__PayPalMonthlyPlanId=P-...    # 2 = professional
Billing__Plans__2__PayPalAnnualPlanId=P-...
```

## 3. Webhook

Add a webhook for `https://<deployment-host>/api/billing/webhooks/paypal` with these events:

- `BILLING.SUBSCRIPTION.CREATED`, `.ACTIVATED`, `.UPDATED`, `.RE-ACTIVATED`, `.SUSPENDED`, `.CANCELLED`,
  `.EXPIRED`, `.PAYMENT.FAILED`
- `PAYMENT.SALE.COMPLETED`, `PAYMENT.SALE.DENIED`, `PAYMENT.SALE.REFUNDED`, `PAYMENT.SALE.REVERSED`

Copy the webhook ID into `PAYPAL_WEBHOOK_ID`. Each delivery is verified with PayPal's
`/v1/notifications/verify-webhook-signature` against this ID. PayPal is enabled only when the client ID, secret and
webhook ID are all set.

## Behaviour differences from Stripe

| | Stripe | PayPal |
|---|---|---|
| Trial | Set by the platform (`Billing:TrialDays`) | Configured on the PayPal plan |
| Cancel | At period end, can be undone | Immediate |
| Plan change | Immediate with proration | Customer approves the revision on PayPal |
| Payment method / history | Stripe customer portal | PayPal account → automatic payments |
| Status mapping | | `ACTIVE` → Active (PastDue if a payment failed), `SUSPENDED` → PastDue, `CANCELLED`/`EXPIRED` → Canceled, `APPROVAL_PENDING`/`APPROVED` → Incomplete |

The subscription's `custom_id` carries `Billing:AccountReference`, so a shared PayPal account is safe across
deployments.
