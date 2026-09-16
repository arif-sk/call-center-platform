import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CallEventView } from '../../core/models';

/**
 * A call replayed from its event stream. This is the view that answers a support ticket, settles a
 * customer dispute, and — unchanged — is what a future AI pipeline consumes (docs §6.2).
 */
@Component({
  selector: 'app-call-trace',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (callId(); as id) {
      <section class="panel">
        <h2>Call trace</h2>
        <p class="hint">Call {{ id }} — {{ events().length }} events, replayed in order.</p>

        <div style="overflow-x:auto">
          <table>
            <thead>
              <tr>
                <th style="width:60px">Seq</th>
                <th style="width:110px">At</th>
                <th style="width:210px">Event</th>
                <th>Payload</th>
              </tr>
            </thead>
            <tbody>
              @for (event of events(); track event.seq) {
                <tr>
                  <td class="mono">{{ event.seq }}</td>
                  <td class="mono">{{ event.occurredAt | date:'HH:mm:ss' }}</td>
                  <td><b>{{ event.type }}</b></td>
                  <td class="mono muted">{{ event.payload }}</td>
                </tr>
              } @empty {
                <tr><td colspan="4" class="muted">loading…</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }
  `,
})
export class CallTraceComponent {
  readonly callId = input<string | null>(null);
  readonly events = input<CallEventView[]>([]);
}
