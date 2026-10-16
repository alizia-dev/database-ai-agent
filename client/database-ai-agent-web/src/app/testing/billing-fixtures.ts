import { Entitlements, Plan, PlanCatalog, SubscriptionOverview } from '../core/billing/billing.service';

const limits = { maxDatabaseConnections: 3, maxUsers: 15, maxConversationsPerDay: 200, maxApprovedTools: 100, maxToolExecutionsPerMonth: 100000 };

export function plan(overrides: Partial<Plan> = {}): Plan {
  return {
    code: 'professional',
    name: 'Professional',
    description: 'For growing teams.',
    isFree: false,
    isCustomPricing: false,
    currency: 'USD',
    monthlyPrice: 149,
    annualPrice: 1430,
    annualSavingsPercent: 20,
    limits,
    features: ['production_access'],
    highlights: ['Up to 3 database connections'],
    checkoutProviders: ['Stripe'],
    ...overrides,
  };
}

export function catalog(): PlanCatalog {
  return {
    plans: [
      plan({ code: 'free', name: 'Free / Demo', isFree: true, monthlyPrice: 0, annualPrice: 0, annualSavingsPercent: null, checkoutProviders: [] }),
      plan(),
      plan({ code: 'enterprise', name: 'Enterprise', isCustomPricing: true, monthlyPrice: null, annualPrice: null, annualSavingsPercent: null, checkoutProviders: [] }),
    ],
    trialDays: 7,
    trialRequiresPaymentMethod: true,
    salesContactEmail: 'sales@example.test',
  };
}

export function entitlements(overrides: Partial<Entitlements> = {}): Entitlements {
  return {
    planCode: 'free',
    planName: 'Free / Demo',
    isFreePlan: true,
    subscriptionStatus: null,
    hasProductionAccess: false,
    limits,
    features: ['demo_environment'],
    trialEndsAt: null,
    currentPeriodEnd: null,
    cancelAtPeriodEnd: false,
    paymentPastDue: false,
    ...overrides,
  };
}

export function overview(overrides: Partial<SubscriptionOverview> = {}): SubscriptionOverview {
  return { entitlements: entitlements(), plan: null, subscription: null, checkoutPending: false, canManagePaymentMethods: false, ...overrides };
}
