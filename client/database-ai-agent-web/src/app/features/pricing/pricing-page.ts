import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { BillingInterval, BillingService, PlanCatalog } from '../../core/billing/billing.service';
import { toApiProblem } from '../../core/http/api-problem';
import { PlanCard } from '../../shared/plan-card';

/** Public pricing. Plans and prices come from the API (configured per deployment), never from the client. */
@Component({
  selector: 'app-pricing-page',
  imports: [RouterLink, MatButtonModule, MatButtonToggleModule, MatProgressBarModule, PlanCard],
  template: `
    <section class="pricing">
      <h1>Simple pricing for every stage</h1>
      <p class="muted lead">
        Start with the demo, then connect your own database when you are ready.
        @if (catalog()?.trialDays) {
          Paid plans start with a {{ catalog()!.trialDays }}-day free trial.
        }
      </p>

      <mat-button-toggle-group
        class="interval"
        [value]="interval()"
        (change)="interval.set($event.value)"
        aria-label="Billing period"
      >
        <mat-button-toggle value="Monthly">Monthly</mat-button-toggle>
        <mat-button-toggle value="Annual">Yearly (save ~20%)</mat-button-toggle>
      </mat-button-toggle-group>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p class="notice error" role="alert">{{ error() }}</p>
      }

      <div class="plans">
        @for (plan of catalog()?.plans ?? []; track plan.code) {
          <app-plan-card [plan]="plan" [interval]="interval()" [featured]="plan.code === 'professional'">
            @if (plan.isCustomPricing) {
              @if (salesHref()) {
                <a mat-stroked-button [href]="salesHref()">Contact sales</a>
              }
            } @else if (plan.isFree) {
              <a mat-stroked-button [routerLink]="auth.isAuthenticated() ? '/app/dashboard' : '/register'">
                {{ auth.isAuthenticated() ? 'Go to the app' : 'Try the demo' }}
              </a>
            } @else {
              <a mat-flat-button [routerLink]="auth.isAuthenticated() ? '/app/billing' : '/register'" [queryParams]="auth.isAuthenticated() ? { plan: plan.code, interval: interval() } : null">
                {{ auth.isAuthenticated() ? 'Choose ' + plan.name : 'Start free' }}
              </a>
            }
          </app-plan-card>
        }
      </div>

      <p class="muted fine-print">
        Pay with card (Visa, Mastercard, Amex and more) through Stripe, or with PayPal where available. Card details are
        entered on the payment provider's secure page and never stored by Database AI Agent. Prices exclude applicable taxes.
      </p>
    </section>
  `,
  styles: `
    .pricing { max-width: 1240px; margin: 0 auto; padding: 56px 24px; text-align: center; }
    h1 { font: var(--mat-sys-display-small); margin: 0 0 12px; }
    .lead { font: var(--mat-sys-body-large); margin: 0 auto 24px; max-width: 640px; }
    .interval { margin-bottom: 32px; }
    .plans { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; text-align: left; }
    .fine-print { margin-top: 32px; font: var(--mat-sys-body-small); }
    @media (max-width: 599px) { .pricing { padding: 32px 16px; } }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PricingPage implements OnInit {
  private readonly billing = inject(BillingService);
  protected readonly auth = inject(AuthService);

  protected readonly catalog = signal<PlanCatalog | null>(null);
  protected readonly interval = signal<BillingInterval>('Monthly');
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly salesHref = computed(() => {
    const email = this.catalog()?.salesContactEmail;
    return email ? `mailto:${email}?subject=${encodeURIComponent('Database AI Agent Enterprise')}` : null;
  });

  async ngOnInit(): Promise<void> {
    try {
      this.catalog.set(await this.billing.plans());
    } catch (error) {
      this.error.set(toApiProblem(error).message);
    } finally {
      this.loading.set(false);
    }
  }
}
