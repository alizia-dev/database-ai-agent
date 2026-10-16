import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { PERMISSION_LABELS } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { WorkspaceService } from '../../core/workspace/workspace.service';

@Component({
  selector: 'app-dashboard-page',
  imports: [MatCardModule, MatIconModule, RouterLink],
  template: `
    @if (auth.user(); as user) {
      <h1 class="page-title">Welcome, {{ user.displayName }}</h1>
      <p class="muted">
        You are signed in to <strong>{{ workspace.settings()?.name ?? 'your workspace' }}</strong> as
        <strong>{{ user.role }}</strong>.
      </p>

      <div class="card-grid">
        <mat-card appearance="outlined">
          <mat-card-header>
            <mat-icon matCardAvatar>verified_user</mat-icon>
            <mat-card-title>Your access</mat-card-title>
            <mat-card-subtitle>What your role allows in this workspace</mat-card-subtitle>
          </mat-card-header>
          <mat-card-content>
            <ul class="plain-list">
              @for (label of permissionLabels(); track label) {
                <li>{{ label }}</li>
              } @empty {
                <li>No permissions are assigned to your role yet. Contact your administrator.</li>
              }
            </ul>
          </mat-card-content>
        </mat-card>

        <mat-card appearance="outlined">
          <mat-card-header>
            <mat-icon matCardAvatar>tune</mat-icon>
            <mat-card-title>Workspace</mat-card-title>
            <mat-card-subtitle>Assistant and regional settings</mat-card-subtitle>
          </mat-card-header>
          <mat-card-content>
            @if (workspace.settings(); as s) {
              <dl class="facts">
                <dt>Assistant</dt>
                <dd>{{ s.assistantName }}</dd>
                <dt>Time zone</dt>
                <dd>{{ s.timeZone }}</dd>
                <dt>Currency</dt>
                <dd>{{ s.currency }}</dd>
              </dl>
            }
            <a routerLink="/app/settings">View settings</a>
          </mat-card-content>
        </mat-card>
      </div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardPage {
  protected readonly auth = inject(AuthService);
  protected readonly workspace = inject(WorkspaceService);

  protected readonly permissionLabels = computed(() => (this.auth.user()?.permissions ?? []).map((p) => PERMISSION_LABELS[p]));
}
