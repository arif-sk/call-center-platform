import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';

export interface ActiveCall {
  id: string;
  party: string;
  startedAt: number;
  onHold: boolean;
  recording: boolean;
  recordingPaused: boolean;
}

@Component({
  selector: 'app-active-call',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (call(); as active) {
      <section class="panel">
        <h2>Active call</h2>

        <dl class="dl">
          <dt>Call</dt><dd class="mono">{{ active.id }}</dd>
          <dt>Party</dt><dd class="mono">{{ active.party }}</dd>
          <dt>Duration</dt><dd>{{ duration() }}</dd>
          <dt>Recording</dt><dd>{{ recordingLabel(active) }}</dd>
        </dl>

        <div class="spacer"></div>

        <div class="row">
          <button type="button" (click)="toggleHold.emit()">
            {{ active.onHold ? 'Retrieve' : 'Hold' }}
          </button>
          <button type="button" (click)="toggleRecording.emit()" [disabled]="!active.recording">
            {{ active.recordingPaused ? 'Resume recording' : 'Pause recording' }}
          </button>
          <button type="button" (click)="transfer.emit()">Blind transfer</button>
          <button class="danger" type="button" (click)="hangup.emit()">Hang up</button>
        </div>

        <div class="spacer"></div>

        <div class="row">
          <div style="flex:2">
            <label for="dtmf">Send DTMF</label>
            <input id="dtmf" [(ngModel)]="digits" placeholder="e.g. 1234#" autocomplete="off">
          </div>
          <button type="button" [disabled]="!digits()" (click)="sendDigits()">Send</button>
        </div>

        <p class="hint" style="margin-top:10px">
          "Pause recording" is the PCI control (FR-G3). The paused range is journalled so the gap
          can be proven to an auditor; the digits themselves never reach the event stream or the
          logs (NFR-SEC6).
        </p>
      </section>
    }
  `,
})
export class ActiveCallComponent {
  private readonly destroyRef = inject(DestroyRef);

  readonly call = input<ActiveCall | null>(null);

  readonly toggleHold = output<void>();
  readonly toggleRecording = output<void>();
  readonly transfer = output<void>();
  readonly hangup = output<void>();
  readonly dtmf = output<string>();

  protected readonly digits = signal('');
  private readonly now = signal(Date.now());

  protected readonly duration = computed(() => {
    const active = this.call();
    if (!active) return '00:00';

    const seconds = Math.max(0, Math.round((this.now() - active.startedAt) / 1000));
    const mm = String(Math.floor(seconds / 60)).padStart(2, '0');
    const ss = String(seconds % 60).padStart(2, '0');
    return `${mm}:${ss}`;
  });

  constructor() {
    const ticker = setInterval(() => this.now.set(Date.now()), 500);
    this.destroyRef.onDestroy(() => clearInterval(ticker));
  }

  protected recordingLabel(active: ActiveCall): string {
    if (!active.recording) return 'not recorded';
    return active.recordingPaused ? 'paused (PCI)' : 'recording';
  }

  protected sendDigits(): void {
    const value = this.digits();
    if (!value) return;

    this.dtmf.emit(value);
    this.digits.set('');
  }
}
