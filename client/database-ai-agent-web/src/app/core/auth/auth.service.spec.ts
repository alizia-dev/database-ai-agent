import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { authInterceptor } from './auth.interceptor';
import { session } from '../../testing/auth-fixtures';
import { AuthService } from './auth.service';

describe('AuthService', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'login', children: [] }]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  it('keeps the access token in memory and exposes the user and permissions after login', async () => {
    const login = auth.login('ava@example.test', 'pw');
    http.expectOne('/api/auth/login').flush(session('access-1', ['users.view']));
    await login;

    expect(auth.getAccessToken()).toBe('access-1');
    expect(auth.user()?.displayName).toBe('Ava');
    expect(auth.hasPermission('users.view')).toBe(true);
    expect(auth.hasPermission('users.manage')).toBe(false);
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('sends the CSRF header on cookie-authenticated auth calls', async () => {
    const refresh = auth.refresh();
    const req = await vi.waitFor(() => http.expectOne('/api/auth/refresh'));
    expect(req.request.headers.get('X-Requested-With')).toBe('XMLHttpRequest');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush(session());
    expect(await refresh).toBe(true);
  });

  it('collapses concurrent refreshes into a single request', async () => {
    const first = auth.refresh();
    const second = auth.refresh();

    const req = await vi.waitFor(() => http.expectOne('/api/auth/refresh'));
    req.flush(session('access-2'));

    expect(await first).toBe(true);
    expect(await second).toBe(true);
    expect(auth.getAccessToken()).toBe('access-2');
  });

  it('retries once when another tab rotated the token at the same moment (409)', async () => {
    vi.useFakeTimers();
    const refresh = auth.refresh();

    (await vi.waitFor(() => http.expectOne('/api/auth/refresh'))).flush({ code: 'auth.refresh_in_progress' }, { status: 409, statusText: 'Conflict' });
    await vi.advanceTimersByTimeAsync(1_000);
    http.expectOne('/api/auth/refresh').flush(session('access-3'));

    expect(await refresh).toBe(true);
    expect(auth.getAccessToken()).toBe('access-3');
  });

  it('clears the session when the refresh token is rejected', async () => {
    const login = auth.login('ava@example.test', 'pw');
    http.expectOne('/api/auth/login').flush(session());
    await login;

    const refresh = auth.refresh();
    (await vi.waitFor(() => http.expectOne('/api/auth/refresh'))).flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(await refresh).toBe(false);
    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.getAccessToken()).toBeNull();
  });

  it('signs out locally even when the server cannot be reached', async () => {
    const login = auth.login('ava@example.test', 'pw');
    http.expectOne('/api/auth/login').flush(session());
    await login;

    const logout = auth.logout();
    http.expectOne('/api/auth/logout').error(new ProgressEvent('error'));
    await logout;

    expect(auth.isAuthenticated()).toBe(false);
  });
});
