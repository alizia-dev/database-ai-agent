import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

/** Endpoints authenticated by the refresh cookie (or anonymous); they never carry the bearer token. */
const isAuthEndpoint = (url: string) => url.startsWith('/api/auth/') || url.startsWith('/api/invitations/');

/**
 * Adds the in-memory access token to API calls and, on a 401, renews the session once and retries.
 * Auth endpoints get the X-Requested-With header the API requires on cookie-authenticated calls (CSRF defence).
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/')) {
    return next(req);
  }

  const auth = inject(AuthService);

  if (isAuthEndpoint(req.url)) {
    return next(req.clone({ setHeaders: { 'X-Requested-With': 'XMLHttpRequest' } }));
  }

  const withToken = (request: HttpRequest<unknown>) => {
    const token = auth.getAccessToken();
    return token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
  };

  return next(withToken(req)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || !auth.isAuthenticated()) {
        return throwError(() => error);
      }

      return from(auth.refresh()).pipe(
        switchMap((renewed) => {
          if (renewed) {
            return next(withToken(req));
          }
          void auth.handleSessionExpired();
          return throwError(() => error);
        }),
      );
    }),
  );
};
