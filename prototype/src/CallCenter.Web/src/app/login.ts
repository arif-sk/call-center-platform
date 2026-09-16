import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { AgentView, CallCenterService } from './call-center.service';

/**
 * Pick a name and start. There is no password: authentication belongs in the company's existing
 * identity provider, and the design document covers how that is wired in.
 */
@Component({
  selector: 'app-login',
  template: `
    <section class="panel narrow">
      <h1>Who is signing in?</h1>
      <p class="muted">Choose a seat to open the desktop. Supervisors get the live console.</p>

      <ul class="seat-list">
        @for (agent of cc.agents(); track agent.id) {
          <li>
            <button type="button" class="seat" (click)="choose(agent)">
              <span class="seat-name">{{ agent.name }}</span>
              <span class="muted">Ext {{ agent.extension }}</span>
              @if (agent.isSupervisor) {
                <span class="tag">Supervisor</span>
              }
              <span class="pill" [class]="'state-' + agent.state">{{ agent.state }}</span>
            </button>
          </li>
        } @empty {
          <li class="muted">Loading seats…</li>
        }
      </ul>
    </section>
  `,
})
export class LoginComponent {
  protected readonly cc = inject(CallCenterService);
  private readonly router = inject(Router);

  protected async choose(agent: AgentView): Promise<void> {
    this.cc.setAgentId(agent.id);
    if (await this.cc.signIn(agent.id)) {
      await this.router.navigate([agent.isSupervisor ? '/supervisor' : '/agent']);
    }
  }
}
