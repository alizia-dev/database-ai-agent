import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { InvitationPreview } from '../../core/auth/auth.models';
import { AuthService } from '../../core/auth/auth.service';
import { ApiProblem, applyFieldErrors, toApiProblem } from '../../core/http/api-problem';
import { FormError } from '../../shared/form-error';
import { PASSWORD_HINT, passwordValidators } from '../../shared/password-rules';

@Component({
  selector: 'app-accept-invite-page',
  imports: [ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatProgressBarModule, FormError],
  template: `
    <div class="auth-page">
      <mat-card class="auth-card" appearance="outlined">
        @if (busy()) {
          <mat-progress-bar mode="indeterminate" />
        }
        <mat-card-header>
          <mat-card-title>Join your team</mat-card-title>
          @if (preview(); as invite) {
            <mat-card-subtitle>{{ invite.workspaceName }} invited {{ invite.email }} as {{ invite.role }}</mat-card-subtitle>
          }
        </mat-card-header>
        <mat-card-content>
          @if (problem(); as p) {
            <p class="notice error" role="alert">{{ p.message }}</p>
          }
          @if (preview()) {
            <form [formGroup]="form" (ngSubmit)="submit()" class="form-stack" novalidate>
              <mat-form-field appearance="outline">
                <mat-label>Your name</mat-label>
                <input matInput formControlName="displayName" autocomplete="name" required />
                <mat-error><app-form-error [control]="form.controls.displayName" label="Name" /></mat-error>
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Choose a password</mat-label>
                <input matInput type="password" formControlName="password" autocomplete="new-password" required />
                <mat-hint>{{ passwordHint }}</mat-hint>
                <mat-error><app-form-error [control]="form.controls.password" label="Password" /></mat-error>
              </mat-form-field>
              <button mat-flat-button type="submit" [disabled]="busy()">Accept invitation</button>
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
export class AcceptInvitePage implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly token = inject(ActivatedRoute).snapshot.queryParamMap.get('token');

  protected readonly passwordHint = PASSWORD_HINT;
  protected readonly form = inject(NonNullableFormBuilder).group({
    displayName: ['', [Validators.required, Validators.maxLength(100)]],
    password: ['', passwordValidators],
  });
  protected readonly busy = signal(true);
  protected readonly preview = signal<InvitationPreview | null>(null);
  protected readonly problem = signal<ApiProblem | null>(null);

  async ngOnInit(): Promise<void> {
    if (!this.token) {
      this.problem.set({ status: 400, message: 'This invitation link is incomplete.', fieldErrors: {} });
      this.busy.set(false);
      return;
    }

    try {
      this.preview.set(await this.auth.previewInvitation(this.token));
    } catch (error) {
      this.problem.set(toApiProblem(error));
    } finally {
      this.busy.set(false);
    }
  }

  async submit(): Promise<void> {
    if (this.form.invalid || !this.token) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.problem.set(null);
    try {
      const { displayName, password } = this.form.getRawValue();
      await this.auth.acceptInvitation(this.token, displayName, password);
      await this.router.navigateByUrl('/app/dashboard');
    } catch (error) {
      const problem = toApiProblem(error);
      applyFieldErrors(this.form, problem);
      this.problem.set(problem);
    } finally {
      this.busy.set(false);
    }
  }
}
