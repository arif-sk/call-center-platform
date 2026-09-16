import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

export interface SimulatedCall {
  did: string;
  from: string;
  patienceSeconds: number;
}

/**
 * Demo controls that stand in for carrier webhooks.
 *
 * This panel only works because a second implementation of `ITelephonyProvider` exists — the same
 * decision that makes CI and load testing free (MVP §3.5). With a real carrier configured the
 * server removes the simulator controller from its routing table entirely and these calls 404,
 * which is why every button here degrades to a visible error rather than silent nothing.
 */
@Component({
  selector: 'app-call-simulator',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Call simulator</h2>
      <p class="hint">
        Stands in for a carrier webhook. The simulated provider is the second implementation of
        <span class="mono">ITelephonyProvider</span> — it is what makes CI, load tests and this
        demo cost nothing (MVP §3.5).
      </p>

      <div class="row">
        <div style="flex:1">
          <label for="did">Dialled number (DID)</label>
          <select id="did" [(ngModel)]="did">
            @for (entry of didEntries(); track entry[0]) {
              <option [value]="entry[0]">{{ entry[0] }} → {{ entry[1] }}</option>
            }
          </select>
        </div>

        <div style="flex:1">
          <label for="from">Caller</label>
          <select id="from" [(ngModel)]="from">
            <option value="+447700900001">+447700900001 — Priya Raman (known)</option>
            <option value="+447700900003">+447700900003 — Sofia Marchetti (known)</option>
            <option value="+447700900004">+447700900004 — ambiguous (two contacts)</option>
            <option value="+447700900555">+447700900555 — unknown number</option>
          </select>
        </div>

        <div style="flex:0 0 130px">
          <label for="patience">Patience (s)</label>
          <input id="patience" type="number" min="3" [(ngModel)]="patience">
        </div>

        <button class="primary" type="button" (click)="place()">Place inbound call</button>
      </div>

      <div class="spacer"></div>

      <div class="row">
        <button type="button" (click)="burst.emit(5)">Burst: 5 calls</button>
        <button type="button" (click)="placeImpatient()">Impatient caller (5 s)</button>
        <button type="button" [class.danger]="crmDown()" (click)="toggleCrm.emit(!crmDown())">
          {{ crmDown() ? 'Restore the CRM' : 'Break the CRM' }}
        </button>
      </div>

      <p class="hint" style="margin-top:10px">
        "Break the CRM" proves the rule the integration is designed around: the CRM is not allowed
        to take the phones down (FR-F5). Calls keep routing; only the screen-pop degrades.
      </p>
    </section>
  `,
})
export class CallSimulatorComponent {
  readonly dids = input<Record<string, string>>({});
  readonly crmDown = input(false);

  readonly placeCall = output<SimulatedCall>();
  readonly burst = output<number>();
  readonly toggleCrm = output<boolean>();

  protected readonly did = signal('+442045550100');
  protected readonly from = signal('+447700900001');
  protected readonly patience = signal(90);

  protected didEntries(): [string, string][] {
    return Object.entries(this.dids());
  }

  protected place(): void {
    this.placeCall.emit({ did: this.did(), from: this.from(), patienceSeconds: this.patience() });
  }

  /** Short patience so the abandon path (FR-C8) is demonstrable without waiting 90 seconds. */
  protected placeImpatient(): void {
    this.placeCall.emit({ did: this.did(), from: this.from(), patienceSeconds: 5 });
  }
}
