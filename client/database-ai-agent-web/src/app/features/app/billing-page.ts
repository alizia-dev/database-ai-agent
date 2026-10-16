import { CurrencyPipe, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import {
  BillingHistory,
  BillingInterval,
  BillingService,
  PaymentProvider,
  Plan,
  SubscriptionOverview,
  isProviderUrl,
} from '../../core/billing/billing.service';
import { toApiProblem } from '../../core/http/api-problem';
import { PlanCard } from '../../shared/plan-card';

const POLL_INTERVAL_MS = 3_000;
const POLL_TIMEOUT_MS = 120_000;

const STATUS_LABELS: Record<string, string> = {
  Incomplete: 'Awaiting payment',
  Trialing: 'Free trial',
  Active: 'Active',
  PastDue: 'Payment failed',
  Paused: 'Paused',
  Unpaid: 'Unpaid',
  Canceled: 'Canceled',
  IncompleteExpired: 'Expired',
};

@Component({
  selector: 'app-billing-page',
  imports: [CurrencyPipe, DatePipe, MatCardModule, MatButtonModule, MatButtonToggleModule, MatProgressBarModule, PlanCard],
  templateUrl: './billing-page.html',
  styleUrl: './billing-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BillingPage implements OnInit {
  private readonly billing = inject(BillingService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthService);

  protected readonly overview = signal<SubscriptionOverview | null>(null);
  protected readonly history = signal<BillingHistory | null>(null);
  protected readonly plans = signal<Plan[]>([]);
  protected readonly interval = signal<BillingInterval>('Monthly');
  protected readonly loading = signal(true);
  protected readonly busy = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly processing = signal(false);
  protected readonly processingTimedOut = signal(false);
  protected readonly confirmingCancel = signal(false);

  protected readonly canManage = computed(() => this.auth.hasPermission('billing.manage'));
  protected readonly subscription = computed(() => this.overview()?.subscription ?? null);
  protected readonly hasLiveSubscription = computed(() => {
    const status = this.subscription()?.status;
    return !!status && status !== 'Canceled' && status !== 'IncompleteExpired' && status !== 'Incomplete';
  });
  protected readonly currentPlanCode = computed(() => this.overview()?.entitlements.planCode ?? null);
  protected readonly purchasablePlans = computed(() => this.plans().filter((p) => !p.isFree));
  protected readonly statusLabel = computed(() => {
    const status = this.subscription()?.status;
    return status ? STATUS_LABELS[status] ?? status : 'No subscription';
  });

  private pollTimer: ReturnType<typeof setTimeout> | undefined;
  private pollStartedAt = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.pollTimer));
  }

  async ngOnInit(): Promise<void> {
    const query = this.route.snapshot.queryParamMap;
    if (query.get('interval') === 'Annual') {
      this.interval.set('Annual');
    }

    await this.load();

    if (query.get('checkout') === 'canceled') {
      this.notify('Checkout was canceled. You have not been charged.');
    }
    if (query.get('checkout') === 'success' || query.get('change') === 'success' || this.overview()?.checkoutPending) {
      this.startPolling();
    }
  }

  protected planAction(plan: Plan): 'current' | 'checkout' | 'change' | 'contact' | 'unavailable' {
    if (plan.isCustomPricing) {
      return 'contact';
    }
    const current = this.subscription();
    if (this.hasLiveSubscription() && plan.code === this.currentPlanCode() && current?.interval === this.interval()) {
      return 'current';
    }
    if (this.hasLiveSubscription()) {
      return 'change';
    }
    return plan.checkoutProviders.length > 0 ? 'checkout' : 'unavailable';
  }

  protected providerLabel(provider: PaymentProvider): string {
    return provider === 'Stripe' ? 'Pay with card (Visa, Mastercard, Amex…)' : 'Pay with PayPal';
  }

  async checkout(plan: Plan, provider: PaymentProvider): Promise<void> {
    await this.run(`checkout:${plan.code}:${provider}`, async () => this.redirect(await this.billing.checkout(plan.code, this.interval(), provider)));
  }

  async changePlan(plan: Plan): Promise<void> {
    await this.run(`change:${plan.code}`, async () => {
      const result = await this.billing.changePlan(plan.code, this.interval());
      if (result.approvalUrl) {
        this.redirect(result.approvalUrl);
        return;
      }
      this.notify(`Switching to ${plan.name}. This page updates when the payment provider confirms.`);
      this.startPolling();
    });
  }

  async openPortal(): Promise<void> {
    await this.run('portal', async () => this.redirect(await this.billing.portal()));
  }

  async cancel(): Promise<void> {
    await this.run('cancel', async () => {
      await this.billing.cancel(true);
      this.confirmingCancel.set(false);
      this.notify('Cancellation requested. You keep access until the end of the current billing period.');
      this.startPolling();
    });
  }

  async resume(): Promise<void> {
    await this.run('resume', async () => {
      await this.billing.resume();
      this.notify('Your subscription will continue.');
      this.startPolling();
    });
  }

  private async load(): Promise<void> {
    this.error.set(null);
    try {
      const [overview, history, catalog] = await Promise.all([this.billing.overview(), this.billing.history(), this.billing.plans()]);
      this.overview.set(overview);
      this.history.set(history);
      this.plans.set(catalog.plans);
      if (overview.subscription?.interval && !this.route.snapshot.queryParamMap.has('interval')) {
        this.interval.set(overview.subscription.interval);
      }
    } catch (error) {
      this.error.set(toApiProblem(error).message);
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * After checkout the subscription is activated by the provider's webhook, not by the redirect. Poll until the
   * server reports the change (or give up after two minutes and explain).
   */
  private startPolling(): void {
    clearTimeout(this.pollTimer);
    this.processing.set(true);
    this.processingTimedOut.set(false);
    this.pollStartedAt = Date.now();
    const before = JSON.stringify(this.overview()?.subscription ?? null);

    const tick = async () => {
      await this.load();
      const overview = this.overview();
      const changed = JSON.stringify(overview?.subscription ?? null) !== before;
      if (overview && !overview.checkoutPending && (changed || this.hasLiveSubscription())) {
        this.processing.set(false);
        await this.auth.reloadUser().catch(() => undefined);
        void this.router.navigate([], { queryParams: {}, replaceUrl: true });
        return;
      }
      if (Date.now() - this.pollStartedAt > POLL_TIMEOUT_MS) {
        this.processing.set(false);
        this.processingTimedOut.set(true);
        return;
      }
      this.pollTimer = setTimeout(() => void tick(), POLL_INTERVAL_MS);
    };

    this.pollTimer = setTimeout(() => void tick(), POLL_INTERVAL_MS);
  }

  private redirect(url: string): void {
    if (!isProviderUrl(url)) {
      this.notify('We could not open the payment page. Please contact support.');
      return;
    }
    window.location.assign(url);
  }

  private async run(key: string, action: () => Promise<void>): Promise<void> {
    this.busy.set(key);
    try {
      await action();
    } catch (error) {
      this.notify(toApiProblem(error).message);
    } finally {
      this.busy.set(null);
    }
  }

  private notify(message: string): void {
    this.snackBar.open(message, 'Dismiss', { duration: 6000 });
  }
}
