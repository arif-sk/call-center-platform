import { Injectable, computed, signal } from '@angular/core';
import { AgentProfile, AgentRole, SessionResponse } from './models';

const STORAGE_KEY = 'callcenter.session';

interface StoredSession {
  token: string;
  role: AgentRole;
  agent: AgentProfile;
}

/**
 * Holds who is signed in. Signals rather than a BehaviorSubject because this is *state* the
 * templates read directly — reserve RxJS for the things that are genuinely streams (see
 * RealtimeService).
 *
 * Persisted to sessionStorage so an F5 does not sign the agent out mid-shift. That is a
 * convenience only: the token is meaningless without the server-side session behind it, and the
 * server evicts a previous session when the same agent signs in elsewhere (FR-B5).
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly session = signal<StoredSession | null>(this.restore());

  readonly token = computed(() => this.session()?.token ?? null);
  readonly agent = computed(() => this.session()?.agent ?? null);
  readonly role = computed<AgentRole | null>(() => this.session()?.role ?? null);
  readonly isAuthenticated = computed(() => this.session() !== null);

  /** Roles are hierarchical server-side: Admin ⊃ Supervisor ⊃ Agent. Mirrored here for the UI. */
  readonly isSupervisor = computed(() => {
    const role = this.role();
    return role === 'Supervisor' || role === 'Admin';
  });

  start(response: SessionResponse): void {
    const stored: StoredSession = {
      token: response.token,
      role: response.role,
      agent: response.agent,
    };

    this.session.set(stored);
    this.persist(stored);
  }

  clear(): void {
    this.session.set(null);
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      // Private browsing or blocked storage — the in-memory signal is the source of truth anyway.
    }
  }

  private persist(stored: StoredSession): void {
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(stored));
    } catch {
      // Non-fatal: the agent simply has to sign in again after a refresh.
    }
  }

  private restore(): StoredSession | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      return raw ? (JSON.parse(raw) as StoredSession) : null;
    } catch {
      return null;
    }
  }
}
