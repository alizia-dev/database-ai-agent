import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { BillingService, Entitlements } from '../../core/billing/billing.service';
import { DatabaseConnection, DatabaseService } from '../../core/database/database.service';
import { toApiProblem } from '../../core/http/api-problem';

const PROVIDER_NAMES: Record<string, string> = { SqlServer: 'SQL Server', PostgreSql: 'PostgreSQL', MySql: 'MySQL' };

@Component({
  selector: 'app-database-page',
  imports: [DatePipe, RouterLink, MatCardModule, MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    <div class="header">
      <div>
        <h1 class="page-title">Databases</h1>
        <p class="muted">Connections to your existing databases. Your data stays where it is; only metadata is read.</p>
      </div>
      @if (canManage() && entitlements()?.hasProductionAccess) {
        <a mat-flat-button routerLink="/app/database/connect" [disabled]="atLimit()">Connect a database</a>
      }
    </div>

    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="notice error" role="alert">{{ error() }}</p>
    }

    @if (entitlements(); as e) {
      @if (!e.hasProductionAccess) {
        <p class="notice" role="status">
          Your current plan includes the demo only. Choose a plan to connect your own database.
          @if (auth.hasPermission('billing.manage')) {
            <a mat-button routerLink="/app/billing">View plans</a>
          }
        </p>
      } @else if (atLimit()) {
        <p class="notice" role="status">
          You have reached your plan's limit of {{ e.limits.maxDatabaseConnections }} database connection(s).
          @if (auth.hasPermission('billing.manage')) {
            <a mat-button routerLink="/app/billing">Upgrade</a>
          }
        </p>
      }
    }

    @for (c of connections(); track c.id) {
      <mat-card appearance="outlined" class="connection">
        <mat-card-header>
          <mat-icon matCardAvatar [class]="'status ' + c.status" aria-hidden="true">{{ c.status === 'Connected' ? 'check_circle' : c.status === 'Failed' ? 'error' : 'help' }}</mat-icon>
          <mat-card-title>{{ c.name }}</mat-card-title>
          <mat-card-subtitle>{{ providerName(c.provider) }} · {{ c.host }}:{{ c.port }} / {{ c.databaseName }}</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <dl class="facts">
            <dt>Status</dt>
            <dd>{{ c.status }}@if (c.lastTestedAt) { — tested {{ c.lastTestedAt | date: 'medium' }} }</dd>
            @if (c.serverVersion) {
              <dt>Server</dt>
              <dd>{{ c.serverVersion }}</dd>
            }
            @if (c.status === 'Connected') {
              <dt>Executable procedures</dt>
              <dd>{{ c.permissions.executableProcedureCount }}</dd>
            }
          </dl>
          @if (c.lastFailureMessage) {
            <p class="notice error">{{ c.lastFailureMessage }}</p>
          }
          @for (w of c.warnings; track w) {
            <p class="warning"><mat-icon aria-hidden="true">warning</mat-icon><span>{{ w }}</span></p>
          }
        </mat-card-content>
        @if (canManage()) {
          <mat-card-actions class="actions">
            <button mat-stroked-button type="button" (click)="retest(c)" [disabled]="busy() !== null">Test connection</button>
            <a mat-button [routerLink]="['/app/database/connect']" [queryParams]="{ id: c.id }">Edit</a>
            <a mat-button [routerLink]="['/app/database/schema']" [queryParams]="{ connection: c.id }">Browse schema</a>
            @if (confirmingDelete() === c.id) {
              <span class="confirm">
                Remove this connection? Your database itself is not changed.
                <button mat-flat-button type="button" class="danger" (click)="remove(c)" [disabled]="busy() !== null">Remove</button>
                <button mat-button type="button" (click)="confirmingDelete.set(null)">Keep</button>
              </span>
            } @else {
              <button mat-button type="button" (click)="confirmingDelete.set(c.id)">Remove</button>
            }
          </mat-card-actions>
        }
      </mat-card>
    } @empty {
      @if (!loading()) {
        <mat-card appearance="outlined" class="empty">
          <mat-card-content>
            <p>No databases are connected yet.</p>
            <p class="muted">
              Supported: SQL Server, PostgreSQL and MySQL. We recommend a dedicated account that can only execute the
              stored procedures you approve; the connect page shows how to create one.
            </p>
          </mat-card-content>
        </mat-card>
      }
    }
  `,
  styles: `
    .header { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: flex-start; gap: 16px; margin-bottom: 16px; }
    .connection, .empty { margin-bottom: 16px; }
    .status.Connected { color: var(--mat-sys-primary); }
    .status.Failed { color: var(--mat-sys-error); }
    .warning { display: flex; gap: 8px; align-items: flex-start; margin: 4px 0; font: var(--mat-sys-body-medium); }
    .warning mat-icon { color: var(--mat-sys-tertiary); font-size: 20px; width: 20px; height: 20px; flex-shrink: 0; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; padding: 0 16px 16px; }
    .confirm { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 8px; }
    .danger { --mat-button-filled-container-color: var(--mat-sys-error); --mat-button-filled-label-text-color: var(--mat-sys-on-error); }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabasePage implements OnInit {
  private readonly database = inject(DatabaseService);
  private readonly billing = inject(BillingService);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly auth = inject(AuthService);

  protected readonly connections = signal<DatabaseConnection[]>([]);
  protected readonly entitlements = signal<Entitlements | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal<string | null>(null);
  protected readonly confirmingDelete = signal<string | null>(null);

  protected readonly canManage = computed(() => this.auth.hasPermission('database.manage'));
  protected readonly atLimit = computed(() => {
    const max = this.entitlements()?.limits.maxDatabaseConnections;
    return max !== null && max !== undefined && this.connections().length >= max;
  });

  async ngOnInit(): Promise<void> {
    try {
      const [connections, entitlements] = await Promise.all([this.database.list(), this.billing.entitlements()]);
      this.connections.set(connections);
      this.entitlements.set(entitlements);
    } catch (error) {
      this.error.set(toApiProblem(error).message);
    } finally {
      this.loading.set(false);
    }
  }

  protected providerName(kind: string): string {
    return PROVIDER_NAMES[kind] ?? kind;
  }

  async retest(connection: DatabaseConnection): Promise<void> {
    this.busy.set(connection.id);
    try {
      const result = await this.database.retest(connection.id);
      this.connections.update((list) => list.map((c) => (c.id === connection.id ? result.connection : c)));
      this.notify(result.test.message);
    } catch (error) {
      this.notify(toApiProblem(error).message);
    } finally {
      this.busy.set(null);
    }
  }

  async remove(connection: DatabaseConnection): Promise<void> {
    this.busy.set(connection.id);
    try {
      await this.database.delete(connection.id);
      this.connections.update((list) => list.filter((c) => c.id !== connection.id));
      this.confirmingDelete.set(null);
      this.notify(`${connection.name} was removed.`);
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
