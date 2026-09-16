import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import { provideRouter } from '@angular/router';

import { authenticatedGuard, supervisorGuard } from './guards';
import { SessionService } from './session.service';
import { AgentRole, SessionResponse } from './models';

function session(role: AgentRole): SessionResponse {
  return {
    token: 'token-' + role,
    role,
    agent: {
      id: '22222222-0000-0000-0000-000000000001',
      name: 'Amara Okafor',
      extension: '1001',
      team: 'Team A',
      skills: { support: 5 },
      state: 'LoggedOut',
    },
  };
}

/**
 * Guards are UX, not security — the server refuses the request regardless (FR-A2). These tests
 * pin the *routing* behaviour, and in particular that a signed-in agent hitting a supervisor
 * route is sent somewhere useful rather than bounced to a sign-in page they already completed.
 */
describe('route guards', () => {
  let sessionService: SessionService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    sessionService = TestBed.inject(SessionService);
    sessionService.clear();
  });

  function run(guard: typeof authenticatedGuard, url = '/supervisor') {
    return TestBed.runInInjectionContext(() =>
      guard({} as never, { url } as never),
    );
  }

  it('sends an anonymous visitor to the login page', () => {
    const result = run(authenticatedGuard);

    expect(result instanceof UrlTree).toBeTrue();
    expect((result as UrlTree).toString()).toContain('/login');
  });

  it('lets a signed-in agent through an authenticated route', () => {
    sessionService.start(session('Agent'));

    expect(run(authenticatedGuard, '/agent')).toBeTrue();
  });

  it('keeps an agent out of the supervisor console', () => {
    sessionService.start(session('Agent'));

    const result = run(supervisorGuard);

    expect(result instanceof UrlTree).toBeTrue();
    // Redirected to their own desktop with an explanation — not to a sign-in page.
    expect((result as UrlTree).toString()).toContain('/agent');
    expect((result as UrlTree).toString()).toContain('denied=supervisor');
  });

  it('lets a supervisor into the supervisor console', () => {
    sessionService.start(session('Supervisor'));

    expect(run(supervisorGuard)).toBeTrue();
  });

  it('treats an admin as a supervisor', () => {
    sessionService.start(session('Admin'));

    expect(run(supervisorGuard)).toBeTrue();
    expect(TestBed.inject(SessionService).isSupervisor()).toBeTrue();
  });

  afterEach(() => {
    TestBed.inject(SessionService).clear();
    TestBed.inject(Router);
  });
});
