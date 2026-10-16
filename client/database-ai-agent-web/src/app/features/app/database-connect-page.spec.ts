import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { DatabaseProviderInfo } from '../../core/database/database.service';
import { DatabaseConnectPage } from './database-connect-page';
import { DatabasePage } from './database-page';
import { entitlements } from '../../testing/billing-fixtures';

const providers: DatabaseProviderInfo[] = [
  { kind: 'SqlServer', displayName: 'Microsoft SQL Server', defaultPort: 1433, leastPrivilegeSetupScript: 'CREATE LOGIN [{user}]; USE [{database}]; GRANT EXECUTE' },
  { kind: 'PostgreSql', displayName: 'PostgreSQL', defaultPort: 5432, leastPrivilegeSetupScript: 'CREATE ROLE "{user}"' },
  { kind: 'MySql', displayName: 'MySQL', defaultPort: 3306, leastPrivilegeSetupScript: "CREATE USER '{user}'" },
];

function setup(): HttpTestingController {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'app/database/connect', component: DatabaseConnectPage },
        { path: 'app/database', component: DatabasePage },
      ]),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });
  return TestBed.inject(HttpTestingController);
}

function type(root: HTMLElement, name: string, value: string): void {
  const input = root.querySelector<HTMLInputElement | HTMLTextAreaElement>(`[formcontrolname="${name}"]`)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

function click(root: HTMLElement, label: string): void {
  Array.from(root.querySelectorAll('button')).find((b) => b.textContent?.trim().includes(label))!.click();
}

/** Waits until initial data has loaded (the loading bar is gone and the form is enabled). */
async function loaded(harness: RouterTestingHarness): Promise<HTMLElement> {
  const root = harness.routeNativeElement as HTMLElement;
  await vi.waitFor(() => {
    harness.detectChanges();
    expect(root.querySelector('mat-progress-bar')).toBeNull();
  });
  return root;
}

async function text(harness: RouterTestingHarness, expected: string): Promise<string> {
  let content = '';
  await vi.waitFor(() => {
    harness.detectChanges();
    content = (harness.routeNativeElement as HTMLElement).textContent ?? '';
    expect(content).toContain(expected);
  });
  return content;
}

describe('DatabaseConnectPage', () => {
  it('tests the entered details and shows least-privilege warnings', async () => {
    const http = setup();
    const harness = await RouterTestingHarness.create('/app/database/connect');
    http.expectOne('/api/database/providers').flush(providers);
    const root = await loaded(harness);

    type(root, 'name', 'CRM');
    type(root, 'host', 'db.example.com');
    type(root, 'database', 'crm');
    type(root, 'username', 'reporting');
    type(root, 'password', 'pw');
    click(root, 'Test connection');

    const req = http.expectOne('/api/database/connections/test');
    expect(req.request.body).toMatchObject({ name: 'CRM', provider: 'SqlServer', host: 'db.example.com', database: 'crm', username: 'reporting', tlsMode: 'VerifyFull' });
    req.flush({
      succeeded: true,
      failure: 'None',
      message: 'Connected successfully.',
      serverVersion: 'Microsoft SQL Server 2022',
      permissions: { isAdministrator: true, canReadTablesDirectly: true, canWriteData: true, executableProcedureCount: 12 },
      warnings: ['This account has administrator rights.'],
      ignoredOptions: [],
      durationMs: 42,
    });

    const content = await text(harness, 'Connected successfully.');
    expect(content).toContain('This account has administrator rights.');
    expect(content).toContain('Read and write');
    // The setup script is personalised with the entered user and database.
    expect(content).toContain('CREATE LOGIN [reporting]; USE [crm]');
  });

  it('sends a pasted connection string as-is and returns to the list after saving', async () => {
    const http = setup();
    const harness = await RouterTestingHarness.create('/app/database/connect');
    http.expectOne('/api/database/providers').flush(providers);
    const root = await loaded(harness);

    click(root, 'Paste connection string');
    harness.detectChanges();
    type(root, 'name', 'Shop');
    type(root, 'connectionString', 'Server=db.example.com;Database=shop;User ID=app;Password=pw');
    click(root, 'Test and connect');

    const req = http.expectOne('/api/database/connections');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toMatchObject({ name: 'Shop', connectionString: 'Server=db.example.com;Database=shop;User ID=app;Password=pw', tlsMode: null });
    req.flush({ connection: { id: '1', name: 'Shop' }, test: { succeeded: true } });

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/app/database'));
  });

  it('shows the server message when the connection fails', async () => {
    const http = setup();
    const harness = await RouterTestingHarness.create('/app/database/connect');
    http.expectOne('/api/database/providers').flush(providers);
    const root = await loaded(harness);

    type(root, 'name', 'CRM');
    type(root, 'host', 'db.example.com');
    type(root, 'database', 'crm');
    type(root, 'username', 'reporting');
    type(root, 'password', 'wrong');
    click(root, 'Test and connect');
    http.expectOne('/api/database/connections').flush(
      { detail: 'The server rejected the username or password.', code: 'database.connection_failed' },
      { status: 400, statusText: 'Bad Request' },
    );

    await text(harness, 'The server rejected the username or password.');
  });
});

describe('DatabasePage', () => {
  it('explains that the free plan cannot connect databases', async () => {
    const http = setup();
    const harness = await RouterTestingHarness.create('/app/database');
    http.expectOne('/api/database/connections').flush([]);
    http.expectOne('/api/billing/entitlements').flush(entitlements());

    const content = await text(harness, 'includes the demo only');
    expect(content).not.toContain('Connect a database');
  });
});
