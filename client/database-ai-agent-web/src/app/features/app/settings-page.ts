import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AuthService } from '../../core/auth/auth.service';
import { applyFieldErrors, toApiProblem } from '../../core/http/api-problem';
import { ResponseStyle, WorkspaceService } from '../../core/workspace/workspace.service';
import { FormError } from '../../shared/form-error';
import { PASSWORD_HINT, matchPasswords, passwordValidators } from '../../shared/password-rules';

const optionalDays = [Validators.min(1), Validators.max(3650)];

@Component({
  selector: 'app-settings-page',
  imports: [ReactiveFormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, FormError],
  templateUrl: './settings-page.html',
  styleUrl: './settings-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsPage implements OnInit {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly workspace = inject(WorkspaceService);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly auth = inject(AuthService);

  protected readonly responseStyles: ResponseStyle[] = ['Concise', 'Balanced', 'Detailed'];
  protected readonly passwordHint = PASSWORD_HINT;
  protected readonly canEditWorkspace = computed(() => this.auth.hasPermission('settings.manage'));
  protected readonly saving = signal<'workspace' | 'profile' | 'password' | null>(null);
  protected readonly loadError = signal<string | null>(null);

  protected readonly workspaceForm = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    industry: ['', Validators.maxLength(100)],
    assistantName: ['', [Validators.required, Validators.maxLength(60)]],
    businessDescription: ['', Validators.maxLength(4000)],
    timeZone: ['', [Validators.required, Validators.maxLength(64)]],
    currency: ['', [Validators.required, Validators.pattern(/^[A-Za-z]{3}$/)]],
    dateFormat: ['', [Validators.required, Validators.maxLength(32)]],
    responseStyle: ['Balanced' as ResponseStyle, Validators.required],
    retention: this.fb.group({
      conversationDays: this.fb.control<number | null>(null, optionalDays),
      auditLogDays: this.fb.control<number | null>(null, optionalDays),
      usageRecordDays: this.fb.control<number | null>(null, optionalDays),
      knowledgeDocumentDays: this.fb.control<number | null>(null, optionalDays),
    }),
  });

  protected readonly profileForm = this.fb.group({
    displayName: [this.auth.user()?.displayName ?? '', [Validators.required, Validators.maxLength(100)]],
  });

  protected readonly passwordForm = this.fb.group(
    { currentPassword: ['', Validators.required], newPassword: ['', passwordValidators], confirm: [''] },
    { validators: matchPasswords('newPassword', 'confirm') },
  );

  async ngOnInit(): Promise<void> {
    try {
      const settings = await this.workspace.load();
      this.workspaceForm.reset({
        ...settings,
        industry: settings.industry ?? '',
        businessDescription: settings.businessDescription ?? '',
      });
    } catch (error) {
      this.loadError.set(toApiProblem(error).message);
    }

    if (!this.canEditWorkspace()) {
      this.workspaceForm.disable();
    }
  }

  async saveWorkspace(): Promise<void> {
    if (this.workspaceForm.invalid) {
      this.workspaceForm.markAllAsTouched();
      return;
    }

    await this.save('workspace', async () => {
      const value = this.workspaceForm.getRawValue();
      await this.workspace.update({
        ...value,
        industry: value.industry || null,
        businessDescription: value.businessDescription || null,
        currency: value.currency.toUpperCase(),
      });
      this.workspaceForm.markAsPristine();
      this.notify('Workspace settings saved.');
    }, () => this.workspaceForm);
  }

  async saveProfile(): Promise<void> {
    if (this.profileForm.invalid) {
      this.profileForm.markAllAsTouched();
      return;
    }

    await this.save('profile', async () => {
      await this.auth.updateProfile(this.profileForm.controls.displayName.value);
      this.profileForm.markAsPristine();
      this.notify('Your profile was updated.');
    }, () => this.profileForm);
  }

  async changePassword(): Promise<void> {
    if (this.passwordForm.invalid) {
      this.passwordForm.markAllAsTouched();
      return;
    }

    await this.save('password', async () => {
      const { currentPassword, newPassword } = this.passwordForm.getRawValue();
      await this.auth.changePassword(currentPassword, newPassword);
      this.passwordForm.reset();
      this.notify('Password changed. Your other sessions were signed out.');
    }, () => this.passwordForm);
  }

  logoutEverywhere(): Promise<void> {
    return this.auth.logoutEverywhere();
  }

  private async save(kind: 'workspace' | 'profile' | 'password', action: () => Promise<void>, form: () => Parameters<typeof applyFieldErrors>[0]): Promise<void> {
    this.saving.set(kind);
    try {
      await action();
    } catch (error) {
      const problem = toApiProblem(error);
      applyFieldErrors(form(), problem);
      this.notify(problem.message);
    } finally {
      this.saving.set(null);
    }
  }

  private notify(message: string): void {
    this.snackBar.open(message, 'Dismiss', { duration: 5000 });
  }
}
