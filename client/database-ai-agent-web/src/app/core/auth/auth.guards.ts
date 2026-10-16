import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Permission } from './auth.models';
import { AuthService } from './auth.service';

/** Signed-in users only; others go to /login and come back afterwards. */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() || inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

/** Pages like /login and /register redirect signed-in users to the app. */
export const guestGuard: CanActivateFn = () =>
  !inject(AuthService).isAuthenticated() || inject(Router).createUrlTree(['/app/dashboard']);

/** UX guard only: the API enforces every permission server-side. */
export const permissionGuard =
  (permission: Permission): CanActivateFn =>
  () =>
    inject(AuthService).hasPermission(permission) || inject(Router).createUrlTree(['/app/dashboard']);

/** Accepts only in-app paths as post-login destinations (prevents open redirects). */
export function safeReturnUrl(candidate: string | null | undefined): string {
  return candidate && candidate.startsWith('/app') && !candidate.startsWith('//') && !candidate.includes('\\')
    ? candidate
    : '/app/dashboard';
}
