import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { authInterceptor, describeHttpError } from './auth.interceptor';
import { SessionService } from './session.service';

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let session: SessionService;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
    router = TestBed.inject(Router);

    session.clear();
  });

  afterEach(() => backend.verify());

  it('does not send an Authorization header when signed out', () => {
    http.get('/api/v1/bootstrap').subscribe();

    const request = backend.expectOne('/api/v1/bootstrap');
    expect(request.request.headers.has('Authorization')).toBeFalse();
    request.flush({});
  });

  it('attaches the bearer token when signed in', () => {
    session.start({
      token: 'abc123',
      role: 'Agent',
      agent: { id: 'a', name: 'A', extension: '1', team: 't', skills: {}, state: 'LoggedOut' },
    });

    http.get('/api/v1/agents/me').subscribe();

    const request = backend.expectOne('/api/v1/agents/me');
    expect(request.request.headers.get('Authorization')).toBe('Bearer abc123');
    request.flush({});
  });

  it('clears the session and redirects on 401', () => {
    session.start({
      token: 'stale',
      role: 'Agent',
      agent: { id: 'a', name: 'A', extension: '1', team: 't', skills: {}, state: 'LoggedOut' },
    });

    const navigate = spyOn(router, 'navigate').and.resolveTo(true);

    http.get('/api/v1/agents/me').subscribe({ error: () => undefined });
    backend.expectOne('/api/v1/agents/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(session.isAuthenticated()).toBeFalse();
    expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { reason: 'expired' } });
  });

  it('does NOT sign the user out on 403', () => {
    // 403 means the session is valid but the role is wrong — bouncing the user to a sign-in page
    // would be a confusing response to a permission problem.
    session.start({
      token: 'agent-token',
      role: 'Agent',
      agent: { id: 'a', name: 'A', extension: '1', team: 't', skills: {}, state: 'LoggedOut' },
    });

    const navigate = spyOn(router, 'navigate').and.resolveTo(true);

    http.get('/api/v1/wallboard').subscribe({ error: () => undefined });
    backend.expectOne('/api/v1/wallboard').flush({ error: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });

    expect(session.isAuthenticated()).toBeTrue();
    expect(navigate).not.toHaveBeenCalled();
  });
});

describe('describeHttpError', () => {
  it('prefers the API error body so the server speaks for itself', () => {
    // A stale reservation comes back as 409 with a message written for the agent. Showing the
    // server's own words beats a generic "request failed".
    const error = new HttpErrorResponse({
      status: 409,
      statusText: 'Conflict',
      error: { error: 'This call is no longer reserved for you.' },
    });

    expect(describeHttpError(error)).toBe('This call is no longer reserved for you.');
  });

  it('unwraps RFC 7807 validation problems from [ApiController]', () => {
    const error = new HttpErrorResponse({
      status: 400,
      statusText: 'Bad Request',
      error: {
        title: 'One or more validation errors occurred.',
        errors: { ReservationToken: ['The ReservationToken field is required.'] },
      },
    });

    expect(describeHttpError(error)).toBe('The ReservationToken field is required.');
  });

  it('explains a dead connection rather than showing "0 "', () => {
    const error = new HttpErrorResponse({ status: 0, statusText: 'Unknown Error' });

    expect(describeHttpError(error)).toContain('Cannot reach the platform');
  });
});
