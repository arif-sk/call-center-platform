import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { WallboardSnapshot } from '../../core/models';

@Component({
  selector: 'app-kpi-strip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel">
      <h2>Live</h2>
      <p class="hint">
        Aggregated server-side and pushed at 1&nbsp;Hz, not forwarded per event — the naive design
        is ~25,000 messages/second at 500 agents (Scalability §5.2). These figures are deliberately
        approximate; exact numbers come from the event stream in reporting.
      </p>

      <div class="kpi">
        <div><div class="v">{{ board()?.liveCalls ?? 0 }}</div><div class="l">live calls</div></div>
        <div><div class="v">{{ waiting() }}</div><div class="l">waiting</div></div>
        <div><div class="v">{{ counts()['Available'] ?? 0 }}</div><div class="l">available</div></div>
        <div><div class="v">{{ onCall() }}</div><div class="l">on call</div></div>
        <div><div class="v">{{ longestWait() }}s</div><div class="l">longest wait</div></div>
        <div><div class="v">{{ board()?.provider ?? '—' }}</div><div class="l">provider</div></div>
        <div>
          <div class="v" [style.color]="board()?.crmCircuitOpen ? 'var(--bad)' : 'var(--ok)'">
            {{ board()?.crmCircuitOpen ? 'circuit open' : 'ok' }}
          </div>
          <div class="l">crm</div>
        </div>
      </div>
    </section>
  `,
})
export class KpiStripComponent {
  readonly board = input<WallboardSnapshot | null>(null);

  protected readonly counts = computed(() => this.board()?.agentStateCounts ?? {});
  protected readonly waiting = computed(() =>
    (this.board()?.queues ?? []).reduce((total, q) => total + q.waiting, 0));
  protected readonly longestWait = computed(() =>
    (this.board()?.queues ?? []).reduce((max, q) => Math.max(max, q.longestWaitSeconds), 0));
  protected readonly onCall = computed(() => {
    const counts = this.counts();
    return (counts['OnCall'] ?? 0) + (counts['Ringing'] ?? 0);
  });
}
