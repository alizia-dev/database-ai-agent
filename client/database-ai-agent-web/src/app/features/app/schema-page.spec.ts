import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthService } from '../../core/auth/auth.service';
import { behaviorBadges } from '../../core/database/database.service';
import { SchemaPage } from './schema-page';

const connection = { id: 'c1', name: 'CRM', provider: 'SqlServer', host: 'db', port: 1433, databaseName: 'crm', tlsMode: 'VerifyFull', status: 'Connected', permissions: {}, warnings: [] };
const behavior = { definitionAvailable: true, modifiesData: false, deletesData: false, changesSchema: false, usesDynamicSql: false, callsOtherRoutines: false };

async function text(harness: RouterTestingHarness, expected: string): Promise<string> {
  let content = '';
  await vi.waitFor(() => {
    harness.detectChanges();
    content = (harness.routeNativeElement as HTMLElement).textContent ?? '';
    expect(content).toContain(expected);
  });
  return content;
}

describe('SchemaPage', () => {
  let http: HttpTestingController;

  async function open(permissions: string[]): Promise<RouterTestingHarness> {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'app/database/schema', component: SchemaPage }]), provideHttpClient(), provideHttpClientTesting()],
    });
    vi.spyOn(TestBed.inject(AuthService), 'hasPermission').mockImplementation((p) => permissions.includes(p));
    http = TestBed.inject(HttpTestingController);
    const harness = await RouterTestingHarness.create('/app/database/schema?connection=c1');
    http.expectOne('/api/database/connections').flush([connection]);
    return harness;
  }

  function flushCatalog(status: 'Succeeded' | 'Running'): void {
    http.expectOne('/api/database/connections/c1/schema').flush({
      latestAnalysis: {
        id: 'a1', status, queuedAt: '2026-10-16T10:00:00Z', startedAt: null, completedAt: '2026-10-16T10:01:00Z', failureMessage: null,
        counts: { schemas: 1, tables: 2, views: 0, columns: 7, relationships: 1, procedures: 2, newProcedures: 0, missingProcedures: 0 },
        warnings: ['Procedure definitions are not visible to this account.'],
      },
      schemas: [{ name: 'dbo', tables: 2, views: 0, procedures: 2 }],
    });
    vi.waitFor(() => http.expectOne((r) => r.url === '/api/database/connections/c1/tables')).then((r) =>
      r.flush({ items: [{ id: 't1', schemaName: 'dbo', name: 'Customers', kind: 'Table', description: null, columnCount: 3, estimatedRowCount: 10 }], total: 1, page: 1, pageSize: 50 }));
    vi.waitFor(() => http.expectOne((r) => r.url === '/api/database/connections/c1/procedures')).then((r) =>
      r.flush({
        items: [
          { id: 'p1', schemaName: 'dbo', name: 'GetCustomers', signature: '', kind: 'Procedure', description: null, parameterCount: 1, canExecute: true, behavior, isMissing: false },
          { id: 'p2', schemaName: 'dbo', name: 'SetCreditLimit', signature: '', kind: 'Procedure', description: null, parameterCount: 2, canExecute: true, behavior: { ...behavior, modifiesData: true }, isMissing: false },
        ],
        total: 2, page: 1, pageSize: 50,
      }));
  }

  it('shows the latest analysis, warnings and procedure behaviour', async () => {
    const harness = await open(['database.view', 'database.analyze']);
    flushCatalog('Succeeded');

    const content = await text(harness, 'dbo.Customers');
    expect(content).toContain('2 tables');
    expect(content).toContain('Procedure definitions are not visible');
    expect(content).toContain('Re-analyze');

    // Tab content renders when the tab is opened.
    (harness.routeNativeElement as HTMLElement).querySelectorAll<HTMLElement>('[role="tab"]')[1].click();
    const procedures = await text(harness, 'Modifies data');
    expect(procedures).toContain('Read-only');
    expect(procedures).toContain('dbo.SetCreditLimit');
  });

  it('starts an analysis and shows progress', async () => {
    const harness = await open(['database.view', 'database.analyze']);
    flushCatalog('Succeeded');
    await text(harness, 'Re-analyze');

    Array.from((harness.routeNativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.includes('Re-analyze'))!.click();
    http.expectOne('/api/database/connections/c1/analyze').flush({ id: 'a2', status: 'Queued' });
    await vi.waitFor(() => http.expectOne('/api/database/connections/c1/schema'));
  });

  it('hides the analyze action without database.analyze', async () => {
    const harness = await open(['database.view']);
    flushCatalog('Succeeded');

    const content = await text(harness, 'dbo.Customers');
    expect(content).not.toContain('Re-analyze');
  });
});

describe('behaviorBadges', () => {
  it('never labels an unreadable definition as read-only', () => {
    expect(behaviorBadges({ ...behavior, definitionAvailable: false })).toEqual([{ label: 'Behaviour unknown', tone: 'warn' }]);
    expect(behaviorBadges(behavior)).toEqual([{ label: 'Read-only', tone: 'ok' }]);
    expect(behaviorBadges({ ...behavior, deletesData: true, usesDynamicSql: true }).map((b) => b.label)).toEqual(['Deletes data', 'Dynamic SQL']);
  });
});
