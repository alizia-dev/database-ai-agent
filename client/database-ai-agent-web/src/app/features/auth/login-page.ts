import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { safeReturnUrl } from '../../core/auth/auth.guards';
import { AuthService } from '../../core/auth/auth.service';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { FormError } from '../../shared/form-error';

@Component({
  selector: 'app-login-page',
  imports: [ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatProgressBarModule, FormError],
  template: `
    <div class="auth-page">
      <mat-card class="auth-card" appearance="outlined">
        @if (busy()) {
          <mat-progress-bar mode="indeterminate" />
        }
        <mat-card-header>
          <mat-card-title>Sign in</mat-card-title>
          <mat-card-subtitle>Welcome back to Database AI Agent</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          @if (sessionExpired) {
            <p class="notice" role="status">Your session ended. Please sign in again.</p>
          }
          @if (problem(); as p) {
            <p class="notice error" role="alert">{{ p.message }}</p>
            @if (p.code === 'auth.email_not_verified') {
              <button mat-button type="button" (click)="resendVerification()" [disabled]="busy()">Send a new verification link</button>
            }
          }
          @if (info()) {
            <p class="notice" role="status">{{ info() }}</p>
          }
          <form [formGroup]="form" (ngSubmit)="submit()" class="form-stack" novalidate>
            <mat-form-field appearance="outline">
              <mat-label>Email</mat-label>
              <input matInput type="email" formControlName="email" autocomplete="username" required />
              <mat-error><app-form-error [control]="form.controls.email" label="Email" /></mat-error>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Password</mat-label>
              <input matInput type="password" formControlName="password" autocomplete="current-password" required />
              <mat-error><app-form-error [control]="form.controls.password" label="Password" /></mat-error>
            </mat-form-field>
            <button mat-flat-button type="submit" [disabled]="busy()">Sign in</button>
          </form>
        </mat-card-content>
        <mat-card-actions class="auth-links">
          <a mat-button routerLink="/forgot-password">Forgot password?</a>
          <a mat-button routerLink="/register">New workspace? Create the owner account</a>
        </mat-card-actions>
      </mat-card>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });
  protected readonly busy = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly info = signal<string | null>(null);
  protected readonly sessionExpired = this.route.snapshot.queryParamMap.has('expired');

  async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.problem.set(null);
    this.info.set(null);
    try {
      const { email, password } = this.form.getRawValue();
      await this.auth.login(email, password);
      await this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
    } catch (error) {
      this.problem.set(toApiProblem(error));
    } finally {
      this.busy.set(false);
    }
  }

  async resendVerification(): Promise<void> {
    this.busy.set(true);
    try {
      const response = await this.auth.resendVerification(this.form.controls.email.value);
      this.problem.set(null);
      this.info.set(response.message);
    } catch (error) {
      this.problem.set(toApiProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
