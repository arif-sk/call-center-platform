import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { QueueStats } from '../../core/models';

@Component({
  selector: 'app-queue-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Queues</h2>
      <p class="hint">Priority first, then oldest first. Billing outranks sales, which outranks support.</p>

      <table>
        <thead>
          <tr>
            <th>Queue</th><th>Waiting</th><th>Longest</th><th>SL %</th>
            <th>Offered</th><th>Answered</th><th>Aband.</th>
          </tr>
        </thead>
        <tbody>
          @for (queue of queues(); track queue.name) {
            <tr>
              <td>
                <b>{{ queue.displayName }}</b>
                <div class="muted mono">{{ queue.name }}</div>
              </td>
              <td>{{ queue.waiting }}</td>
              <td>{{ queue.longestWaitSeconds }}s</td>
              <td [style.color]="queue.serviceLevelPct >= 80 ? 'var(--ok)' : 'var(--bad)'">
                {{ queue.serviceLevelPct }}%
                <span class="muted">/{{ queue.slaThresholdSeconds }}s</span>
              </td>
              <td>{{ queue.offeredToday }}</td>
              <td>{{ queue.answeredToday }}</td>
              <td>{{ queue.abandonedToday }}</td>
            </tr>
          } @empty {
            <tr><td colspan="7" class="muted">connecting…</td></tr>
          }
        </tbody>
      </table>
    </section>
  `,
})
export class QueueTableComponent {
  readonly queues = input<QueueStats[]>([]);
}
