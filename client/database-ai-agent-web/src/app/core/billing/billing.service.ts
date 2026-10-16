import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type PaymentProvider = 'Stripe' | 'PayPal' | 'Manual';
export type BillingInterval = 'Monthly' | 'Annual';
export type SubscriptionStatus =
  | 'Incomplete'
  | 'Trialing'
  | 'Active'
  | 'PastDue'
  | 'Paused'
  | 'Unpaid'
  | 'Canceled'
  | 'IncompleteExpired';

export interface PlanLimits {
  maxDatabaseConnections: number | null;
  maxUsers: number | null;
  maxConversationsPerDay: number | null;
  maxApprovedTools: number | null;
  maxToolExecutionsPerMonth: number | null;
}

export interface Plan {
  code: string;
  name: string;
  description: string | null;
  isFree: boolean;
  isCustomPricing: boolean;
  currency: string;
  monthlyPrice: number | null;
  annualPrice: number | null;
  annualSavingsPercent: number | null;
  limits: PlanLimits;
  features: string[];
  highlights: string[];
  checkoutProviders: PaymentProvider[];
}

export interface PlanCatalog {
  plans: Plan[];
  trialDays: number;
  trialRequiresPaymentMethod: boolean;
  salesContactEmail: string | null;
}

export interface Entitlements {
  planCode: string;
  planName: string;
  isFreePlan: boolean;
  subscriptionStatus: SubscriptionStatus | null;
  hasProductionAccess: boolean;
  limits: PlanLimits;
  features: string[];
  trialEndsAt: string | null;
  currentPeriodEnd: string | null;
  cancelAtPeriodEnd: boolean;
  paymentPastDue: boolean;
}

export interface SubscriptionInfo {
  provider: PaymentProvider;
  status: SubscriptionStatus;
  interval: BillingInterval;
  currentPeriodEnd: string | null;
  trialEnd: string | null;
  cancelAtPeriodEnd: boolean;
  canceledAt: string | null;
  canResume: boolean;
}

export interface SubscriptionOverview {
  entitlements: Entitlements;
  plan: Plan | null;
  subscription: SubscriptionInfo | null;
  checkoutPending: boolean;
  canManagePaymentMethods: boolean;
}

export interface InvoiceRecord {
  number: string | null;
  status: 'Draft' | 'Open' | 'Paid' | 'Void' | 'Uncollectible';
  currency: string;
  amountDue: number;
  amountPaid: number;
  issuedAt: string;
  paidAt: string | null;
  hostedInvoiceUrl: string | null;
  pdfUrl: string | null;
}

export interface PaymentRecord {
  provider: PaymentProvider;
  status: 'Succeeded' | 'Failed' | 'Refunded' | 'PartiallyRefunded';
  currency: string;
  amount: number;
  amountRefunded: number;
  failureMessage: string | null;
  occurredAt: string;
}

export interface BillingHistory {
  invoices: InvoiceRecord[];
  payments: PaymentRecord[];
}

@Injectable({ providedIn: 'root' })
export class BillingService {
  private readonly http = inject(HttpClient);

  plans(): Promise<PlanCatalog> {
    return firstValueFrom(this.http.get<PlanCatalog>('/api/billing/plans'));
  }

  overview(): Promise<SubscriptionOverview> {
    return firstValueFrom(this.http.get<SubscriptionOverview>('/api/billing/subscription'));
  }

  history(): Promise<BillingHistory> {
    return firstValueFrom(this.http.get<BillingHistory>('/api/billing/history'));
  }

  /** Returns the provider-hosted checkout URL. Card details are only ever entered on the provider's page. */
  async checkout(planCode: string, interval: BillingInterval, provider: PaymentProvider): Promise<string> {
    return (await firstValueFrom(this.http.post<{ url: string }>('/api/billing/checkout', { planCode, interval, provider }))).url;
  }

  async portal(): Promise<string> {
    return (await firstValueFrom(this.http.post<{ url: string }>('/api/billing/portal', {}))).url;
  }

  changePlan(planCode: string, interval: BillingInterval): Promise<{ status: string; approvalUrl: string | null }> {
    return firstValueFrom(this.http.post<{ status: string; approvalUrl: string | null }>('/api/billing/change-plan', { planCode, interval }));
  }

  cancel(atPeriodEnd: boolean): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/billing/cancel', { atPeriodEnd }));
  }

  resume(): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/billing/resume', {}));
  }
}

/** Redirects only to the payment providers' own hosted pages (defence against a tampered API response). */
const ALLOWED_REDIRECT_HOSTS = [
  'checkout.stripe.com',
  'billing.stripe.com',
  'www.paypal.com',
  'www.sandbox.paypal.com',
];

export function isProviderUrl(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'https:' && ALLOWED_REDIRECT_HOSTS.includes(parsed.hostname);
  } catch {
    return false;
  }
}
