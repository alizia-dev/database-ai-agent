import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { session } from '../../testing/auth-fixtures';
import { AuthService } from './auth.service';

describe('authInterceptor', () => {
  let auth: AuthService;
  let client: HttpClient;
  let http: HttpTestingController;

  async function signIn(token = 'access-1'): Promise<void> {
    const login = auth.login('ava@example.test', 'pw');
    http.expectOne('/api/auth/login').flush(session(token));
    await login;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'login', children: [] }]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthService);
    client = TestBed.inject(HttpClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('attaches the bearer token to API requests only', async () => {
    await signIn();

    client.get('/api/account/me').subscribe();
    client.get('https://cdn.example.test/file.json').subscribe();

    expect(http.expectOne('/api/account/me').request.headers.get('Authorization')).toBe('Bearer access-1');
    expect(http.expectOne('https://cdn.example.test/file.json').request.headers.has('Authorization')).toBe(false);
  });

  it('renews the session once on 401 and retries with the new token', async () => {
    await signIn('expired-token');

    const result = firstValueFrom(client.get<{ ok: boolean }>('/api/team/members'));
    http.expectOne('/api/team/members').flush({}, { status: 401, statusText: 'Unauthorized' });
    (await vi.waitFor(() => http.expectOne('/api/auth/refresh'))).flush(session('fresh-token'));
    const retry = await vi.waitFor(() => http.expectOne('/api/team/members'));

    expect(retry.request.headers.get('Authorization')).toBe('Bearer fresh-token');
    retry.flush({ ok: true });
    expect(await result).toEqual({ ok: true });
  });

  it('sends the user to sign in when the session cannot be renewed', async () => {
    await signIn();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    const result = firstValueFrom(client.get('/api/team/members'));
    http.expectOne('/api/team/members').flush({}, { status: 401, statusText: 'Unauthorized' });
    (await vi.waitFor(() => http.expectOne('/api/auth/refresh'))).flush({}, { status: 401, statusText: 'Unauthorized' });

    await expect(result).rejects.toBeTruthy();
    expect(auth.isAuthenticated()).toBe(false);
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.anything());
  });

  it('does not try to refresh for anonymous callers', async () => {
    const result = firstValueFrom(client.get('/api/account/me'));
    http.expectOne('/api/account/me').flush({}, { status: 401, statusText: 'Unauthorized' });

    await expect(result).rejects.toBeTruthy();
    http.expectNone('/api/auth/refresh');
  });
});
