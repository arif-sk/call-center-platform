import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal,
} from '@angular/core';
import { CallOfferedMessage } from '../../core/models';

/**
 * The incoming-call panel.
 *
 * The countdown is the important detail: the reservation has a server-side TTL, and when it expires
 * the platform treats it as ring-no-answer — re-queues the caller with a priority boost and takes
 * this agent out of routing (FR-C7). Showing the agent exactly how long they have is the difference
 * between "the system stole my call" and "I ran out of time".
 */
@Component({
  selector: 'app-incoming-call',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (offer(); as call) {
      <section class="panel offer">
        <h2>Incoming call</h2>

        <dl class="dl">
          <dt>From</dt><dd class="mono">{{ call.from }}</dd>
          <dt>Queue</dt><dd>{{ call.queue }}</dd>
          <dt>Waited</dt><dd>{{ call.waitSeconds }}s in queue</dd>
          <dt>Expires in</dt><dd class="countdown">{{ secondsLeft() }}s</dd>
        </dl>

        <div class="spacer"></div>

        <div class="row">
          <button class="good" type="button" [disabled]="busy()" (click)="answer.emit()">Answer</button>
          <button class="danger" type="button" [disabled]="busy()" (click)="reject.emit()">Reject</button>
        </div>

        <p class="hint" style="margin-top:10px">
          Let the countdown run out and you will be set Not&nbsp;Ready with reason <b>RNA</b> while
          the caller is re-queued with a priority boost (FR-C7).
        </p>
      </section>
    }
  `,
})
export class IncomingCallComponent {
  private readonly destroyRef = inject(DestroyRef);

  readonly offer = input<CallOfferedMessage | null>(null);
  readonly busy = input(false);

  readonly answer = output<void>();
  readonly reject = output<void>();

  private readonly now = signal(Date.now());

  readonly secondsLeft = computed(() => {
    const call = this.offer();
    if (!call) return 0;

    return Math.max(0, Math.round((new Date(call.expiresAt).getTime() - this.now()) / 1000));
  });

  constructor() {
    const ticker = setInterval(() => this.now.set(Date.now()), 250);
    this.destroyRef.onDestroy(() => clearInterval(ticker));
  }
}
