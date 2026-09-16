import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionService } from './session.service';

/**
 * Route guards mirror the server's roles — but they are **UX, not security**.
 *
 * The real control is `[Authorize(Roles = "Supervisor")]` on the controller: an agent who edits
 * their way past these guards still gets a 403 from every supervisor endpoint. Guards exist so a
 * user is not shown a console that will only ever return errors. Keeping that distinction explicit
 * matters, because a client-side check that *looks* like authorisation is how teams end up with
 * neither.
 */
export const authenticatedGuard: CanActivateFn = () => {
  const session = inject(SessionService);
  const router = inject(Router);

  return session.isAuthenticated() ? true : router.createUrlTree(['/login']);
};

export const supervisorGuard: CanActivateFn = (route, state) => {
  const session = inject(SessionService);
  const router = inject(Router);

  if (!session.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }

  // Signed in, wrong role: send them somewhere useful rather than to a sign-in page they have
  // already completed.
  return session.isSupervisor() ? true : router.createUrlTree(['/agent'], { queryParams: { denied: 'supervisor' } });
};
