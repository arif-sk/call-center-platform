import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export interface LogLine { at: Date; event: string; detail: string; }

@Component({
  selector: 'app-session-log',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Session log</h2>
      <p class="hint">Everything this desktop received over the realtime connection.</p>

      <div class="log">
        @for (line of lines(); track line.at.getTime() + line.event) {
          <div>
            <span class="t">{{ line.at.toLocaleTimeString() }}</span>
            <span class="e">{{ line.event }}</span>
            {{ line.detail }}
          </div>
        } @empty {
          <div class="t">Waiting for events…</div>
        }
      </div>
    </section>
  `,
})
export class SessionLogComponent {
  readonly lines = input<LogLine[]>([]);
}
