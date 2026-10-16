import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type DatabaseProviderKind = 'SqlServer' | 'PostgreSql' | 'MySql';
export type TlsMode = 'Disabled' | 'Preferred' | 'Required' | 'VerifyFull';
export type ConnectionStatus = 'Untested' | 'Connected' | 'Failed';
export type ConnectionFailureKind =
  | 'None'
  | 'HostNotAllowed'
  | 'HostUnreachable'
  | 'AuthenticationFailed'
  | 'DatabaseNotFound'
  | 'Timeout'
  | 'TlsFailure'
  | 'PermissionDenied'
  | 'Unknown';

export interface PermissionSummary {
  isAdministrator: boolean;
  canReadTablesDirectly: boolean;
  canWriteData: boolean;
  executableProcedureCount: number;
}

export interface DatabaseProviderInfo {
  kind: DatabaseProviderKind;
  displayName: string;
  defaultPort: number;
  leastPrivilegeSetupScript: string;
}

/** Details as entered. On update, leave the password empty to keep the stored credentials. */
export interface ConnectionInput {
  name: string;
  provider: DatabaseProviderKind;
  host?: string | null;
  port?: number | null;
  database?: string | null;
  username?: string | null;
  password?: string | null;
  tlsMode?: TlsMode | null;
  connectionString?: string | null;
}

export interface ConnectionTest {
  succeeded: boolean;
  failure: ConnectionFailureKind;
  message: string;
  serverVersion: string | null;
  permissions: PermissionSummary | null;
  warnings: string[];
  ignoredOptions: string[];
  durationMs: number;
}

/** A saved connection. The API never returns credentials. */
export interface DatabaseConnection {
  id: string;
  name: string;
  provider: DatabaseProviderKind;
  host: string;
  port: number;
  databaseName: string;
  tlsMode: TlsMode;
  status: ConnectionStatus;
  lastTestedAt: string | null;
  lastFailure: ConnectionFailureKind;
  lastFailureMessage: string | null;
  serverVersion: string | null;
  permissions: PermissionSummary;
  warnings: string[];
  createdAt: string;
}

export interface SavedConnection {
  connection: DatabaseConnection;
  test: ConnectionTest;
}

export type AnalysisStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed';

export interface AnalysisCounts {
  schemas: number;
  tables: number;
  views: number;
  columns: number;
  relationships: number;
  procedures: number;
  newProcedures: number;
  missingProcedures: number;
}

export interface Analysis {
  id: string;
  status: AnalysisStatus;
  queuedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  failureMessage: string | null;
  counts: AnalysisCounts;
  warnings: string[];
}

export interface SchemaOverview {
  latestAnalysis: Analysis | null;
  schemas: { name: string; tables: number; views: number; procedures: number }[];
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface TableSummary {
  id: string;
  schemaName: string;
  name: string;
  kind: 'Table' | 'View';
  description: string | null;
  columnCount: number;
  estimatedRowCount: number | null;
}

export interface Column {
  name: string;
  ordinal: number;
  dataType: string;
  maxLength: number | null;
  precision: number | null;
  scale: number | null;
  isNullable: boolean;
  isPrimaryKey: boolean;
  isIdentity: boolean;
  description: string | null;
}

export interface Relationship {
  name: string;
  fromSchema: string;
  fromTable: string;
  fromColumns: string[];
  toSchema: string;
  toTable: string;
  toColumns: string[];
}

export interface TableDetail extends Omit<TableSummary, 'columnCount'> {
  columns: Column[];
  indexes: { name: string; isUnique: boolean; isPrimaryKey: boolean; columns: string[] }[];
  references: Relationship[];
  referencedBy: Relationship[];
  lastSeenAt: string;
}

export interface RoutineBehavior {
  definitionAvailable: boolean;
  modifiesData: boolean;
  deletesData: boolean;
  changesSchema: boolean;
  usesDynamicSql: boolean;
  callsOtherRoutines: boolean;
}

export interface ProcedureSummary {
  id: string;
  schemaName: string;
  name: string;
  signature: string;
  kind: 'Procedure' | 'ScalarFunction' | 'TableFunction';
  description: string | null;
  parameterCount: number;
  canExecute: boolean;
  behavior: RoutineBehavior;
  isMissing: boolean;
}

export interface ProcedureDetail extends Omit<ProcedureSummary, 'parameterCount'> {
  returnType: string | null;
  isReadOnly: boolean;
  declaredDataAccess: string | null;
  averageDurationMs: number | null;
  parameters: { name: string; ordinal: number; dataType: string; direction: 'In' | 'Out' | 'InOut'; hasDefault: boolean }[];
  resultColumns: { name: string; ordinal: number; dataType: string; isNullable: boolean }[];
  firstSeenAt: string;
  lastSeenAt: string;
}

/** Human-readable behaviour badges for a routine (conservative: unknown is never shown as read-only). */
export function behaviorBadges(b: RoutineBehavior): { label: string; tone: 'ok' | 'warn' | 'risk' }[] {
  if (!b.definitionAvailable && !b.modifiesData) {
    return [{ label: 'Behaviour unknown', tone: 'warn' }];
  }
  const badges: { label: string; tone: 'ok' | 'warn' | 'risk' }[] = [];
  if (b.modifiesData) badges.push({ label: 'Modifies data', tone: 'risk' });
  if (b.deletesData) badges.push({ label: 'Deletes data', tone: 'risk' });
  if (b.changesSchema) badges.push({ label: 'Changes schema', tone: 'risk' });
  if (b.usesDynamicSql) badges.push({ label: 'Dynamic SQL', tone: 'warn' });
  if (b.callsOtherRoutines) badges.push({ label: 'Calls other routines', tone: 'warn' });
  return badges.length > 0 ? badges : [{ label: 'Read-only', tone: 'ok' }];
}

export const TLS_MODE_LABELS: Record<TlsMode, string> = {
  VerifyFull: 'Verify full (recommended): encrypted, certificate validated',
  Required: 'Required: encrypted, certificate not validated',
  Preferred: 'Preferred: encrypt when available',
  Disabled: 'Disabled: no encryption (trusted private networks only)',
};

@Injectable({ providedIn: 'root' })
export class DatabaseService {
  private readonly http = inject(HttpClient);

  providers(): Promise<DatabaseProviderInfo[]> {
    return firstValueFrom(this.http.get<DatabaseProviderInfo[]>('/api/database/providers'));
  }

  list(): Promise<DatabaseConnection[]> {
    return firstValueFrom(this.http.get<DatabaseConnection[]>('/api/database/connections'));
  }

  get(id: string): Promise<DatabaseConnection> {
    return firstValueFrom(this.http.get<DatabaseConnection>(`/api/database/connections/${encodeURIComponent(id)}`));
  }

  test(input: ConnectionInput): Promise<ConnectionTest> {
    return firstValueFrom(this.http.post<ConnectionTest>('/api/database/connections/test', input));
  }

  create(input: ConnectionInput): Promise<SavedConnection> {
    return firstValueFrom(this.http.post<SavedConnection>('/api/database/connections', input));
  }

  update(id: string, input: ConnectionInput): Promise<SavedConnection> {
    return firstValueFrom(this.http.put<SavedConnection>(`/api/database/connections/${encodeURIComponent(id)}`, input));
  }

  retest(id: string): Promise<SavedConnection> {
    return firstValueFrom(this.http.post<SavedConnection>(`/api/database/connections/${encodeURIComponent(id)}/test`, {}));
  }

  analyze(id: string): Promise<Analysis> {
    return firstValueFrom(this.http.post<Analysis>(`/api/database/connections/${encodeURIComponent(id)}/analyze`, {}));
  }

  schema(id: string): Promise<SchemaOverview> {
    return firstValueFrom(this.http.get<SchemaOverview>(`/api/database/connections/${encodeURIComponent(id)}/schema`));
  }

  tables(id: string, query: { schema?: string; search?: string; page?: number }): Promise<Paged<TableSummary>> {
    return firstValueFrom(this.http.get<Paged<TableSummary>>(`/api/database/connections/${encodeURIComponent(id)}/tables`, { params: clean(query) }));
  }

  table(id: string, tableId: string): Promise<TableDetail> {
    return firstValueFrom(this.http.get<TableDetail>(`/api/database/connections/${encodeURIComponent(id)}/tables/${encodeURIComponent(tableId)}`));
  }

  procedures(id: string, query: { schema?: string; search?: string; page?: number; includeMissing?: boolean }): Promise<Paged<ProcedureSummary>> {
    return firstValueFrom(this.http.get<Paged<ProcedureSummary>>(`/api/database/connections/${encodeURIComponent(id)}/procedures`, { params: clean(query) }));
  }

  procedure(id: string, procedureId: string): Promise<ProcedureDetail> {
    return firstValueFrom(this.http.get<ProcedureDetail>(`/api/database/connections/${encodeURIComponent(id)}/procedures/${encodeURIComponent(procedureId)}`));
  }

  delete(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/database/connections/${encodeURIComponent(id)}`));
  }
}

function clean(query: Record<string, string | number | boolean | undefined>): Record<string, string> {
  return Object.fromEntries(Object.entries(query).filter(([, v]) => v !== undefined && v !== '').map(([k, v]) => [k, String(v)]));
}
