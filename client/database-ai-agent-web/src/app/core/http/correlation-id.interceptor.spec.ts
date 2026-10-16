import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { CORRELATION_ID_HEADER, correlationIdInterceptor } from './correlation-id.interceptor';

describe('correlationIdInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([correlationIdInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  it('adds a server-compatible correlation id to API requests', () => {
    http.get('/api/system/info').subscribe();

    const req = controller.expectOne('/api/system/info');
    expect(req.request.headers.get(CORRELATION_ID_HEADER)).toMatch(/^[0-9a-f]{32}$/);
    req.flush({});
  });

  it('does not tag requests to other origins', () => {
    http.get('https://example.com/data').subscribe();

    const req = controller.expectOne('https://example.com/data');
    expect(req.request.headers.has(CORRELATION_ID_HEADER)).toBe(false);
    req.flush({});
  });

  it('keeps an explicitly supplied correlation id', () => {
    http.get('/api/system/info', { headers: { [CORRELATION_ID_HEADER]: 'abc' } }).subscribe();

    const req = controller.expectOne('/api/system/info');
    expect(req.request.headers.get(CORRELATION_ID_HEADER)).toBe('abc');
    req.flush({});
  });
});
