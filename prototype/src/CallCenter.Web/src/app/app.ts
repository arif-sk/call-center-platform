import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ApiService } from './core/api.service';
import { RealtimeService } from './core/realtime.service';
import { SessionService } from './core/session.service';

/**
 * Application shell: identity, navigation, and the connection indicator.
 *
 * The indicator is not decoration. An agent must be able to tell at a glance that they are not
 * receiving calls (NFR-U4) — a softphone that has silently stopped listening looks exactly like a
 * quiet queue, and the agent finds out when their supervisor asks why they missed six calls.
 */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header>
      <h1>Call Center Platform</h1>

      <span class="conn" [class]="realtime.status()">
        <span class="status-dot" [class.up]="realtime.status() === 'connected'"></span>
        {{ connectionLabel() }}
      </span>

      @if (session.agent(); as agent) {
        <span class="who">{{ agent.name }} · {{ session.role() }}</span>
      }

      <nav>
        @if (session.isAuthenticated()) {
          <a routerLink="/agent" routerLinkActive="active">Agent</a>

          @if (session.isSupervisor()) {
            <a routerLink="/supervisor" routerLinkActive="active">Supervisor</a>
          }

          <a href="/swagger" target="_blank" rel="noopener">API</a>
          <button type="button" class="link" (click)="signOut()">Sign out</button>
        }
      </nav>
    </header>

    <main>
      <router-outlet />
    </main>
  `,
  styles: `
    header {
      background: #0f172a;
      color: #e2e8f0;
      padding: 12px 20px;
      display: flex;
      align-items: center;
      gap: 16px;
      flex-wrap: wrap;
    }
    h1 { font-size: 15px; margin: 0; font-weight: 600; }
    .conn { font-size: 12px; color: #94a3b8; }
    .conn.disconnected, .conn.reconnecting { color: #fca5a5; font-weight: 600; }
    .who { font-size: 12px; color: #94a3b8; }
    nav { margin-left: auto; display: flex; gap: 14px; align-items: center; }
    nav a, nav .link {
      color: #cbd5e1; text-decoration: none; font-size: 13px;
      background: none; border: 0; cursor: pointer; padding: 0; font-family: inherit;
    }
    nav a:hover, nav a.active, nav .link:hover { color: #fff; text-decoration: underline; }
    main { padding: 20px; max-width: 1320px; margin: 0 auto; }
  `,
})
export class App {
  protected readonly session = inject(SessionService);
  protected readonly realtime = inject(RealtimeService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  protected connectionLabel(): string {
    switch (this.realtime.status()) {
      case 'connected': return 'connected';
      case 'connecting': return 'connecting…';
      case 'reconnecting': return 'reconnecting — not receiving calls';
      default: return 'offline — not receiving calls';
    }
  }

  protected signOut(): void {
    // Tell the server first so the session is actually revoked, then tear down locally. If the
    // request fails we still sign out on this device — the server-side session expires on its own.
    this.api.logout().subscribe({
      next: () => this.finishSignOut(),
      error: () => this.finishSignOut(),
    });
  }

  private finishSignOut(): void {
    void this.realtime.disconnect();
    this.session.clear();
    void this.router.navigate(['/login']);
  }
}
