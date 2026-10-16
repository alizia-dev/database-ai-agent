import { HttpErrorResponse } from '@angular/common/http';
import { safeReturnUrl } from './auth.guards';
import { GENERIC_ERROR, OFFLINE_ERROR, toApiProblem } from '../http/api-problem';

describe('safeReturnUrl', () => {
  it.each([
    ['/app/team', '/app/team'],
    ['/app/settings?tab=1', '/app/settings?tab=1'],
    [null, '/app/dashboard'],
    ['https://evil.example/app', '/app/dashboard'],
    ['//evil.example/app', '/app/dashboard'],
    ['/login', '/app/dashboard'],
    ['/app\\..\\evil', '/app/dashboard'],
  ])('maps %s to %s', (input, expected) => {
    expect(safeReturnUrl(input)).toBe(expected);
  });
});

describe('toApiProblem', () => {
  it('uses the safe server message and field errors for client errors', () => {
    const problem = toApiProblem(
      new HttpErrorResponse({
        status: 400,
        error: { detail: 'Please correct the highlighted fields.', code: 'validation_failed', errors: { email: ['Enter a valid email address.'] } },
      }),
    );

    expect(problem.message).toBe('Please correct the highlighted fields.');
    expect(problem.code).toBe('validation_failed');
    expect(problem.fieldErrors['email']).toEqual(['Enter a valid email address.']);
  });

  it('never shows server error details for 5xx responses', () => {
    const problem = toApiProblem(new HttpErrorResponse({ status: 500, error: { detail: 'NpgsqlException: connection refused' } }));

    expect(problem.message).toBe(GENERIC_ERROR);
  });

  it('explains network failures', () => {
    expect(toApiProblem(new HttpErrorResponse({ status: 0 })).message).toBe(OFFLINE_ERROR);
  });
});
