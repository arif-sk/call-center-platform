import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { SessionService } from './session.service';

/**
 * Attaches the bearer token to every API call and reacts to the two answers the server can give
 * about identity.
 *
 * The 401/403 split is deliberate and is worth reading as a pair:
 *   401 — the session is gone or invalid. Clear it and send the user to sign in again.
 *   403 — the session is fine, the *role* is not. Do NOT sign the user out; that would be a
 *         confusing bounce for a permission problem. Let the caller surface the refusal.
 *
 * Both are decided by the server (FR-A2); this interceptor only routes the consequence.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(SessionService);
  const router = inject(Router);

  const token = session.token();

  const authorised = token
    ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : request;

  return next(authorised).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401 && session.isAuthenticated()) {
        session.clear();
        void router.navigate(['/login'], { queryParams: { reason: 'expired' } });
      }

      return throwError(() => error);
    }),
  );
};

/** Pulls the server's `{ "error": "..." }` body out of a failed response for display. */
export function describeHttpError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { error?: string; title?: string; errors?: Record<string, string[]> } | null;

    if (body?.error) return body.error;

    // RFC 7807 problem details from [ApiController] model validation.
    if (body?.errors) {
      const first = Object.values(body.errors)[0];
      if (first?.length) return first[0];
    }

    if (body?.title) return body.title;
    if (error.status === 0) return 'Cannot reach the platform. Check your connection.';

    return `${error.status} ${error.statusText}`;
  }

  return 'Unexpected error.';
}
