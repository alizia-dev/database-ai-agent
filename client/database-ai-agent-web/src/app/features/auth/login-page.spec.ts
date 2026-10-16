import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { authInterceptor } from '../../core/auth/auth.interceptor';
import { session } from '../../testing/auth-fixtures';
import { LoginPage } from './login-page';

describe('LoginPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', component: LoginPage },
          { path: 'app/team', children: [] },
          { path: 'app/dashboard', children: [] },
        ]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    harness = await RouterTestingHarness.create('/login?returnUrl=%2Fapp%2Fteam');
  });

  function fill(email: string, password: string): void {
    const element: HTMLElement = harness.routeNativeElement!;
    const [emailInput, passwordInput] = Array.from(element.querySelectorAll('input'));
    emailInput.value = email;
    emailInput.dispatchEvent(new Event('input'));
    passwordInput.value = password;
    passwordInput.dispatchEvent(new Event('input'));
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
  }

  it('signs in and returns to the requested in-app page', async () => {
    fill('ava@example.test', 'correct horse battery staple');

    const req = http.expectOne('/api/auth/login');
    expect(req.request.body).toEqual({ email: 'ava@example.test', password: 'correct horse battery staple' });
    req.flush(session());

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/app/team'));
  });

  it('shows the safe server message when credentials are wrong', async () => {
    fill('ava@example.test', 'wrong');

    http.expectOne('/api/auth/login').flush(
      { detail: 'The email or password is incorrect.', code: 'auth.invalid_credentials' },
      { status: 401, statusText: 'Unauthorized' },
    );

    await vi.waitFor(() => {
      harness.detectChanges();
      expect(harness.routeNativeElement!.textContent).toContain('The email or password is incorrect.');
    });
  });

  it('offers a new verification link when the email is not verified', async () => {
    fill('ava@example.test', 'correct horse battery staple');

    http.expectOne('/api/auth/login').flush(
      { detail: 'Please verify your email address before signing in.', code: 'auth.email_not_verified' },
      { status: 403, statusText: 'Forbidden' },
    );

    await vi.waitFor(() => {
      harness.detectChanges();
      expect(harness.routeNativeElement!.textContent).toContain('Send a new verification link');
    });
  });

  it('does not call the API when the form is invalid', () => {
    fill('', '');

    http.expectNone('/api/auth/login');
  });
});
