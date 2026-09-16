import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription, interval, startWith, switchMap } from 'rxjs';

import { ApiService } from '../../core/api.service';
import { describeHttpError } from '../../core/auth.interceptor';
import { RealtimeService } from '../../core/realtime.service';
import { CallEventMessage, CallEventView, CallView, WallboardSnapshot } from '../../core/models';

import { AgentTableComponent } from './agent-table';
import { CallListComponent } from './call-list';
import { CallSimulatorComponent, SimulatedCall } from './call-simulator';
import { CallTraceComponent } from './call-trace';
import { EventLogComponent } from './event-log';
import { KpiStripComponent } from './kpi-strip';
import { QueueTableComponent } from './queue-table';

type Banner = { kind: 'info' | 'warn' | 'bad'; text: string } | null;

/**
 * The supervisor console.
 *
 * Note the two different update mechanisms, which is the design point rather than an inconsistency:
 * the wallboard arrives **pushed** at a fixed 1 Hz already aggregated server-side, while the call
 * list is **polled** every few seconds because it is a report, not a live metric. Pushing every
 * call change to every supervisor is the design that stops scaling at a few hundred agents
 * (docs §5.2).
 */
@Component({
  selector: 'app-supervisor-console',
  imports: [
    KpiStripComponent, CallSimulatorComponent, QueueTableComponent,
    AgentTableComponent, EventLogComponent, CallListComponent, CallTraceComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (banner(); as message) {
      <div class="banner show" [class]="message.kind">{{ message.text }}</div>
    }

    <app-kpi-strip [board]="board()" />

    <div class="spacer"></div>

    <div class="grid two">
      <app-call-simulator
        [dids]="dids()"
        [crmDown]="crmDown()"
        (placeCall)="placeInbound($event)"
        (burst)="placeBurst($event)"
        (toggleCrm)="toggleCrm($event)" />

      <app-queue-table [queues]="board()?.queues ?? []" />
    </div>

    <div class="spacer"></div>

    <div class="grid two">
      <app-agent-table [agents]="board()?.agents ?? []" />
      <app-event-log [events]="events()" />
    </div>

    <div class="spacer"></div>

    <app-call-list
      [calls]="calls()"
      [selectedId]="selectedCallId()"
      (selected)="showTrace($event)" />

    <div class="spacer"></div>

    <app-call-trace [callId]="selectedCallId()" [events]="trace()" />
  `,
})
export class SupervisorConsoleComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);

  protected readonly board = signal<WallboardSnapshot | null>(null);
  protected readonly events = signal<CallEventMessage[]>([]);
  protected readonly calls = signal<CallView[]>([]);
  protected readonly trace = signal<CallEventView[]>([]);
  protected readonly selectedCallId = signal<string | null>(null);
  protected readonly dids = signal<Record<string, string>>({});
  protected readonly crmDown = signal(false);
  protected readonly banner = signal<Banner>(null);

  private traceSubscription: Subscription | null = null;

  constructor() {
    // Pushed at 1 Hz, already aggregated (docs §4.3.4).
    this.realtime.wallboard$.pipe(takeUntilDestroyed()).subscribe(snapshot => {
      this.board.set(snapshot);
      this.crmDown.set(snapshot.crmCircuitOpen || this.crmDown());
    });

    this.realtime.callEvent$.pipe(takeUntilDestroyed()).subscribe(event => {
      this.events.update(list => [event, ...list].slice(0, 300));
    });

    // Polled: the call list is a report, not a live metric.
    interval(2500)
      .pipe(startWith(0), switchMap(() => this.api.recentCalls()), takeUntilDestroyed())
      .subscribe({
        next: calls => this.calls.set(calls),
        error: err => this.show('bad', describeHttpError(err)),
      });
  }

  ngOnInit(): void {
    this.api.bootstrap().subscribe({
      next: boot => this.dids.set(boot.dids),
      error: err => this.show('bad', describeHttpError(err)),
    });

    // A restored session arrives here without a live hub connection.
    void this.realtime.connect().catch(() =>
      this.show('bad', 'Realtime connection failed — the wallboard will not update.'),
    );

    // First paint comes from the REST endpoint so the console is not blank for up to a second.
    this.api.wallboard().subscribe({
      next: snapshot => this.board.set(snapshot),
      error: err => this.show('bad', describeHttpError(err)),
    });
  }

  // ---------------------------------------------------------------- actions

  protected placeInbound(call: SimulatedCall): void {
    this.api.simulateInbound(call.did, call.from, call.patienceSeconds).subscribe({
      error: err => this.show('bad', describeHttpError(err)),
    });
  }

  protected placeBurst(count: number): void {
    // Sequential rather than parallel so arrival order is deterministic and the priority ordering
    // is actually observable in the queue table.
    const next = (remaining: number): void => {
      if (remaining === 0) {
        this.show('info', `${count} calls injected — higher-priority queues are served first (FR-C5).`);
        return;
      }

      this.api.simulateInbound('+442045550100', '+447700900001', 90).subscribe({
        next: () => next(remaining - 1),
        error: err => this.show('bad', describeHttpError(err)),
      });
    };

    next(count);
  }

  protected toggleCrm(enabled: boolean): void {
    this.api.setCrmOutage(enabled).subscribe({
      next: result => {
        this.crmDown.set(result.crmFailing);
        this.show(result.crmFailing ? 'warn' : 'info', result.crmFailing
          ? 'CRM is failing. Place a call — it still routes and connects; only the screen-pop degrades.'
          : 'CRM restored.');
      },
      error: err => this.show('bad', describeHttpError(err)),
    });
  }

  protected showTrace(callId: string): void {
    this.selectedCallId.set(callId);
    this.trace.set([]);

    this.traceSubscription?.unsubscribe();
    this.traceSubscription = this.api.trace(callId).subscribe({
      next: events => this.trace.set(events),
      error: err => this.show('bad', describeHttpError(err)),
    });
  }

  private show(kind: 'info' | 'warn' | 'bad', text: string): void {
    this.banner.set({ kind, text });
    setTimeout(() => this.banner.set(null), 6000);
  }
}
