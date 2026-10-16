import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { catalog } from '../../testing/billing-fixtures';
import { PricingPage } from './pricing-page';

describe('PricingPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'pricing', component: PricingPage }]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create('/pricing');
    http.expectOne('/api/billing/plans').flush(catalog());
    await harness.fixture.whenStable();
    harness.detectChanges();
  });

  afterEach(() => http.verify());

  const text = () => (harness.routeNativeElement as HTMLElement).textContent ?? '';

  it('renders the plans returned by the API, including trial and sales contact', () => {
    expect(text()).toContain('Professional');
    expect(text()).toContain('$149');
    expect(text()).toContain('7-day free trial');
    expect(text()).toContain('Contact sales');
    expect(text()).toContain('Pay with card (Visa, Mastercard, Amex and more)');
  });

  it('switches to annual prices and shows the saving', async () => {
    const yearly = Array.from((harness.routeNativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.includes('Yearly'))!;
    yearly.click();
    await harness.fixture.whenStable();
    harness.detectChanges();

    expect(text()).toContain('$1,430');
    expect(text()).toContain('Save 20% with annual billing');
  });
});
