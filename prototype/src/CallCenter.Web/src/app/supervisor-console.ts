import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CallCenterService } from './call-center.service';

/**
 * What a supervisor watches: how many people are waiting, how long the worst wait is, and who is
 * free. The call simulator stands in for the telephone network so the whole flow can be
 * demonstrated without a carrier account.
 */
@Component({
  selector: 'app-supervisor-console',
  imports: [FormsModule],
  template: `
    <section class="stats">
      <div class="stat"><span class="figure">{{ stats().waiting }}</span><span>Waiting</span></div>
      <div class="stat" [class.warn]="stats().longestWaitSeconds > 30">
        <span class="figure">{{ stats().longestWaitSeconds }}s</span><span>Longest wait</span>
      </div>
      <div class="stat"><span class="figure">{{ stats().available }}</span><span>Available</span></div>
      <div class="stat"><span class="figure">{{ stats().onCall }}</span><span>On a call</span></div>
      <div class="stat"><span class="figure">{{ stats().completed }}</span><span>Completed</span></div>
    </section>

    <section class="panel">
      <h2>Call simulator</h2>
      <p class="muted">Stands in for the phone network. Press the button to make a customer call in.</p>
      <div class="row gap">
        <input [(ngModel)]="from" placeholder="+1 555 000 0000" aria-label="Caller number" />
        <button type="button" class="primary" (click)="cc.simulateInbound(from)">Call in</button>
      </div>
    </section>

    <section class="panel">
      <h2>Live calls</h2>
      @if (cc.snapshot()?.queue?.length) {
        <table>
          <thead>
            <tr><th>From</th><th>Status</th><th>Agent</th><th>Waiting</th><th></th></tr>
          </thead>
          <tbody>
            @for (call of cc.snapshot()!.queue; track call.id) {
              <tr>
                <td>{{ call.from }}</td>
                <td><span class="pill" [class]="'call-' + call.status">{{ call.status }}</span></td>
                <td>{{ call.agentName ?? '—' }}</td>
                <td>{{ cc.elapsed(call.queuedAt) }}s</td>
                <td>
                  @if (call.status === 'Queued') {
                    <button type="button" class="ghost small" (click)="cc.abandon(call.id)">Caller hangs up</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="muted">Nothing in progress.</p>
      }
    </section>

    <section class="panel">
      <h2>Agents</h2>
      <table>
        <thead>
          <tr><th>Agent</th><th>Ext</th><th>State</th><th>For</th></tr>
        </thead>
        <tbody>
          @for (agent of cc.agents(); track agent.id) {
            <tr>
              <td>{{ agent.name }}</td>
              <td class="muted">{{ agent.extension }}</td>
              <td><span class="pill" [class]="'state-' + agent.state">{{ agent.state }}</span></td>
              <td class="muted">{{ cc.elapsed(agent.stateChangedAt) }}s</td>
            </tr>
          }
        </tbody>
      </table>
    </section>

    <section class="panel">
      <h2>Recently finished</h2>
      @if (cc.snapshot()?.recent?.length) {
        <table>
          <thead>
            <tr><th>From</th><th>Outcome</th><th>Agent</th><th>Waited</th><th>Talked</th></tr>
          </thead>
          <tbody>
            @for (call of cc.snapshot()!.recent; track call.id) {
              <tr>
                <td>{{ call.from }}</td>
                <td>{{ call.disposition ?? call.status }}</td>
                <td>{{ call.agentName ?? '—' }}</td>
                <td class="muted">{{ call.waitSeconds }}s</td>
                <td class="muted">{{ call.talkSeconds }}s</td>
              </tr>
            }
          </tbody>
        </table>
      } @else {
        <p class="muted">No calls have finished yet.</p>
      }
    </section>
  `,
})
export class SupervisorConsoleComponent {
  protected readonly cc = inject(CallCenterService);
  protected from = '+15551234567';

  protected stats() {
    return this.cc.snapshot()?.stats ?? { waiting: 0, available: 0, onCall: 0, completed: 0, longestWaitSeconds: 0 };
  }
}
