import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';

import { ApiService } from '../../core/api.service';
import { describeHttpError } from '../../core/auth.interceptor';
import { RealtimeService } from '../../core/realtime.service';
import { SessionService } from '../../core/session.service';
import { AgentState, CallOfferedMessage, ScreenPopMessage } from '../../core/models';

import { ActiveCall, ActiveCallComponent } from './active-call';
import { IncomingCallComponent } from './incoming-call';
import { OutboundDialerComponent } from './outbound-dialer';
import { PresencePanelComponent } from './presence-panel';
import { ScreenPopComponent } from './screen-pop';
import { LogLine, SessionLogComponent } from './session-log';
import { Disposition, WrapUpFormComponent } from './wrap-up-form';

type Banner = { kind: 'info' | 'warn' | 'bad'; text: string } | null;

/**
 * The agent desktop container.
 *
 * It owns all state and talks to the services; every child is presentational, taking inputs and
 * emitting outputs. That split is what makes the interesting parts testable without a browser —
 * the countdown, the screen-pop outcomes and the wrap-up rules are all pure component logic.
 */
@Component({
  selector: 'app-agent-desktop',
  imports: [
    PresencePanelComponent, IncomingCallComponent, ActiveCallComponent,
    WrapUpFormComponent, OutboundDialerComponent, ScreenPopComponent, SessionLogComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (banner(); as message) {
      <div class="banner show" [class]="message.kind">{{ message.text }}</div>
    }

    <div class="grid two">
      <div class="stack">
        <app-presence-panel
          [agent]="session.agent()"
          [state]="state()"
          [stateReason]="stateReason()"
          [secondsInState]="secondsInState()"
          [reasons]="notReadyReasons()"
          (goAvailable)="setState('Available')"
          (goNotReady)="setState('NotReady', $event)" />

        <app-incoming-call
          [offer]="offer()"
          [busy]="busy()"
          (answer)="answer()"
          (reject)="reject()" />

        <app-active-call
          [call]="activeCall()"
          (toggleHold)="toggleHold()"
          (toggleRecording)="toggleRecording()"
          (transfer)="transfer()"
          (hangup)="hangup()"
          (dtmf)="sendDtmf($event)" />

        @if (wrapUpCallId()) {
          <app-wrap-up-form
            [options]="dispositions()"
            (submitted)="submitDisposition($event)" />
        }

        <app-outbound-dialer
          [disabled]="state() !== 'Available'"
          (dial)="placeOutbound($event)" />
      </div>

      <div class="stack">
        <app-screen-pop [pop]="screenPop()" />
        <app-session-log [lines]="log()" />
      </div>
    </div>
  `,
})
export class AgentDesktopComponent implements OnInit {
  protected readonly session = inject(SessionService);
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private readonly route = inject(ActivatedRoute);

  protected readonly state = signal<AgentState>('LoggedOut');
  protected readonly stateReason = signal<string | null>(null);
  protected readonly stateSince = signal(Date.now());
  protected readonly offer = signal<CallOfferedMessage | null>(null);
  protected readonly screenPop = signal<ScreenPopMessage | null>(null);
  protected readonly activeCall = signal<ActiveCall | null>(null);
  protected readonly wrapUpCallId = signal<string | null>(null);
  protected readonly dispositions = signal<string[]>([]);
  protected readonly notReadyReasons = signal<string[]>([]);
  protected readonly log = signal<LogLine[]>([]);
  protected readonly banner = signal<Banner>(null);
  protected readonly busy = signal(false);

  private readonly tick = signal(Date.now());

  protected readonly secondsInState = computed(() =>
    Math.max(0, Math.round((this.tick() - this.stateSince()) / 1000)),
  );

  constructor() {
    setInterval(() => this.tick.set(Date.now()), 1000);
    this.subscribeToRealtime();
  }

  ngOnInit(): void {
    if (this.route.snapshot.queryParamMap.get('denied') === 'supervisor') {
      this.show('warn', 'You need the Supervisor role for that console — the server enforces it too.');
    }

    this.api.bootstrap().subscribe({
      next: boot => {
        this.dispositions.set(boot.dispositions);
        this.notReadyReasons.set(boot.notReadyReasons);
      },
      error: err => this.show('bad', describeHttpError(err)),
    });

    // A restored session (page refresh) has no live hub connection yet.
    void this.realtime.connect().then(() => this.reconcile()).catch(() => {
      this.show('bad', 'Could not open the realtime connection — you will not receive calls.');
    });
  }

  // ---------------------------------------------------------------- realtime

  private subscribeToRealtime(): void {
    this.realtime.callOffered$.pipe(takeUntilDestroyed()).subscribe(offer => {
      this.offer.set(offer);
      this.state.set('Reserved');
      this.stateSince.set(Date.now());
      this.write('callOffered', `${offer.from} · ${offer.queue}`);
    });

    this.realtime.screenPop$.pipe(takeUntilDestroyed()).subscribe(pop => {
      this.screenPop.set(pop);
      this.write('screenPop', `${pop.outcome} in ${pop.elapsedMs} ms`);
    });

    this.realtime.callConnected$.pipe(takeUntilDestroyed()).subscribe(connected => {
      this.offer.set(null);
      this.activeCall.set({
        id: connected.callId,
        party: this.offer()?.from ?? this.activeCall()?.party ?? '—',
        startedAt: new Date(connected.answeredAt).getTime(),
        onHold: false,
        recording: connected.recording ?? true,
        recordingPaused: false,
      });
      this.setLocalState('OnCall');
      this.write('callConnected', connected.callId);
    });

    this.realtime.callEnded$.pipe(takeUntilDestroyed()).subscribe(ended => {
      this.activeCall.set(null);
      this.wrapUpCallId.set(ended.callId);
      this.setLocalState('AfterCallWork');
      this.write('callEnded', `${ended.reason} after ${ended.talkSeconds}s`);
    });

    this.realtime.callRevoked$.pipe(takeUntilDestroyed()).subscribe(revoked => {
      this.offer.set(null);
      this.screenPop.set(null);
      this.setLocalState(revoked.agentState, revoked.loggedOut ? null : 'RNA');

      this.show('warn', revoked.loggedOut
        ? 'Too many unanswered calls — you have been signed out (FR-C7).'
        : 'Call withdrawn: not answered in time. You are now Not Ready (RNA).');

      this.write('callRevoked', revoked.reason);
    });

    this.realtime.agentStateChanged$.pipe(takeUntilDestroyed()).subscribe(change => {
      if (change.agentId !== this.session.agent()?.id) return;
      this.setLocalState(change.state, change.reason);
    });

    this.realtime.sessionEnded$.pipe(takeUntilDestroyed()).subscribe(ended => {
      this.show('bad', `Session ended: ${ended.reason}. Sign in again to receive calls.`);
      this.setLocalState('LoggedOut');
    });

    // Reconnect reconciles against the server rather than resetting the screen (docs §4.3.3).
    this.realtime.reconnected$.pipe(takeUntilDestroyed()).subscribe(() => {
      this.write('reconnected', 'reconciling with the server');
      this.reconcile();
    });
  }

  private reconcile(): void {
    this.api.me().subscribe({
      next: me => {
        this.setLocalState(me.state, me.reason);

        if (me.currentCall && me.reservationToken && me.reservationExpiresAt) {
          // Reattach to an offer that arrived while we were disconnected.
          this.offer.set({
            callId: me.currentCall.id,
            reservationToken: me.reservationToken,
            expiresAt: me.reservationExpiresAt,
            direction: me.currentCall.direction,
            from: me.currentCall.from,
            to: me.currentCall.to,
            queue: me.currentCall.queue ?? '',
            waitSeconds: me.currentCall.waitSeconds,
            ringTimeoutSeconds: 15,
          });
        } else if (me.currentCall && me.state === 'OnCall') {
          this.activeCall.set({
            id: me.currentCall.id,
            party: me.currentCall.direction === 'Inbound' ? me.currentCall.from : me.currentCall.to,
            startedAt: Date.now() - me.currentCall.talkSeconds * 1000,
            onHold: me.currentCall.onHold,
            recording: me.currentCall.recording,
            recordingPaused: me.currentCall.recordingPaused,
          });
        } else if (me.currentCall && me.state === 'AfterCallWork') {
          this.wrapUpCallId.set(me.currentCall.id);
        }
      },
      error: err => this.show('bad', describeHttpError(err)),
    });
  }

  // ---------------------------------------------------------------- commands

  protected setState(state: AgentState, reason?: string): void {
    this.api.setState(state, reason ?? null).subscribe({
      next: result => {
        this.setLocalState(result.state, result.reason);
        this.write('stateChanged', `${result.state}${result.reason ? ' · ' + result.reason : ''}`);
      },
      // e.g. "Finish or release the current call first." — the server's rule, surfaced verbatim.
      error: err => this.show('warn', describeHttpError(err)),
    });
  }

  protected answer(): void {
    const offer = this.offer();
    if (!offer) return;

    this.busy.set(true);
    this.api.answer(offer.callId, offer.reservationToken).subscribe({
      next: () => this.busy.set(false),
      error: err => {
        // 409: the reservation expired and the call has moved on. Say so plainly.
        this.offer.set(null);
        this.busy.set(false);
        this.show('warn', describeHttpError(err));
      },
    });
  }

  protected reject(): void {
    const offer = this.offer();
    if (!offer) return;

    this.busy.set(true);
    this.api.reject(offer.callId, offer.reservationToken).subscribe({
      next: () => {
        this.offer.set(null);
        this.busy.set(false);
      },
      error: err => {
        this.offer.set(null);
        this.busy.set(false);
        this.show('warn', describeHttpError(err));
      },
    });
  }

  protected toggleHold(): void {
    const call = this.activeCall();
    if (!call) return;

    this.api.hold(call.id, !call.onHold).subscribe({
      next: result => {
        this.activeCall.update(c => (c ? { ...c, onHold: result.onHold } : c));
        this.write('hold', String(result.onHold));
      },
      error: err => this.show('warn', describeHttpError(err)),
    });
  }

  protected toggleRecording(): void {
    const call = this.activeCall();
    if (!call) return;

    this.api.setRecordingPaused(call.id, !call.recordingPaused).subscribe({
      next: result => {
        this.activeCall.update(c => (c ? { ...c, recordingPaused: result.recordingPaused } : c));
        this.write('recording', result.recordingPaused ? 'paused for card capture' : 'resumed');
      },
      error: err => this.show('warn', describeHttpError(err)),
    });
  }

  protected transfer(): void {
    const call = this.activeCall();
    if (!call) return;

    const to = prompt('Transfer to number (E.164 or local):', '+442045550300');
    if (!to) return;

    this.api.transfer(call.id, to, null).subscribe({
      next: () => this.write('transfer', `blind → ${to}`),
      error: err => this.show('warn', describeHttpError(err)),
    });
  }

  protected sendDtmf(digits: string): void {
    const call = this.activeCall();
    if (!call) return;

    this.api.sendDtmf(call.id, digits).subscribe({
      next: () => this.write('dtmf', `${digits.length} digits sent (never logged)`),
      error: err => this.show('warn', describeHttpError(err)),
    });
  }

  protected hangup(): void {
    const call = this.activeCall();
    if (!call) return;

    this.api.hangup(call.id).subscribe({
      error: err => this.show('warn', describeHttpError(err)),
    });
  }

  protected submitDisposition(disposition: Disposition): void {
    const callId = this.wrapUpCallId();
    if (!callId) return;

    this.api.submitDisposition(callId, disposition.code, disposition.notes).subscribe({
      next: () => {
        this.wrapUpCallId.set(null);
        this.screenPop.set(null);
        this.write('disposition', 'submitted · CRM write-back queued');
      },
      error: err => this.show('warn', describeHttpError(err)),
    });
  }

  protected placeOutbound(to: string): void {
    this.api.placeOutbound(to).subscribe({
      next: result => {
        this.activeCall.set({
          id: result.callId,
          party: result.to,
          startedAt: Date.now(),
          onHold: false,
          recording: true,
          recordingPaused: false,
        });

        this.setLocalState('OnCall');

        if (result.contact) {
          this.screenPop.set({
            callId: result.callId, outcome: 'Found',
            contact: result.contact, candidates: [], elapsedMs: 0,
          });
        }

        this.write('outbound', `dialling ${result.to}`);
      },
      error: err => {
        // 403 from the do-not-call gate. A policy refusal, shown as one.
        const message = describeHttpError(err);
        this.show('bad', message);
        this.write('outboundBlocked', message);
      },
    });
  }

  // ---------------------------------------------------------------- helpers

  private setLocalState(state: AgentState, reason: string | null = null): void {
    this.state.set(state);
    this.stateReason.set(reason);
    this.stateSince.set(Date.now());

    if (state === 'Available' || state === 'LoggedOut') {
      this.offer.set(null);
      this.activeCall.set(null);
    }
  }

  private write(event: string, detail: string): void {
    this.log.update(lines => [{ at: new Date(), event, detail }, ...lines].slice(0, 200));
  }

  private show(kind: 'info' | 'warn' | 'bad', text: string): void {
    this.banner.set({ kind, text });
    setTimeout(() => this.banner.set(null), 6000);
  }
}
