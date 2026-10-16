import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthService } from '../../core/auth/auth.service';
import { isProviderUrl } from '../../core/billing/billing.service';
import { catalog, entitlements, overview, plan } from '../../testing/billing-fixtures';
import { BillingPage } from './billing-page';

describe('BillingPage', () => {
  let http: HttpTestingController;

  async function open(url: string, permissions: string[] = ['billing.view', 'billing.manage']): Promise<RouterTestingHarness> {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'app/billing', component: BillingPage }]), provideHttpClient(), provideHttpClientTesting()],
    });
    vi.spyOn(TestBed.inject(AuthService), 'hasPermission').mockImplementation((p) => permissions.includes(p));
    vi.spyOn(TestBed.inject(AuthService), 'reloadUser').mockResolvedValue();
    http = TestBed.inject(HttpTestingController);
    return RouterTestingHarness.create(url);
  }

  function respond(o = overview()): void {
    http.expectOne('/api/billing/subscription').flush(o);
    http.expectOne('/api/billing/history').flush({ invoices: [], payments: [] });
    http.expectOne('/api/billing/plans').flush(catalog());
  }

  /** Waits until the page (rendered after async loads) contains the expected text, then returns all text. */
  async function textOnceContains(harness: RouterTestingHarness, expected: string): Promise<string> {
    let text = '';
    await vi.waitFor(() => {
      harness.detectChanges();
      text = (harness.routeNativeElement as HTMLElement).textContent ?? '';
      expect(text).toContain(expected);
    });
    return text;
  }

  afterEach(() => vi.useRealTimers());

  it('offers checkout with card for purchasable plans when there is no subscription', async () => {
    const harness = await open('/app/billing');
    respond();
    const text = await textOnceContains(harness, 'You are using the demo');

    expect(text).toContain('Pay with card (Visa, Mastercard, Amex…)');
    expect(text).toContain('Contact sales for Enterprise pricing.');
  });

  it('shows "Payment processing" after checkout and waits for the webhook-confirmed subscription', async () => {
    vi.useFakeTimers();
    const harness = await open('/app/billing?checkout=success');
    respond(overview({ checkoutPending: true }));
    await textOnceContains(harness, 'Payment processing');

    await vi.advanceTimersByTimeAsync(3_100);
    respond(
      overview({
        entitlements: entitlements({ planCode: 'professional', planName: 'Professional', isFreePlan: false, hasProductionAccess: true, subscriptionStatus: 'Trialing' }),
        plan: plan(),
        subscription: { provider: 'Stripe', status: 'Trialing', interval: 'Monthly', currentPeriodEnd: '2026-11-16T00:00:00Z', trialEnd: '2026-10-23T00:00:00Z', cancelAtPeriodEnd: false, canceledAt: null, canResume: false },
        canManagePaymentMethods: true,
      }),
    );
    const text = await textOnceContains(harness, 'Free trial');
    await vi.waitFor(() => {
      harness.detectChanges();
      expect((harness.routeNativeElement as HTMLElement).textContent).not.toContain('Payment processing');
    });

    expect(text).toContain('Manage payment method');
    expect(text).toContain('Your current plan');
  });

  it('hides purchase and management actions from members without billing.manage', async () => {
    const harness = await open('/app/billing', ['billing.view']);
    respond();
    const text = await textOnceContains(harness, 'Billing history');

    const headings = Array.from((harness.routeNativeElement as HTMLElement).querySelectorAll('h2')).map((h) => h.textContent?.trim());
    expect(headings).not.toContain('Choose a plan');
    expect(text).toContain('Ask a workspace owner to choose a plan');
    expect(text).not.toContain('Pay with card');
  });
});

describe('isProviderUrl', () => {
  it.each([
    ['https://checkout.stripe.com/c/pay/cs_1', true],
    ['https://billing.stripe.com/p/session/1', true],
    ['https://www.sandbox.paypal.com/webapps/billing/subscriptions?ba_token=1', true],
    ['http://checkout.stripe.com/c/pay/cs_1', false],
    ['https://checkout.stripe.com.evil.example/', false],
    ['javascript:alert(1)', false],
  ])('%s -> %s', (url, expected) => {
    expect(isProviderUrl(url)).toBe(expected);
  });
});
