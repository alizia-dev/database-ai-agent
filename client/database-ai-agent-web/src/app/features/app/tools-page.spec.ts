import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthService } from '../../core/auth/auth.service';
import { ToolDetailPage } from './tool-detail-page';
import { ToolsPage } from './tools-page';

const summary = {
  id: 't1', connectionId: 'c1', name: 'get_customers', description: 'Customers above a balance.', operation: 'Read', risk: 'Low',
  classificationSource: 'DefinitionAnalysis', status: 'PendingReview', disabledReason: 'None', procedureName: 'dbo.GetCustomers',
  parameterCount: 1, supported: true, approvedAt: null,
};
const detail = {
  summary, classificationReasons: ['The definition only reads data.'],
  parameters: [{ name: 'minBalance', sourceName: '@MinBalance', ordinal: 1, type: 'Number', sourceDataType: 'decimal', required: true, maxLength: null, minimum: null, maximum: null, description: 'Minimum balance.' }],
  parametersSchema: {}, allowedRoles: ['Owner', 'Admin', 'Manager', 'Analyst', 'Viewer'], timeoutSeconds: 30, maxRows: 500, maxResultBytes: 1048576,
  requiresConfirmation: false, unsupportedReason: null, rejectionReason: null, canApprove: true, approvalBlockedReason: null, canConfirmReadOnly: false,
  procedure: null, recentExecutions: [],
};

async function text(harness: RouterTestingHarness, expected: string): Promise<string> {
  let content = '';
  await vi.waitFor(() => {
    harness.detectChanges();
    content = (harness.routeNativeElement as HTMLElement).textContent ?? '';
    expect(content).toContain(expected);
  });
  return content;
}

function button(harness: RouterTestingHarness, label: string): HTMLButtonElement {
  return Array.from((harness.routeNativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.includes(label))!;
}

describe('Tools pages', () => {
  let http: HttpTestingController;

  async function open(url: string, permissions: string[]): Promise<RouterTestingHarness> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'app/tools', component: ToolsPage }, { path: 'app/tools/:id', component: ToolDetailPage }], withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    vi.spyOn(TestBed.inject(AuthService), 'hasPermission').mockImplementation((p) => permissions.includes(p));
    http = TestBed.inject(HttpTestingController);
    return RouterTestingHarness.create(url);
  }

  it('lists tools with status and risk, and counts those needing review', async () => {
    const harness = await open('/app/tools', ['tools.view']);
    await vi.waitFor(() => http.expectOne((r) => r.url === '/api/tools')).then((r) =>
      r.flush([summary, { ...summary, id: 't2', name: 'set_limit', operation: 'Write', risk: 'High', status: 'Enabled' }]));

    const content = await text(harness, 'get_customers');
    expect(content).toContain('Needs review');
    expect(content).toContain('Write · High risk');
    expect(content).toContain('1 tool(s) need review');
    expect(content).not.toContain('Regenerate drafts');
  });

  it('approves a read tool and runs it from the test panel', async () => {
    const harness = await open('/app/tools/t1', ['tools.view', 'tools.manage', 'tools.execute']);
    http.expectOne('/api/tools/t1').flush(detail);
    await text(harness, 'The definition only reads data.');
    expect(await text(harness, 'Approve the tool to run it.')).toContain('Settings');

    button(harness, 'Approve').click();
    const approve = http.expectOne('/api/tools/t1/approve');
    expect(approve.request.body).toEqual({ acknowledgeDataChanges: false });
    approve.flush({ ...detail, summary: { ...summary, status: 'Enabled' }, canApprove: false });
    await text(harness, 'Disable');

    const input = (harness.routeNativeElement as HTMLElement).querySelector<HTMLInputElement>('input[name="arg-minBalance"]')!;
    input.value = '250';
    input.dispatchEvent(new Event('input'));
    button(harness, 'Run').click();

    const execute = await vi.waitFor(() => http.expectOne('/api/tools/get_customers/execute'));
    expect(execute.request.body).toEqual({ arguments: { minBalance: 250 }, confirmed: false });
    execute.flush({
      outcome: 'Succeeded', message: 'Returned 1 row.', reasonCode: null, executionId: 'e1', toolName: 'get_customers',
      columns: ['id', 'name'], rows: [[1, 'Contoso']], outputValues: {}, truncated: false, retrievedAt: '2026-10-16T10:00:00Z',
      durationMs: 12, confirmationPrompt: null, argumentErrors: null,
    });
    await vi.waitFor(() => http.expectOne('/api/tools/t1')).then((r) => r.flush({ ...detail, summary: { ...summary, status: 'Enabled' } }));

    const content = await text(harness, 'Contoso');
    expect(content).toContain('Returned 1 row.');
  });

  it('requires acknowledgement before approving a write tool', async () => {
    const harness = await open('/app/tools/t1', ['tools.view', 'tools.manage']);
    http.expectOne('/api/tools/t1').flush({ ...detail, summary: { ...summary, operation: 'Write', risk: 'High' }, requiresConfirmation: true });

    await text(harness, 'I understand this tool changes data');
    expect(button(harness, 'Approve').disabled).toBe(true);
  });

  it('explains why a tool cannot be approved', async () => {
    const harness = await open('/app/tools/t1', ['tools.view', 'tools.manage']);
    http.expectOne('/api/tools/t1').flush({
      ...detail, canApprove: false, approvalBlockedReason: 'Tools that change data are disabled for this workspace.',
    });

    await text(harness, 'Cannot be approved: Tools that change data are disabled');
  });
});
