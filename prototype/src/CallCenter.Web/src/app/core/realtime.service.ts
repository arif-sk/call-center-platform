import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import {
  HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel,
} from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import {
  AgentStateChangedMessage, CallConnectedMessage, CallEndedMessage, CallEventMessage,
  CallOfferedMessage, CallRevokedMessage, ScreenPopMessage, SessionEndedMessage, WallboardSnapshot,
} from './models';
import { SessionService } from './session.service';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/** Heartbeat cadence. Two missed beats make an agent suspect; six sign them out (FR-B4). */
const HEARTBEAT_MS = 10_000;

/**
 * The realtime channel.
 *
 * This is where RxJS genuinely belongs, and it is the reason Angular suits an agent desktop: a
 * call is not a request/response, it is a continuous stream of events arriving unprompted —
 * offered, screen-popped, connected, revoked, ended. Each one is a typed Subject that components
 * subscribe to, so a component never touches the transport.
 *
 * Two behaviours matter more than the plumbing:
 *  - the connection status is honest and visible (NFR-U4) — an agent must know instantly that
 *    they are not receiving calls;
 *  - reconnection emits `reconnected$` so the desktop reconciles against the server rather than
 *    resetting itself (docs §4.3.3). An agent who refreshes mid-call gets their call back.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly session = inject(SessionService);
  private readonly destroyRef = inject(DestroyRef);

  private connection: HubConnection | null = null;
  private heartbeat: ReturnType<typeof setInterval> | null = null;

  readonly status = signal<ConnectionStatus>('disconnected');

  // ---- typed streams -------------------------------------------------------
  private readonly callOffered = new Subject<CallOfferedMessage>();
  private readonly screenPop = new Subject<ScreenPopMessage>();
  private readonly callConnected = new Subject<CallConnectedMessage>();
  private readonly callEnded = new Subject<CallEndedMessage>();
  private readonly callRevoked = new Subject<CallRevokedMessage>();
  private readonly agentStateChanged = new Subject<AgentStateChangedMessage>();
  private readonly sessionEnded = new Subject<SessionEndedMessage>();
  private readonly wallboard = new Subject<WallboardSnapshot>();
  private readonly callEvent = new Subject<CallEventMessage>();
  private readonly reconnected = new Subject<void>();

  readonly callOffered$: Observable<CallOfferedMessage> = this.callOffered.asObservable();
  readonly screenPop$: Observable<ScreenPopMessage> = this.screenPop.asObservable();
  readonly callConnected$: Observable<CallConnectedMessage> = this.callConnected.asObservable();
  readonly callEnded$: Observable<CallEndedMessage> = this.callEnded.asObservable();
  readonly callRevoked$: Observable<CallRevokedMessage> = this.callRevoked.asObservable();
  readonly agentStateChanged$: Observable<AgentStateChangedMessage> = this.agentStateChanged.asObservable();
  readonly sessionEnded$: Observable<SessionEndedMessage> = this.sessionEnded.asObservable();
  readonly wallboard$: Observable<WallboardSnapshot> = this.wallboard.asObservable();
  readonly callEvent$: Observable<CallEventMessage> = this.callEvent.asObservable();
  readonly reconnected$: Observable<void> = this.reconnected.asObservable();

  constructor() {
    this.destroyRef.onDestroy(() => void this.disconnect());
  }

  async connect(): Promise<void> {
    if (this.connection && this.connection.state !== HubConnectionState.Disconnected) return;

    const token = this.session.token();
    if (!token) throw new Error('Cannot open a realtime connection without a session.');

    this.status.set('connecting');

    this.connection = new HubConnectionBuilder()
      .withUrl('/hubs/callcenter', {
        // SignalR sends this as an Authorization header where it can, and as an access_token query
        // parameter on the WebSocket handshake — browsers cannot set headers there. The server's
        // authentication scheme accepts both.
        accessTokenFactory: () => this.session.token() ?? '',
      })
      // Backoff rather than a tight retry loop: 500 agents reconnecting in lockstep after a hub
      // restart is a thundering herd (docs §7.3).
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    this.registerHandlers(this.connection);

    this.connection.onreconnecting(() => this.status.set('reconnecting'));

    this.connection.onreconnected(() => {
      this.status.set('connected');
      this.reconnected.next();
    });

    this.connection.onclose(() => {
      this.status.set('disconnected');
      this.stopHeartbeat();
    });

    await this.connection.start();
    this.status.set('connected');
    this.startHeartbeat();
  }

  async disconnect(): Promise<void> {
    this.stopHeartbeat();

    if (this.connection) {
      await this.connection.stop().catch(() => undefined);
      this.connection = null;
    }

    this.status.set('disconnected');
  }

  /** Server-side snapshot used after a reconnect. */
  reconcile(): Promise<unknown> {
    return this.connection?.invoke('Reconcile') ?? Promise.resolve(null);
  }

  private registerHandlers(connection: HubConnection): void {
    connection.on('callOffered', (m: CallOfferedMessage) => this.callOffered.next(m));
    connection.on('screenPop', (m: ScreenPopMessage) => this.screenPop.next(m));
    connection.on('callConnected', (m: CallConnectedMessage) => this.callConnected.next(m));
    connection.on('callEnded', (m: CallEndedMessage) => this.callEnded.next(m));
    connection.on('callRevoked', (m: CallRevokedMessage) => this.callRevoked.next(m));
    connection.on('agentStateChanged', (m: AgentStateChangedMessage) => this.agentStateChanged.next(m));
    connection.on('sessionEnded', (m: SessionEndedMessage) => this.sessionEnded.next(m));
    connection.on('wallboard', (m: WallboardSnapshot) => this.wallboard.next(m));
    connection.on('callEvent', (m: CallEventMessage) => this.callEvent.next(m));
  }

  private startHeartbeat(): void {
    this.stopHeartbeat();

    this.heartbeat = setInterval(() => {
      this.connection?.invoke('Heartbeat').catch(() => {
        // A failed beat is not fatal on its own; the server's own timeout is the authority on
        // whether this agent is still routable.
      });
    }, HEARTBEAT_MS);
  }

  private stopHeartbeat(): void {
    if (this.heartbeat) {
      clearInterval(this.heartbeat);
      this.heartbeat = null;
    }
  }
}
