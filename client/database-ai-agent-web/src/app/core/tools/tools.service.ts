import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { RoutineBehavior } from '../database/database.service';

export type ToolOperation = 'Read' | 'Write' | 'Delete' | 'Admin';
export type ToolRisk = 'Low' | 'Medium' | 'High' | 'Critical';
export type ClassificationSource = 'DefinitionAnalysis' | 'Declaration' | 'Unverified' | 'AdministratorConfirmed';
export type ToolStatus = 'PendingReview' | 'Enabled' | 'Disabled' | 'Rejected';
export type ToolDisabledReason = 'None' | 'ByAdministrator' | 'ProcedureChanged' | 'ProcedureUnavailable';
export type ToolParameterType = 'String' | 'Integer' | 'Number' | 'Boolean' | 'Date' | 'DateTime' | 'Time' | 'Uuid';
export type ToolOutcome = 'Succeeded' | 'ConfirmationRequired' | 'Denied' | 'InvalidArguments' | 'Failed' | 'TimedOut';

export interface ToolSummary {
  id: string;
  connectionId: string;
  name: string;
  description: string;
  operation: ToolOperation;
  risk: ToolRisk;
  classificationSource: ClassificationSource;
  status: ToolStatus;
  disabledReason: ToolDisabledReason;
  procedureName: string;
  parameterCount: number;
  supported: boolean;
  approvedAt: string | null;
}

export interface ToolParameter {
  name: string;
  sourceName: string;
  ordinal: number;
  type: ToolParameterType;
  sourceDataType: string;
  required: boolean;
  maxLength: number | null;
  minimum: number | null;
  maximum: number | null;
  description: string | null;
}

export interface ToolExecutionRecord {
  id: string;
  status: 'Succeeded' | 'Denied' | 'Failed' | 'TimedOut' | 'ConfirmationRequired';
  reasonCode: string | null;
  startedAt: string;
  durationMs: number;
  rowCount: number;
  truncated: boolean;
  confirmed: boolean;
}

export interface ToolDetail {
  summary: ToolSummary;
  classificationReasons: string[];
  parameters: ToolParameter[];
  parametersSchema: Record<string, unknown>;
  allowedRoles: string[];
  timeoutSeconds: number;
  maxRows: number;
  maxResultBytes: number;
  requiresConfirmation: boolean;
  unsupportedReason: string | null;
  rejectionReason: string | null;
  canApprove: boolean;
  approvalBlockedReason: string | null;
  canConfirmReadOnly: boolean;
  procedure: {
    schemaName: string;
    name: string;
    kind: string;
    description: string | null;
    behavior: RoutineBehavior;
    canExecute: boolean;
    isMissing: boolean;
  } | null;
  recentExecutions: ToolExecutionRecord[];
}

export interface UpdateTool {
  name: string;
  description: string;
  parameterDescriptions: Record<string, string | null>;
  timeoutSeconds: number;
  maxRows: number;
  maxResultKilobytes: number;
  allowedRoles: string[];
}

export interface ToolExecutionResult {
  outcome: ToolOutcome;
  message: string;
  reasonCode: string | null;
  executionId: string | null;
  toolName: string | null;
  columns: string[];
  rows: unknown[][];
  outputValues: Record<string, unknown>;
  truncated: boolean;
  retrievedAt: string | null;
  durationMs: number;
  confirmationPrompt: string | null;
  argumentErrors: Record<string, string[]> | null;
}

export interface ToolSyncReport {
  created: number;
  updated: number;
  disabledChanged: number;
  disabledUnavailable: number;
  unchanged: number;
}

export const STATUS_LABELS: Record<ToolStatus, string> = {
  PendingReview: 'Needs review',
  Enabled: 'Enabled',
  Disabled: 'Disabled',
  Rejected: 'Rejected',
};

export const DISABLED_REASON_LABELS: Record<ToolDisabledReason, string> = {
  None: '',
  ByAdministrator: 'Disabled by an administrator',
  ProcedureChanged: 'The procedure changed since it was approved. Review it again.',
  ProcedureUnavailable: 'The procedure no longer exists or cannot be executed by the connection account.',
};

export function riskTone(risk: ToolRisk): 'ok' | 'warn' | 'risk' {
  return risk === 'Low' ? 'ok' : risk === 'Medium' ? 'warn' : 'risk';
}

/** AI tools generated from stored procedures: review, approval and test runs. */
@Injectable({ providedIn: 'root' })
export class ToolsService {
  private readonly http = inject(HttpClient);

  list(query: { connectionId?: string | null; status?: ToolStatus | null; search?: string | null } = {}): Promise<ToolSummary[]> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value) {
        params = params.set(key, value);
      }
    }
    return firstValueFrom(this.http.get<ToolSummary[]>('/api/tools', { params }));
  }

  get(id: string): Promise<ToolDetail> {
    return firstValueFrom(this.http.get<ToolDetail>(`/api/tools/${encodeURIComponent(id)}`));
  }

  update(id: string, input: UpdateTool): Promise<ToolDetail> {
    return firstValueFrom(this.http.put<ToolDetail>(`/api/tools/${encodeURIComponent(id)}`, input));
  }

  approve(id: string, acknowledgeDataChanges: boolean): Promise<ToolDetail> {
    return firstValueFrom(this.http.post<ToolDetail>(`/api/tools/${encodeURIComponent(id)}/approve`, { acknowledgeDataChanges }));
  }

  disable(id: string): Promise<ToolDetail> {
    return firstValueFrom(this.http.post<ToolDetail>(`/api/tools/${encodeURIComponent(id)}/disable`, {}));
  }

  reject(id: string, reason: string | null): Promise<ToolDetail> {
    return firstValueFrom(this.http.post<ToolDetail>(`/api/tools/${encodeURIComponent(id)}/reject`, { reason }));
  }

  reopen(id: string): Promise<ToolDetail> {
    return firstValueFrom(this.http.post<ToolDetail>(`/api/tools/${encodeURIComponent(id)}/reopen`, {}));
  }

  confirmReadOnly(id: string): Promise<ToolDetail> {
    return firstValueFrom(this.http.post<ToolDetail>(`/api/tools/${encodeURIComponent(id)}/confirm-read-only`, { confirmation: true }));
  }

  generate(connectionId: string): Promise<ToolSyncReport> {
    return firstValueFrom(this.http.post<ToolSyncReport>(`/api/database/connections/${encodeURIComponent(connectionId)}/tools/generate`, {}));
  }

  execute(name: string, args: Record<string, unknown>, confirmed = false): Promise<ToolExecutionResult> {
    return firstValueFrom(this.http.post<ToolExecutionResult>(`/api/tools/${encodeURIComponent(name)}/execute`, { arguments: args, confirmed }));
  }
}
