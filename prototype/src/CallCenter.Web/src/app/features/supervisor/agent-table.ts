import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { AgentSnapshot } from '../../core/models';

@Component({
  selector: 'app-agent-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Agents</h2>
      <p class="hint">Server-authoritative state. Sign agents in from the Agent view in other browser tabs.</p>

      <table>
        <thead>
          <tr><th>Agent</th><th>Team</th><th>State</th><th>In state</th><th>Calls</th><th>Skills</th></tr>
        </thead>
        <tbody>
          @for (agent of agents(); track agent.id) {
            <tr>
              <td><b>{{ agent.name }}</b></td>
              <td>{{ agent.team }}</td>
              <td>
                <span class="pill" [class]="agent.state">
                  {{ agent.state }}{{ agent.reason ? ' · ' + agent.reason : '' }}
                </span>
              </td>
              <td>{{ agent.secondsInState }}s</td>
              <td>{{ agent.callsHandled }}</td>
              <td class="muted mono">{{ agent.skills.join(', ') }}</td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="muted">connecting…</td></tr>
          }
        </tbody>
      </table>
    </section>
  `,
})
export class AgentTableComponent {
  readonly agents = input<AgentSnapshot[]>([]);
}
