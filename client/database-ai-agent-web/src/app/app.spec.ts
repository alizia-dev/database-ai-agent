import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';

describe('App routing', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  it('renders the landing hero inside the public shell', async () => {
    const harness = await RouterTestingHarness.create('/');

    expect(harness.fixture.nativeElement.textContent).toContain('Talk to Your Business Database with AI');
    TestBed.inject(HttpTestingController).expectOne('/api/system/info').flush({ name: 'x', version: '1.0.0', environment: 'Test' });
  });

  it('shows the service as online when the API responds', async () => {
    const harness = await RouterTestingHarness.create('/');
    TestBed.inject(HttpTestingController)
      .expectOne('/api/system/info')
      .flush({ name: 'Database AI Agent', version: '1.0.0', environment: 'Test' });
    harness.detectChanges();
    await harness.fixture.whenStable();

    expect(harness.fixture.nativeElement.textContent).toContain('Service online');
  });

  it('shows the service as unavailable when the API is unreachable', async () => {
    const harness = await RouterTestingHarness.create('/');
    TestBed.inject(HttpTestingController)
      .expectOne('/api/system/info')
      .flush(null, { status: 503, statusText: 'Service Unavailable' });
    harness.detectChanges();
    await harness.fixture.whenStable();

    expect(harness.fixture.nativeElement.textContent).toContain('Service unavailable');
  });
});
