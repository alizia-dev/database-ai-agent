import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ResponseStyle = 'Concise' | 'Balanced' | 'Detailed';

export interface RetentionPolicy {
  conversationDays: number | null;
  auditLogDays: number | null;
  usageRecordDays: number | null;
  knowledgeDocumentDays: number | null;
}

export interface WorkspaceSettings {
  name: string;
  industry: string | null;
  assistantName: string;
  businessDescription: string | null;
  timeZone: string;
  currency: string;
  dateFormat: string;
  responseStyle: ResponseStyle;
  retention: RetentionPolicy;
  updatedAt: string | null;
}

export type UpdateWorkspaceSettings = Omit<WorkspaceSettings, 'updatedAt'>;

@Injectable({ providedIn: 'root' })
export class WorkspaceService {
  private readonly http = inject(HttpClient);
  private readonly current = signal<WorkspaceSettings | null>(null);

  readonly settings = this.current.asReadonly();

  async load(): Promise<WorkspaceSettings> {
    const settings = await firstValueFrom(this.http.get<WorkspaceSettings>('/api/workspace/settings'));
    this.current.set(settings);
    return settings;
  }

  async update(settings: UpdateWorkspaceSettings): Promise<WorkspaceSettings> {
    const updated = await firstValueFrom(this.http.put<WorkspaceSettings>('/api/workspace/settings', settings));
    this.current.set(updated);
    return updated;
  }
}
