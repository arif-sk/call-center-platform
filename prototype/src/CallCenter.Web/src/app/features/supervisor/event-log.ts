import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CallEventMessage } from '../../core/models';

@Component({
  selector: 'app-event-log',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Interaction event stream</h2>
      <p class="hint">
        The system of record (FR-I1). Reporting, audit and every future AI feature read from this,
        not from a CRUD table — which is why it is in v1 (AI-Readiness §6.2).
      </p>

      <div class="log">
        @for (event of events(); track $index) {
          <div>
            <span class="t">{{ event.occurredAt | date:'HH:mm:ss' }}</span>
            <span class="e">{{ event.type }}</span>
            <span class="t">{{ event.callId.slice(0, 8) }}</span>
            {{ event.payload }}
          </div>
        } @empty {
          <div class="t">Waiting for events…</div>
        }
      </div>
    </section>
  `,
})
export class EventLogComponent {
  readonly events = input<CallEventMessage[]>([]);
}
