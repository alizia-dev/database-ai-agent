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

  delete(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/database/connections/${encodeURIComponent(id)}`));
  }
}
