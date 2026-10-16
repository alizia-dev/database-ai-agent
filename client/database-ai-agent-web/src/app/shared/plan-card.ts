import { CurrencyPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { BillingInterval, Plan } from '../core/billing/billing.service';

/** Presentational plan card shared by the pricing and billing pages; actions are projected by the parent. */
@Component({
  selector: 'app-plan-card',
  imports: [CurrencyPipe, MatCardModule, MatIconModule],
  template: `
    <mat-card appearance="outlined" class="plan" [class.current]="current()" [class.featured]="featured()">
      <mat-card-header>
        <mat-card-title>{{ plan().name }}</mat-card-title>
        @if (current()) {
          <mat-card-subtitle class="badge">Current plan</mat-card-subtitle>
        } @else if (featured()) {
          <mat-card-subtitle class="badge">Most popular</mat-card-subtitle>
        }
      </mat-card-header>
      <mat-card-content>
        <div class="price" aria-live="polite">
          @if (plan().isCustomPricing) {
            <span class="amount">Custom</span>
          } @else if (price() === 0) {
            <span class="amount">Free</span>
          } @else if (price() !== null) {
            <span class="amount">{{ price() | currency: plan().currency : 'symbol' : '1.0-0' }}</span>
            <span class="per">/ {{ interval() === 'Annual' ? 'year' : 'month' }}</span>
          }
        </div>
        @if (interval() === 'Annual' && plan().annualSavingsPercent) {
          <div class="savings">Save {{ plan().annualSavingsPercent }}% with annual billing</div>
        }
        @if (plan().description) {
          <p class="description">{{ plan().description }}</p>
        }
        <ul class="highlights">
          @for (item of plan().highlights; track item) {
            <li><mat-icon aria-hidden="true">check</mat-icon><span>{{ item }}</span></li>
          }
        </ul>
      </mat-card-content>
      <mat-card-actions class="actions">
        <ng-content />
      </mat-card-actions>
    </mat-card>
  `,
  styles: `
    .plan { height: 100%; display: flex; flex-direction: column; }
    .plan.featured, .plan.current { border-color: var(--mat-sys-primary); }
    .badge { color: var(--mat-sys-primary); }
    .price { margin: 8px 0 4px; }
    .amount { font: var(--mat-sys-headline-medium); }
    .per { color: var(--mat-sys-on-surface-variant); margin-left: 4px; }
    .savings { color: var(--mat-sys-primary); font: var(--mat-sys-label-large); }
    .description { color: var(--mat-sys-on-surface-variant); }
    .highlights { list-style: none; padding: 0; margin: 12px 0 0; }
    .highlights li { display: flex; gap: 8px; align-items: flex-start; margin-bottom: 6px; font: var(--mat-sys-body-medium); }
    .highlights mat-icon { font-size: 18px; width: 18px; height: 18px; color: var(--mat-sys-primary); }
    mat-card-content { flex: 1; }
    .actions { display: flex; flex-direction: column; align-items: stretch; gap: 8px; padding: 16px; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlanCard {
  readonly plan = input.required<Plan>();
  readonly interval = input<BillingInterval>('Monthly');
  readonly current = input(false);
  readonly featured = input(false);

  protected readonly price = computed(() =>
    this.interval() === 'Annual' ? this.plan().annualPrice : this.plan().monthlyPrice,
  );
}
