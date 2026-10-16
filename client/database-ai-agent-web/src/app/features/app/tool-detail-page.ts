import { DatePipe, JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { toApiProblem } from '../../core/http/api-problem';
import {
  DISABLED_REASON_LABELS,
  STATUS_LABELS,
  ToolDetail,
  ToolExecutionResult,
  ToolParameter,
  ToolsService,
  riskTone,
} from '../../core/tools/tools.service';

const ROLES = ['Owner', 'Admin', 'Manager', 'Analyst', 'Viewer'];

@Component({
  selector: 'app-tool-detail-page',
  imports: [
    DatePipe, JsonPipe, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatIconModule,
    MatInputModule, MatProgressBarModule,
  ],
  templateUrl: './tool-detail-page.html',
  styleUrl: './tools-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ToolDetailPage implements OnInit {
  readonly id = input.required<string>();

  private readonly toolsApi = inject(ToolsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly auth = inject(AuthService);

  protected readonly tool = signal<ToolDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly result = signal<ToolExecutionResult | null>(null);

  protected readonly canManage = computed(() => this.auth.hasPermission('tools.manage'));
  protected readonly canExecute = computed(() => this.auth.hasPermission('tools.execute'));
  protected readonly statusLabels = STATUS_LABELS;
  protected readonly disabledReasons = DISABLED_REASON_LABELS;
  protected readonly tone = riskTone;
  protected readonly roles = ROLES;

  protected acknowledge = false;
  protected confirmReadOnly = false;
  protected rejectionReason = '';
  protected form = { name: '', description: '', timeoutSeconds: 30, maxRows: 500, maxResultKilobytes: 1024, allowedRoles: [] as string[], parameterDescriptions: {} as Record<string, string | null> };
  protected args: Record<string, string | boolean> = {};

  async ngOnInit(): Promise<void> {
    try {
      this.apply(await this.toolsApi.get(this.id()));
    } catch (error) {
      this.error.set(toApiProblem(error).message);
    } finally {
      this.loading.set(false);
    }
  }

  protected approve(): Promise<void> {
    return this.act(() => this.toolsApi.approve(this.id(), this.acknowledge), 'Tool approved. The assistant can now use it.');
  }

  protected disable(): Promise<void> {
    return this.act(() => this.toolsApi.disable(this.id()), 'Tool disabled.');
  }

  protected reject(): Promise<void> {
    return this.act(() => this.toolsApi.reject(this.id(), this.rejectionReason || null), 'Tool rejected.');
  }

  protected reopen(): Promise<void> {
    return this.act(() => this.toolsApi.reopen(this.id()), 'Tool reopened for review.');
  }

  protected markReadOnly(): Promise<void> {
    return this.act(() => this.toolsApi.confirmReadOnly(this.id()), 'Classification confirmed as read-only.');
  }

  protected save(): Promise<void> {
    return this.act(() => this.toolsApi.update(this.id(), { ...this.form }), 'Changes saved.');
  }

  protected toggleRole(role: string, checked: boolean): void {
    this.form.allowedRoles = checked ? [...this.form.allowedRoles, role] : this.form.allowedRoles.filter((r) => r !== role);
  }

  protected async run(confirmed = false): Promise<void> {
    const tool = this.tool();
    if (!tool) {
      return;
    }
    this.busy.set(true);
    try {
      this.result.set(await this.toolsApi.execute(tool.summary.name, this.argumentValues(tool.parameters), confirmed));
      this.tool.set(await this.toolsApi.get(this.id()));
    } catch (error) {
      this.snackBar.open(toApiProblem(error).message, 'Dismiss', { duration: 6000 });
    } finally {
      this.busy.set(false);
    }
  }

  protected inputType(p: ToolParameter): string {
    switch (p.type) {
      case 'Integer':
      case 'Number':
        return 'number';
      case 'Date':
        return 'date';
      case 'DateTime':
        return 'datetime-local';
      case 'Time':
        return 'time';
      default:
        return 'text';
    }
  }

  protected argumentErrors(result: ToolExecutionResult): { name: string; message: string }[] {
    return Object.entries(result.argumentErrors ?? {}).map(([name, messages]) => ({ name, message: messages[0] }));
  }

  protected display(value: unknown): string {
    return value === null || value === undefined ? '—' : typeof value === 'object' ? JSON.stringify(value) : String(value);
  }

  /** Empty inputs are omitted so the procedure's own defaults apply; the server validates every value. */
  private argumentValues(parameters: ToolParameter[]): Record<string, unknown> {
    const values: Record<string, unknown> = {};
    for (const p of parameters) {
      const raw = this.args[p.name];
      if (p.type === 'Boolean') {
        if (raw !== undefined && raw !== '') {
          values[p.name] = raw === true || raw === 'true';
        }
      } else if (typeof raw === 'string' && raw.trim() !== '') {
        values[p.name] = p.type === 'Integer' || p.type === 'Number' ? Number(raw) : raw;
      }
    }
    return values;
  }

  private async act(action: () => Promise<ToolDetail>, message: string): Promise<void> {
    this.busy.set(true);
    try {
      this.apply(await action());
      this.snackBar.open(message, 'Dismiss', { duration: 4000 });
    } catch (error) {
      this.snackBar.open(toApiProblem(error).message, 'Dismiss', { duration: 6000 });
    } finally {
      this.busy.set(false);
    }
  }

  private apply(tool: ToolDetail): void {
    this.tool.set(tool);
    this.acknowledge = false;
    this.confirmReadOnly = false;
    this.form = {
      name: tool.summary.name,
      description: tool.summary.description,
      timeoutSeconds: tool.timeoutSeconds,
      maxRows: tool.maxRows,
      maxResultKilobytes: Math.round(tool.maxResultBytes / 1024),
      allowedRoles: [...tool.allowedRoles],
      parameterDescriptions: Object.fromEntries(tool.parameters.map((p) => [p.name, p.description])),
    };
  }
}
