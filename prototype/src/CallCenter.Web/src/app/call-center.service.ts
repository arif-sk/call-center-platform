import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import * as signalR from '@microsoft/signalr';

export interface AgentView {
  id: string;
  name: string;
  extension: string;
  isSupervisor: boolean;
  state: 'Offline' | 'Available' | 'NotReady' | 'Ringing' | 'OnCall' | 'WrapUp';
  currentCallId: string | null;
  stateChangedAt: string;
}

export interface CallView {
  id: string;
  from: string;
  to: string;
  status: 'Queued' | 'Ringing' | 'Connected' | 'WrapUp' | 'Completed' | 'Abandoned';
  queuedAt: string;
  answeredAt: string | null;
  endedAt: string | null;
  agentId: string | null;
  agentName: string | null;
  disposition: string | null;
  waitSeconds: number;
  talkSeconds: number;
}

export interface Snapshot {
  agents: AgentView[];
  queue: CallView[];
  recent: CallView[];
  stats: { waiting: number; available: number; onCall: number; completed: number; longestWaitSeconds: number };
  serverTime: string;
}

/**
 * The whole client-side model. The server sends a complete snapshot after every change, so this
 * service never has to work out what the new state should be — it only has to hold the last one
 * the server sent.
 */
@Injectable({ providedIn: 'root' })
export class CallCenterService {
  private readonly snapshotSignal = signal<Snapshot | null>(null);
  private readonly agentIdSignal = signal<string | null>(localStorage.getItem('agentId'));
  private readonly errorSignal = signal<string | null>(null);
  private readonly connectedSignal = signal(false);

  /** Ticks once a second so on-screen timers count up without the server sending anything. */
  private readonly nowSignal = signal(Date.now());

  readonly snapshot = this.snapshotSignal.asReadonly();
  readonly error = this.errorSignal.asReadonly();
  readonly connected = this.connectedSignal.asReadonly();
  readonly now = this.nowSignal.asReadonly();
  readonly agentId = this.agentIdSignal.asReadonly();

  readonly agents = computed(() => this.snapshotSignal()?.agents ?? []);

  /** The signed-in agent's own row, which is what the desktop renders from. */
  readonly me = computed(() => {
    const id = this.agentIdSignal();
    return id ? (this.agents().find((a) => a.id === id) ?? null) : null;
  });

  readonly myCall = computed(() => {
    const me = this.me();
    const snapshot = this.snapshotSignal();
    if (!me?.currentCallId || !snapshot) return null;
    return snapshot.queue.find((c) => c.id === me.currentCallId) ?? null;
  });

  constructor(private readonly http: HttpClient) {
    setInterval(() => this.nowSignal.set(Date.now()), 1000);
    void this.start();
  }

  private async start(): Promise<void> {
    this.snapshotSignal.set(await firstValueFrom(this.http.get<Snapshot>('/api/agents')));

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/hub')
      .withAutomaticReconnect()
      .build();

    connection.on('snapshot', (snapshot: Snapshot) => this.snapshotSignal.set(snapshot));
    connection.onreconnected(() => this.connectedSignal.set(true));
    connection.onclose(() => this.connectedSignal.set(false));

    await connection.start();
    this.connectedSignal.set(true);
  }

  setAgentId(id: string | null): void {
    this.agentIdSignal.set(id);
    if (id) localStorage.setItem('agentId', id);
    else localStorage.removeItem('agentId');
  }

  dismissError(): void {
    this.errorSignal.set(null);
  }

  /** Seconds since an ISO timestamp, recomputed by the one-second tick above. */
  elapsed(since: string | null): number {
    if (!since) return 0;
    return Math.max(0, Math.round((this.now() - new Date(since).getTime()) / 1000));
  }

  signIn(agentId: string) { return this.post(`/api/agents/${agentId}/sign-in`); }
  signOut(agentId: string) { return this.post(`/api/agents/${agentId}/sign-out`); }
  setReady(agentId: string, ready: boolean) { return this.post(`/api/agents/${agentId}/ready`, { ready }); }

  simulateInbound(from: string) { return this.post('/api/calls/inbound', { from, to: '+18005550100' }); }
  abandon(callId: string) { return this.post(`/api/calls/${callId}/abandon`); }

  answer(callId: string, agentId: string) { return this.post(`/api/calls/${callId}/answer/${agentId}`); }
  decline(callId: string, agentId: string) { return this.post(`/api/calls/${callId}/decline/${agentId}`); }
  hangUp(callId: string, agentId: string) { return this.post(`/api/calls/${callId}/hang-up/${agentId}`); }

  wrapUp(callId: string, agentId: string, disposition: string, notes: string) {
    return this.post(`/api/calls/${callId}/wrap-up/${agentId}`, { disposition, notes });
  }

  /**
   * Every command returns the new snapshot, so the screen updates from the reply even if the live
   * connection is briefly down. A rejected command shows the server's message rather than a
   * generic failure.
   */
  private async post(url: string, body: unknown = {}): Promise<boolean> {
    try {
      this.errorSignal.set(null);
      this.snapshotSignal.set(await firstValueFrom(this.http.post<Snapshot>(url, body)));
      return true;
    } catch (response: any) {
      this.errorSignal.set(response?.error?.error ?? 'Something went wrong. Please try again.');
      return false;
    }
  }
}
