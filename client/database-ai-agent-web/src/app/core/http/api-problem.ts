import { HttpErrorResponse } from '@angular/common/http';
import { AbstractControl, FormGroup } from '@angular/forms';

/** A server error normalized for display. Messages come from the API's safe ProblemDetails, never raw exceptions. */
export interface ApiProblem {
  status: number;
  code?: string;
  message: string;
  fieldErrors: Record<string, string[]>;
  correlationId?: string;
}

export const GENERIC_ERROR = 'We could not execute this request. Please try again or contact your administrator.';
export const OFFLINE_ERROR = "We couldn't reach the service. Check your connection and try again.";

export function toApiProblem(error: unknown): ApiProblem {
  if (!(error instanceof HttpErrorResponse)) {
    return { status: -1, message: GENERIC_ERROR, fieldErrors: {} };
  }

  if (error.status === 0) {
    return { status: 0, message: OFFLINE_ERROR, fieldErrors: {} };
  }

  const body = (typeof error.error === 'object' && error.error) || {};
  const fieldErrors: Record<string, string[]> = isStringArrayRecord(body.errors) ? body.errors : {};
  const message =
    error.status >= 500
      ? GENERIC_ERROR
      : typeof body.detail === 'string'
        ? body.detail
        : typeof body.title === 'string'
          ? body.title
          : error.status === 429
            ? 'Too many attempts. Please wait a moment and try again.'
            : GENERIC_ERROR;

  return {
    status: error.status,
    code: typeof body.code === 'string' ? body.code : undefined,
    message,
    fieldErrors,
    correlationId: typeof body.correlationId === 'string' ? body.correlationId : undefined,
  };
}

/** Puts server-side field errors on the matching form controls (keys are camelCase control names). */
export function applyFieldErrors(form: FormGroup, problem: ApiProblem): void {
  for (const [field, messages] of Object.entries(problem.fieldErrors)) {
    const control: AbstractControl | null = form.get(field);
    if (control && messages.length > 0) {
      control.setErrors({ ...(control.errors ?? {}), server: messages.join(' ') });
      control.markAsTouched();
    }
  }
}

function isStringArrayRecord(value: unknown): value is Record<string, string[]> {
  return (
    typeof value === 'object' &&
    value !== null &&
    Object.values(value).every((v) => Array.isArray(v) && v.every((m) => typeof m === 'string'))
  );
}
