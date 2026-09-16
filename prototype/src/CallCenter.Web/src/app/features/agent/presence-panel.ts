import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentProfile, AgentState } from '../../core/models';

/**
 * Presence controls.
 *
 * The buttons request; the server decides (FR-B1). Trying to go Not Ready while on a call is
 * refused server-side and surfaces here as a message rather than being prevented in the UI — the
 * rule lives in one place, and the client is not the one enforcing it.
 */
@Component({
  selector: 'app-presence-panel',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Presence</h2>

      <div class="row" style="align-items:center">
        <div style="flex:2">
          <div class="name">{{ agent()?.name ?? '—' }}</div>
          <div class="muted mono">{{ meta() }}</div>
        </div>
        <div class="right" style="flex:1">
          <span class="pill" [class]="state()">{{ stateLabel() }}</span>
          <div class="muted mono">{{ secondsInState() }}s in state</div>
        </div>
      </div>

      <div class="spacer"></div>

      <div class="row">
        <button class="good" type="button" (click)="goAvailable.emit()">Available</button>
        <select [(ngModel)]="reason" style="max-width:150px" aria-label="Not ready reason">
          @for (option of reasons(); track option) {
            <option [value]="option">{{ option }}</option>
          }
        </select>
        <button type="button" (click)="goNotReady.emit(reason())">Not ready</button>
      </div>

      <p class="hint" style="margin-top:10px">
        The server decides, not the client. Try "Not ready" while on a call — it is refused
        (FR-B1). Close this tab while Available and the platform removes you from routing within
        45 s (FR-B4).
      </p>
    </section>
  `,
  styles: `.name { font-size: 18px; font-weight: 700; }`,
})
export class PresencePanelComponent {
  readonly agent = input<AgentProfile | null>(null);
  readonly state = input<AgentState>('LoggedOut');
  readonly stateReason = input<string | null>(null);
  readonly secondsInState = input(0);
  readonly reasons = input<string[]>([]);

  readonly goAvailable = output<void>();
  readonly goNotReady = output<string>();

  protected readonly reason = signal('Break');

  protected stateLabel(): string {
    const reason = this.stateReason();
    return reason ? `${this.state()} · ${reason}` : this.state();
  }

  protected meta(): string {
    const agent = this.agent();
    if (!agent) return '—';

    const skills = Object.entries(agent.skills).map(([name, level]) => `${name}(${level})`).join(' ');
    return `ext ${agent.extension} · ${agent.team} · ${skills || 'no skills'}`;
  }
}
