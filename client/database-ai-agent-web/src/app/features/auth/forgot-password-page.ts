import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { FormError } from '../../shared/form-error';

@Component({
  selector: 'app-forgot-password-page',
  imports: [ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatProgressBarModule, FormError],
  template: `
    <div class="auth-page">
      <mat-card class="auth-card" appearance="outlined">
        @if (busy()) {
          <mat-progress-bar mode="indeterminate" />
        }
        <mat-card-header>
          <mat-card-title>Reset your password</mat-card-title>
          <mat-card-subtitle>We'll email you a link to choose a new password.</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          @if (sent()) {
            <p class="notice" role="status">{{ sent() }}</p>
          } @else {
            @if (problem(); as p) {
              <p class="notice error" role="alert">{{ p.message }}</p>
            }
            <form [formGroup]="form" (ngSubmit)="submit()" class="form-stack" novalidate>
              <mat-form-field appearance="outline">
                <mat-label>Email</mat-label>
                <input matInput type="email" formControlName="email" autocomplete="email" required />
                <mat-error><app-form-error [control]="form.controls.email" label="Email" /></mat-error>
              </mat-form-field>
              <button mat-flat-button type="submit" [disabled]="busy()">Send reset link</button>
            </form>
          }
        </mat-card-content>
        <mat-card-actions class="auth-links">
          <a mat-button routerLink="/login">Back to sign in</a>
        </mat-card-actions>
      </mat-card>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ForgotPasswordPage {
  private readonly auth = inject(AuthService);

  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', [Validators.required, Validators.email]] });
  protected readonly busy = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly sent = signal<string | null>(null);

  async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.problem.set(null);
    try {
      this.sent.set((await this.auth.forgotPassword(this.form.controls.email.value)).message);
    } catch (error) {
      this.problem.set(toApiProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
