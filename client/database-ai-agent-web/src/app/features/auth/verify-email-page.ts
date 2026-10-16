import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { toApiProblem } from '../../core/http/api-problem';

type VerifyState = { kind: 'verifying' } | { kind: 'verified' } | { kind: 'failed'; message: string };

@Component({
  selector: 'app-verify-email-page',
  imports: [RouterLink, MatCardModule, MatButtonModule, MatProgressBarModule],
  template: `
    <div class="auth-page">
      <mat-card class="auth-card" appearance="outlined">
        @if (state().kind === 'verifying') {
          <mat-progress-bar mode="indeterminate" />
        }
        <mat-card-header>
          <mat-card-title>Email verification</mat-card-title>
        </mat-card-header>
        <mat-card-content>
          @switch (state().kind) {
            @case ('verifying') {
              <p role="status">Verifying your email address&hellip;</p>
            }
            @case ('verified') {
              <p class="notice" role="status">Your email address is verified. You can now sign in.</p>
              <a mat-flat-button routerLink="/login">Sign in</a>
            }
            @case ('failed') {
              <p class="notice error" role="alert">{{ failureMessage() }}</p>
              <p>Sign in to request a new verification link.</p>
              <a mat-flat-button routerLink="/login">Go to sign in</a>
            }
          }
        </mat-card-content>
      </mat-card>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VerifyEmailPage implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly query = inject(ActivatedRoute).snapshot.queryParamMap;

  protected readonly state = signal<VerifyState>({ kind: 'verifying' });

  protected failureMessage(): string {
    const state = this.state();
    return state.kind === 'failed' ? state.message : '';
  }

  async ngOnInit(): Promise<void> {
    const userId = this.query.get('userId');
    const token = this.query.get('token');
    if (!userId || !token) {
      this.state.set({ kind: 'failed', message: 'This verification link is incomplete.' });
      return;
    }

    try {
      await this.auth.verifyEmail(userId, token);
      this.state.set({ kind: 'verified' });
    } catch (error) {
      this.state.set({ kind: 'failed', message: toApiProblem(error).message });
    }
  }
}
