# Stripe setup

Stripe is the primary provider: Checkout, subscriptions, trials, proration, the customer portal and invoices. The
platform uses Stripe-hosted pages only, so card data (Visa, Mastercard, Amex, Apple Pay / Google Pay where enabled)
never reaches it. Stripe.net 52.4.2, API version `2026-08-26.dahlia`.

## 1. Products and prices

For each paid, self-service plan (Starter, Professional, Business), create one **Product** and two recurring **Prices**:
monthly and yearly (the default catalogue uses $470 / $1,430 / $3,830 per year). Put the IDs into the plan, either:

- through configuration (filled on startup only if still empty):

  ```text
  Billing__Plans__1__StripeProductId=prod_...      # 1 = starter, 2 = professional, 3 = business
  Billing__Plans__1__StripeMonthlyPriceId=price_...
  Billing__Plans__1__StripeAnnualPriceId=price_...
  ```

- or with `PUT /api/billing/plans/{code}` in an operator-managed deployment (`Billing:AllowPlanEditing=true`).

Plans without a price ID for an interval show "Not available for online purchase yet" for that interval.

## 2. Customer portal

In **Settings → Billing → Customer portal**, enable: update payment methods, view invoices, cancel subscriptions (at
period end), and switching between your plan prices (optional; in-app plan changes also work).

## 3. Webhook endpoint (one per deployment)

**Developers → Webhooks → Add endpoint**: `https://<deployment-host>/api/billing/webhooks/stripe`, with these events:

- `checkout.session.completed`
- `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`,
  `customer.subscription.paused`, `customer.subscription.resumed`
- `invoice.created`, `invoice.finalized`, `invoice.paid`, `invoice.payment_failed`, `invoice.voided`,
  `invoice.marked_uncollectible`
- `payment_intent.succeeded`, `payment_intent.payment_failed`
- `charge.refunded`

Copy the endpoint's signing secret into `STRIPE_WEBHOOK_SECRET` (`Stripe__WebhookSecret`).

## 4. Keys

`STRIPE_SECRET_KEY` (`Stripe__SecretKey`) must be a secret key or a restricted key with write access to Customers,
Checkout Sessions, Customer portal, Subscriptions and Refunds. Store it in the secret manager, never in source control.

## 5. Test locally

```bash
stripe listen --forward-to localhost:5080/api/billing/webhooks/stripe   # prints a whsec_... secret for local use
stripe trigger customer.subscription.updated
```

Use test card `4242 4242 4242 4242` in Checkout. The billing page shows "Payment processing…" until the
forwarded webhook arrives.

## Several deployments, one Stripe account

That works: every Checkout Session and Subscription carries `metadata.account_reference = Billing:AccountReference`
(and `client_reference_id`). Each deployment ignores events for other references or for customers it does not know.
Each deployment still needs its own webhook endpoint and secret.
