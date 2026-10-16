import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ConnectionInput,
  ConnectionTest,
  DatabaseProviderInfo,
  DatabaseProviderKind,
  DatabaseService,
  TLS_MODE_LABELS,
  TlsMode,
} from '../../core/database/database.service';
import { applyFieldErrors, toApiProblem } from '../../core/http/api-problem';
import { FormError } from '../../shared/form-error';

type EntryMode = 'fields' | 'connectionString';

@Component({
  selector: 'app-database-connect-page',
  imports: [
    ReactiveFormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule,
    MatButtonToggleModule, MatProgressBarModule, MatIconModule, MatExpansionModule, FormError,
  ],
  templateUrl: './database-connect-page.html',
  styleUrl: './database-connect-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DatabaseConnectPage implements OnInit {
  private readonly database = inject(DatabaseService);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  private readonly connectionId = inject(ActivatedRoute).snapshot.queryParamMap.get('id');

  protected readonly isEdit = this.connectionId !== null;
  protected readonly tlsModes = Object.entries(TLS_MODE_LABELS) as [TlsMode, string][];
  protected readonly providers = signal<DatabaseProviderInfo[]>([]);
  protected readonly mode = signal<EntryMode>('fields');
  protected readonly busy = signal<'loading' | 'testing' | 'saving' | null>('loading');
  protected readonly test = signal<ConnectionTest | null>(null);
  protected readonly error = signal<string | null>(null);

  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    provider: ['SqlServer' as DatabaseProviderKind, Validators.required],
    host: ['', Validators.maxLength(253)],
    port: this.fieldNumber(),
    database: ['', Validators.maxLength(128)],
    username: ['', Validators.maxLength(128)],
    password: ['', Validators.maxLength(256)],
    tlsMode: ['VerifyFull' as TlsMode],
    connectionString: ['', Validators.maxLength(2048)],
  });

  private readonly providerValue = signal<DatabaseProviderKind>('SqlServer');
  protected readonly selectedProvider = computed(() => this.providers().find((p) => p.kind === this.providerValue()) ?? null);
  protected readonly setupScript = computed(() => {
    const provider = this.selectedProvider();
    if (!provider) {
      return '';
    }
    return provider.leastPrivilegeSetupScript
      .replaceAll('{user}', this.scriptUser())
      .replaceAll('{database}', this.scriptDatabase())
      .replaceAll('{password}', '<choose-a-strong-password>');
  });
  private readonly scriptUser = signal('ai_agent_reader');
  private readonly scriptDatabase = signal('your_database');

  async ngOnInit(): Promise<void> {
    this.form.controls.provider.valueChanges.subscribe((value) => {
      this.providerValue.set(value);
      this.test.set(null);
    });
    this.form.controls.username.valueChanges.subscribe((v) => this.scriptUser.set(v?.trim() || 'ai_agent_reader'));
    this.form.controls.database.valueChanges.subscribe((v) => this.scriptDatabase.set(v?.trim() || 'your_database'));

    try {
      this.providers.set(await this.database.providers());
      if (this.connectionId) {
        const existing = await this.database.get(this.connectionId);
        this.form.patchValue({
          name: existing.name,
          provider: existing.provider,
          host: existing.host,
          port: existing.port,
          database: existing.databaseName,
          tlsMode: existing.tlsMode,
        });
      }
    } catch (error) {
      this.error.set(toApiProblem(error).message);
    } finally {
      this.busy.set(null);
    }
  }

  protected setMode(mode: EntryMode): void {
    this.mode.set(mode);
    this.test.set(null);
  }

  async runTest(): Promise<void> {
    if (!this.validate()) {
      return;
    }
    this.busy.set('testing');
    this.error.set(null);
    try {
      this.test.set(await this.database.test(this.input()));
    } catch (error) {
      this.showProblem(error);
    } finally {
      this.busy.set(null);
    }
  }

  async save(): Promise<void> {
    if (!this.validate()) {
      return;
    }
    this.busy.set('saving');
    this.error.set(null);
    try {
      const saved = this.connectionId
        ? await this.database.update(this.connectionId, this.input())
        : await this.database.create(this.input());
      this.snackBar.open(`${saved.connection.name} is connected.`, 'Dismiss', { duration: 5000 });
      await this.router.navigateByUrl('/app/database');
    } catch (error) {
      this.showProblem(error);
    } finally {
      this.busy.set(null);
    }
  }

  async copyScript(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.setupScript());
      this.snackBar.open('Setup script copied.', 'Dismiss', { duration: 3000 });
    } catch {
      this.snackBar.open('Copy failed; select the script and copy it manually.', 'Dismiss', { duration: 4000 });
    }
  }

  private validate(): boolean {
    const v = this.form.getRawValue();
    const missing: string[] = [];
    if (this.mode() === 'fields') {
      if (!v.host.trim()) missing.push('host');
      if (!v.database.trim()) missing.push('database');
      if (!v.username.trim() && !this.isEdit) missing.push('username');
      if (!v.password && !this.isEdit) missing.push('password');
    } else if (!v.connectionString.trim()) {
      missing.push('connectionString');
    }
    for (const field of missing) {
      this.form.get(field)?.setErrors({ required: true });
      this.form.get(field)?.markAsTouched();
    }
    this.form.controls.name.markAsTouched();
    return this.form.valid && missing.length === 0;
  }

  private input(): ConnectionInput {
    const v = this.form.getRawValue();
    const base: ConnectionInput = { name: v.name.trim(), provider: v.provider, tlsMode: v.tlsMode };
    if (this.mode() === 'connectionString') {
      return { ...base, connectionString: v.connectionString, tlsMode: null };
    }
    return {
      ...base,
      host: v.host.trim(),
      port: v.port ?? null,
      database: v.database.trim(),
      username: v.username.trim() || null,
      password: v.password || null,
    };
  }

  private showProblem(error: unknown): void {
    const problem = toApiProblem(error);
    applyFieldErrors(this.form, problem);
    this.error.set(problem.message);
  }

  private fieldNumber() {
    return inject(NonNullableFormBuilder).control<number | null>(null, [Validators.min(1), Validators.max(65535)]);
  }
}
