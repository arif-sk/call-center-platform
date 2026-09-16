import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService } from '../../core/api.service';
import { describeHttpError } from '../../core/auth.interceptor';
import { RealtimeService } from '../../core/realtime.service';
import { SessionService } from '../../core/session.service';
import { AgentProfile, AgentRole } from '../../core/models';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Sign in</h2>
      <p class="hint">
        Stands in for corporate SSO — production uses OIDC authorization-code + PKCE (NFR-SEC2).
        What matters below this point is that the session is held by the server, there is exactly
        one per agent, and every request re-validates it.
      </p>

      @if (error(); as message) {
        <div class="banner show bad">{{ message }}</div>
      }

      <div class="row">
        <div style="flex:2">
          <label for="agent">Agent</label>
          <select id="agent" [(ngModel)]="selectedAgentId" [disabled]="busy()">
            @for (agent of agents(); track agent.id) {
              <option [value]="agent.id">
                {{ agent.name }} — {{ skillList(agent) }}
              </option>
            }
          </select>
        </div>

        <div style="flex:1">
          <label for="role">Role</label>
          <select id="role" [(ngModel)]="selectedRole" [disabled]="busy()">
            <option value="Agent">Agent</option>
            <option value="Supervisor">Supervisor</option>
          </select>
        </div>

        <button class="primary" type="button" [disabled]="busy() || !selectedAgentId()" (click)="signIn()">
          {{ busy() ? 'Signing in…' : 'Sign in' }}
        </button>
      </div>

      <p class="hint" style="margin-top:14px">
        Sign in as <b>Supervisor</b> to reach the wallboard and the call simulator. Signing in as
        <b>Agent</b> and then visiting /supervisor shows the role check working — the server
        returns 403 whatever the browser does (FR-A2).
      </p>
    </section>

    <div class="spacer"></div>

    <section class="panel">
      <h2>Supervisor console</h2>
      <p class="hint">
        A dedicated account that authenticates and observes but never takes calls — so opening the
        console cannot evict a real agent from their session (FR-B5).
      </p>
      <button type="button" [disabled]="busy() || !supervisorAgentId()" (click)="signInAsSupervisor()">
        Sign in as Supervisor Console
      </button>
    </section>
  `,
  styles: `
    :host { display: block; max-width: 760px; margin: 0 auto; }
  `,
})
export class LoginComponent {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);

  protected readonly agents = signal<AgentProfile[]>([]);
  protected readonly supervisorAgentId = signal<string | null>(null);
  protected readonly selectedAgentId = signal<string>('');
  protected readonly selectedRole = signal<AgentRole>('Agent');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected skillList(agent: AgentProfile): string {
    return Object.keys(agent.skills).join(', ') || 'no skills';
  }

  constructor() {
    this.api.bootstrap().subscribe({
      next: boot => {
        this.agents.set(boot.agents);
        this.supervisorAgentId.set(boot.supervisorAgentId);
        if (boot.agents.length) this.selectedAgentId.set(boot.agents[0].id);
      },
      error: err => this.error.set(describeHttpError(err)),
    });
  }

  protected signIn(): void {
    this.authenticate(this.selectedAgentId(), this.selectedRole(), '/agent');
  }

  protected signInAsSupervisor(): void {
    const id = this.supervisorAgentId();
    if (id) this.authenticate(id, 'Supervisor', '/supervisor');
  }

  private authenticate(agentId: string, role: AgentRole, destination: string): void {
    this.busy.set(true);
    this.error.set(null);

    this.api.login(agentId, role).subscribe({
      next: async response => {
        this.session.start(response);

        try {
          await this.realtime.connect();
          await this.router.navigate([destination]);
        } catch (err) {
          this.error.set('Signed in, but the realtime connection failed. Reload to retry.');
        } finally {
          this.busy.set(false);
        }
      },
      error: err => {
        this.error.set(describeHttpError(err));
        this.busy.set(false);
      },
    });
  }
}
