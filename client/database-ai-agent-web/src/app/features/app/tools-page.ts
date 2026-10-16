import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { DatabaseConnection, DatabaseService } from '../../core/database/database.service';
import { toApiProblem } from '../../core/http/api-problem';
import { STATUS_LABELS, ToolStatus, ToolSummary, ToolsService, riskTone } from '../../core/tools/tools.service';

@Component({
  selector: 'app-tools-page',
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule],
  template: `
    <h1 class="page-title">AI tools</h1>
    <p class="muted">
      Tools let the assistant run approved stored procedures. They are drafted automatically after each schema analysis and stay
      off until someone reviews and approves them.
    </p>

    <div class="toolbar">
      <mat-form-field appearance="outline">
        <mat-label>Database</mat-label>
        <mat-select [(ngModel)]="connectionId" (selectionChange)="load()">
          <mat-option [value]="null">All databases</mat-option>
          @for (c of connections(); track c.id) {
            <mat-option [value]="c.id">{{ c.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Status</mat-label>
        <mat-select [(ngModel)]="status" (selectionChange)="load()">
          <mat-option [value]="null">Any status</mat-option>
          @for (s of statuses; track s) {
            <mat-option [value]="s">{{ statusLabels[s] }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Search</mat-label>
        <input matInput [(ngModel)]="search" (keyup.enter)="load()" />
      </mat-form-field>
      @if (canManage() && connectionId) {
        <button mat-stroked-button type="button" (click)="generate()"><mat-icon>auto_fix_high</mat-icon>Regenerate drafts</button>
      }
    </div>

    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p class="notice error" role="alert">{{ error() }}</p>
    }
    @if (!loading() && tools().length === 0 && !error()) {
      <p class="notice">
        No tools yet. Tools are created from stored procedures when a database is analyzed.
        <a routerLink="/app/database/schema">Open the schema browser</a>.
      </p>
    }

    @if (pendingCount() > 0) {
      <p class="notice" role="status"><mat-icon aria-hidden="true">rate_review</mat-icon> {{ pendingCount() }} tool(s) need review.</p>
    }

    <ul class="tool-list">
      @for (t of tools(); track t.id) {
        <li>
          <a [routerLink]="['/app/tools', t.id]" class="tool">
            <div class="head">
              <code>{{ t.name }}</code>
              <span class="badge" [attr.data-status]="t.status">{{ statusLabels[t.status] }}</span>
              <span class="badge" [attr.data-tone]="tone(t.risk)">{{ t.operation }} · {{ t.risk }} risk</span>
              @if (t.classificationSource === 'Unverified') {
                <span class="badge" data-tone="warn">Unverified</span>
              }
              @if (!t.supported) {
                <span class="badge" data-tone="warn">Unsupported parameters</span>
              }
            </div>
            <div class="muted">{{ t.procedureName }} · {{ t.parameterCount }} parameter(s)</div>
            <div class="description">{{ t.description }}</div>
          </a>
        </li>
      }
    </ul>
  `,
  styleUrl: './tools-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ToolsPage implements OnInit {
  private readonly toolsApi = inject(ToolsService);
  private readonly database = inject(DatabaseService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly auth = inject(AuthService);

  protected readonly tools = signal<ToolSummary[]>([]);
  protected readonly connections = signal<DatabaseConnection[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly canManage = computed(() => this.auth.hasPermission('tools.manage'));
  protected readonly pendingCount = computed(() => this.tools().filter((t) => t.status === 'PendingReview').length);

  protected readonly statuses: ToolStatus[] = ['PendingReview', 'Enabled', 'Disabled', 'Rejected'];
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly tone = riskTone;

  protected connectionId: string | null = null;
  protected status: ToolStatus | null = null;
  protected search = '';

  async ngOnInit(): Promise<void> {
    if (this.auth.hasPermission('database.view')) {
      this.database.list().then((c) => this.connections.set(c), () => undefined);
    }
    await this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.tools.set(await this.toolsApi.list({ connectionId: this.connectionId, status: this.status, search: this.search }));
    } catch (error) {
      this.error.set(toApiProblem(error).message);
    } finally {
      this.loading.set(false);
    }
  }

  async generate(): Promise<void> {
    if (!this.connectionId) {
      return;
    }
    try {
      const report = await this.toolsApi.generate(this.connectionId);
      this.snackBar.open(`${report.created} new, ${report.updated} updated, ${report.disabledChanged + report.disabledUnavailable} disabled.`, 'Dismiss', { duration: 5000 });
      await this.load();
    } catch (error) {
      this.snackBar.open(toApiProblem(error).message, 'Dismiss', { duration: 6000 });
    }
  }
}
