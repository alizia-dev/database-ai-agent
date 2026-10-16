import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { ApiProblem, applyFieldErrors, toApiProblem } from '../../core/http/api-problem';
import { FormError } from '../../shared/form-error';
import { PASSWORD_HINT, matchPasswords, passwordValidators } from '../../shared/password-rules';

@Component({
  selector: 'app-reset-password-page',
  imports: [ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatProgressBarModule, FormError],
  template: `
    <div class="auth-page">
      <mat-card class="auth-card" appearance="outlined">
        @if (busy()) {
          <mat-progress-bar mode="indeterminate" />
        }
        <mat-card-header>
          <mat-card-title>Choose a new password</mat-card-title>
          @if (email) {
            <mat-card-subtitle>For {{ email }}</mat-card-subtitle>
          }
        </mat-card-header>
        <mat-card-content>
          @if (!email || !token) {
            <p class="notice error" role="alert">This reset link is incomplete. Please request a new one.</p>
          } @else if (done()) {
            <p class="notice" role="status">Your password was changed and all other sessions were signed out.</p>
            <a mat-flat-button routerLink="/login">Sign in</a>
          } @else {
            @if (problem(); as p) {
              <p class="notice error" role="alert">{{ p.message }}</p>
            }
            <form [formGroup]="form" (ngSubmit)="submit()" class="form-stack" novalidate>
              <mat-form-field appearance="outline">
                <mat-label>New password</mat-label>
                <input matInput type="password" formControlName="newPassword" autocomplete="new-password" required />
                <mat-hint>{{ passwordHint }}</mat-hint>
                <mat-error><app-form-error [control]="form.controls.newPassword" label="Password" /></mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Confirm new password</mat-label>
                <input matInput type="password" formControlName="confirm" autocomplete="new-password" required />
                <mat-error><app-form-error [control]="form.controls.confirm" label="Confirmation" /></mat-error>
              </mat-form-field>
              <button mat-flat-button type="submit" [disabled]="busy()">Change password</button>
            </form>
          }
        </mat-card-content>
        <mat-card-actions class="auth-links">
          <a mat-button routerLink="/forgot-password">Request a new link</a>
        </mat-card-actions>
      </mat-card>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResetPasswordPage {
  private readonly auth = inject(AuthService);
  private readonly query = inject(ActivatedRoute).snapshot.queryParamMap;

  protected readonly email = this.query.get('email');
  protected readonly token = this.query.get('token');
  protected readonly passwordHint = PASSWORD_HINT;
  protected readonly form = inject(NonNullableFormBuilder).group(
    { newPassword: ['', passwordValidators], confirm: [''] },
    { validators: matchPasswords('newPassword', 'confirm') },
  );
  protected readonly busy = signal(false);
  protected readonly done = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);

  async submit(): Promise<void> {
    if (this.form.invalid || !this.email || !this.token) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.problem.set(null);
    try {
      await this.auth.resetPassword(this.email, this.token, this.form.controls.newPassword.value);
      this.done.set(true);
    } catch (error) {
      const problem = toApiProblem(error);
      applyFieldErrors(this.form, problem);
      this.problem.set(problem);
    } finally {
      this.busy.set(false);
    }
  }
}
