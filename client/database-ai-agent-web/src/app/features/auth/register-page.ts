import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { ApiProblem, applyFieldErrors, toApiProblem } from '../../core/http/api-problem';
import { FormError } from '../../shared/form-error';
import { PASSWORD_HINT, passwordValidators } from '../../shared/password-rules';

@Component({
  selector: 'app-register-page',
  imports: [ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatProgressBarModule, FormError],
  template: `
    <div class="auth-page">
      <mat-card class="auth-card" appearance="outlined">
        @if (busy()) {
          <mat-progress-bar mode="indeterminate" />
        }
        <mat-card-header>
          <mat-card-title>Create your account</mat-card-title>
          <mat-card-subtitle>The first account becomes the workspace Owner. Team members join by invitation.</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          @if (sent()) {
            <p class="notice" role="status">{{ sent() }}</p>
            <a mat-flat-button routerLink="/login">Go to sign in</a>
          } @else {
            @if (problem(); as p) {
              <p class="notice error" role="alert">{{ p.message }}</p>
            }
            <form [formGroup]="form" (ngSubmit)="submit()" class="form-stack" novalidate>
              <mat-form-field appearance="outline">
                <mat-label>Your name</mat-label>
                <input matInput formControlName="displayName" autocomplete="name" required />
                <mat-error><app-form-error [control]="form.controls.displayName" label="Name" /></mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Work email</mat-label>
                <input matInput type="email" formControlName="email" autocomplete="email" required />
                <mat-error><app-form-error [control]="form.controls.email" label="Email" /></mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Password</mat-label>
                <input matInput type="password" formControlName="password" autocomplete="new-password" required />
                <mat-hint>{{ passwordHint }}</mat-hint>
                <mat-error><app-form-error [control]="form.controls.password" label="Password" /></mat-error>
              </mat-form-field>
              <button mat-flat-button type="submit" [disabled]="busy()">Create account</button>
            </form>
          }
        </mat-card-content>
        <mat-card-actions class="auth-links">
          <a mat-button routerLink="/login">Already have an account? Sign in</a>
        </mat-card-actions>
      </mat-card>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RegisterPage {
  private readonly auth = inject(AuthService);

  protected readonly passwordHint = PASSWORD_HINT;
  protected readonly form = inject(NonNullableFormBuilder).group({
    displayName: ['', [Validators.required, Validators.maxLength(100)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    password: ['', passwordValidators],
  });
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
      const { displayName, email, password } = this.form.getRawValue();
      this.sent.set((await this.auth.register(displayName, email, password)).message);
    } catch (error) {
      const problem = toApiProblem(error);
      applyFieldErrors(this.form, problem);
      this.problem.set(problem);
    } finally {
      this.busy.set(false);
    }
  }
}
