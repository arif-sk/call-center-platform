import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { CallCenterService } from './call-center.service';

const DISPOSITIONS = ['Resolved', 'Escalated', 'Follow-up required', 'Wrong number', 'No issue'];

/**
 * The agent's one screen. What it shows is decided entirely by the state the server says the
 * agent is in, so the desktop cannot disagree with the platform about what is happening.
 */
@Component({
  selector: 'app-agent-desktop',
  imports: [FormsModule, RouterLink],
  template: `
    @if (cc.me(); as me) {
      <section class="panel">
        <div class="row between">
          <div>
            <h1>{{ me.name }}</h1>
            <p class="muted">Extension {{ me.extension }}</p>
          </div>
          <div class="row">
            <span class="pill big" [class]="'state-' + me.state">{{ label(me.state) }}</span>
            <span class="muted">{{ duration(me.stateChangedAt) }}</span>
          </div>
        </div>

        <div class="row gap">
          @if (me.state === 'Available') {
            <button type="button" class="secondary" (click)="cc.setReady(me.id, false)">Go not ready</button>
          } @else if (me.state === 'NotReady' || me.state === 'Offline') {
            <button type="button" class="primary" (click)="cc.setReady(me.id, true)">Go ready</button>
          }
          <button type="button" class="ghost" (click)="signOut(me.id)">Sign out</button>
        </div>
      </section>

      @if (cc.myCall(); as call) {
        @if (call.status === 'Ringing') {
          <section class="panel ringing">
            <h2>Incoming call</h2>
            <p class="number">{{ call.from }}</p>
            <p class="muted">Waiting {{ duration(call.queuedAt) }}</p>
            <div class="row gap">
              <button type="button" class="primary" (click)="cc.answer(call.id, me.id)">Answer</button>
              <button type="button" class="danger" (click)="cc.decline(call.id, me.id)">Decline</button>
            </div>
          </section>
        } @else if (call.status === 'Connected') {
          <section class="panel connected">
            <h2>On a call</h2>
            <p class="number">{{ call.from }}</p>
            <p class="muted">Talking for {{ duration(call.answeredAt) }}</p>
            <button type="button" class="danger" (click)="cc.hangUp(call.id, me.id)">Hang up</button>
          </section>
        } @else if (call.status === 'WrapUp') {
          <section class="panel">
            <h2>Wrap up</h2>
            <p class="muted">{{ call.from }} · talked for {{ call.talkSeconds }}s</p>

            <label for="disposition">How did it end?</label>
            <select id="disposition" [(ngModel)]="disposition">
              <option value="">Choose one…</option>
              @for (option of dispositions; track option) {
                <option [value]="option">{{ option }}</option>
              }
            </select>

            <label for="notes">Notes (optional)</label>
            <textarea id="notes" rows="3" [(ngModel)]="notes"></textarea>

            <button type="button" class="primary" [disabled]="!disposition" (click)="finish(call.id, me.id)">
              Finish and go available
            </button>
          </section>
        }
      } @else {
        <section class="panel idle">
          @if (me.state === 'Available') {
            <h2>Ready for the next call</h2>
            <p class="muted">{{ cc.snapshot()?.stats?.waiting ?? 0 }} caller(s) waiting.</p>
          } @else {
            <h2>Not taking calls</h2>
            <p class="muted">Press “Go ready” when you are set up.</p>
          }
        </section>
      }

      <section class="panel">
        <h2>Team</h2>
        <table>
          <thead>
            <tr><th>Agent</th><th>State</th><th>For</th></tr>
          </thead>
          <tbody>
            @for (agent of cc.agents(); track agent.id) {
              <tr [class.self]="agent.id === me.id">
                <td>{{ agent.name }}</td>
                <td><span class="pill" [class]="'state-' + agent.state">{{ label(agent.state) }}</span></td>
                <td class="muted">{{ duration(agent.stateChangedAt) }}</td>
              </tr>
            }
          </tbody>
        </table>
      </section>
    } @else {
      <section class="panel narrow">
        <h1>Not signed in</h1>
        <p class="muted">Choose a seat before opening the desktop.</p>
        <a routerLink="/login" class="button primary">Go to sign in</a>
      </section>
    }
  `,
})
export class AgentDesktopComponent {
  protected readonly cc = inject(CallCenterService);
  private readonly router = inject(Router);

  protected readonly dispositions = DISPOSITIONS;
  protected disposition = '';
  protected notes = '';

  protected label(state: string): string {
    return state === 'NotReady' ? 'Not ready' : state === 'OnCall' ? 'On call'
      : state === 'WrapUp' ? 'Wrap up' : state;
  }

  protected duration(since: string | null): string {
    const total = this.cc.elapsed(since);
    const minutes = Math.floor(total / 60);
    return `${minutes}:${String(total % 60).padStart(2, '0')}`;
  }

  protected async finish(callId: string, agentId: string): Promise<void> {
    if (await this.cc.wrapUp(callId, agentId, this.disposition, this.notes)) {
      this.disposition = '';
      this.notes = '';
    }
  }

  protected async signOut(agentId: string): Promise<void> {
    if (await this.cc.signOut(agentId)) {
      this.cc.setAgentId(null);
      await this.router.navigate(['/login']);
    }
  }
}
