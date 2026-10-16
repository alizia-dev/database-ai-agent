import { HttpInterceptorFn } from '@angular/common/http';

export const CORRELATION_ID_HEADER = 'X-Correlation-ID';

/** Generates a 32-char hex id (matches the server's accepted format). */
export function newCorrelationId(): string {
  return crypto.randomUUID().replaceAll('-', '');
}

/**
 * Tags every API request with a correlation id so a user-visible error can be traced to server logs.
 * Requests to other origins are left untouched.
 */
export const correlationIdInterceptor: HttpInterceptorFn = (req, next) => {
  const isApiRequest = req.url.startsWith('/api/') || req.url.startsWith('/hubs/');
  if (!isApiRequest || req.headers.has(CORRELATION_ID_HEADER)) {
    return next(req);
  }
  return next(req.clone({ setHeaders: { [CORRELATION_ID_HEADER]: newCorrelationId() } }));
};
