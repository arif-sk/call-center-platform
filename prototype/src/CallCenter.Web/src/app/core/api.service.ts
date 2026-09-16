import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentDetail, AgentStateResponse, Bootstrap, CallEventView, CallView,
  OutboundCallResponse, SessionResponse, WallboardSnapshot,
} from './models';

/**
 * One typed method per endpoint. Nothing else in the app knows a URL, which keeps the API surface
 * in a single reviewable file and means a route change is a one-line edit rather than a search.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1';

  // ---------------------------------------------------------------- session

  bootstrap(): Observable<Bootstrap> {
    return this.http.get<Bootstrap>(`${this.base}/bootstrap`);
  }

  login(agentId: string, role: string): Observable<SessionResponse> {
    return this.http.post<SessionResponse>(`${this.base}/session`, { agentId, role });
  }

  logout(): Observable<void> {
    return this.http.delete<void>(`${this.base}/session`);
  }

  // ---------------------------------------------------------------- agent

  me(): Observable<AgentDetail> {
    return this.http.get<AgentDetail>(`${this.base}/agents/me`);
  }

  setState(state: string, reason?: string | null): Observable<AgentStateResponse> {
    return this.http.put<AgentStateResponse>(`${this.base}/agents/me/state`, { state, reason: reason ?? null });
  }

  // ---------------------------------------------------------------- call control

  answer(callId: string, reservationToken: string): Observable<unknown> {
    return this.http.post(`${this.base}/calls/${callId}/answer`, { reservationToken });
  }

  reject(callId: string, reservationToken: string): Observable<unknown> {
    return this.http.post(`${this.base}/calls/${callId}/reject`, { reservationToken });
  }

  hold(callId: string, hold: boolean): Observable<{ onHold: boolean }> {
    return this.http.post<{ onHold: boolean }>(`${this.base}/calls/${callId}/hold`, { hold });
  }

  sendDtmf(callId: string, digits: string): Observable<unknown> {
    return this.http.post(`${this.base}/calls/${callId}/dtmf`, { digits });
  }

  setRecordingPaused(callId: string, paused: boolean): Observable<{ recordingPaused: boolean }> {
    return this.http.post<{ recordingPaused: boolean }>(`${this.base}/calls/${callId}/recording`, { paused });
  }

  transfer(callId: string, toNumber: string | null, toAgentId: string | null): Observable<unknown> {
    return this.http.post(`${this.base}/calls/${callId}/transfer`, { toNumber, toAgentId });
  }

  hangup(callId: string): Observable<unknown> {
    return this.http.post(`${this.base}/calls/${callId}/hangup`, {});
  }

  submitDisposition(callId: string, code: string, notes: string | null): Observable<unknown> {
    return this.http.post(`${this.base}/calls/${callId}/disposition`, { code, notes });
  }

  placeOutbound(to: string): Observable<OutboundCallResponse> {
    return this.http.post<OutboundCallResponse>(`${this.base}/calls/outbound`, { to });
  }

  // ---------------------------------------------------------------- supervisor (role-gated)

  wallboard(): Observable<WallboardSnapshot> {
    return this.http.get<WallboardSnapshot>(`${this.base}/wallboard`);
  }

  recentCalls(take = 50): Observable<CallView[]> {
    return this.http.get<CallView[]>(`${this.base}/calls/recent`, {
      params: new HttpParams().set('take', take),
    });
  }

  trace(callId: string): Observable<CallEventView[]> {
    return this.http.get<CallEventView[]>(`${this.base}/calls/${callId}/trace`);
  }

  // ---------------------------------------------------------------- simulator (demo only)
  //
  // These exist only while the simulated telephony provider is active. With a real carrier the
  // controller is removed from the routing table entirely, and these calls return 404.

  simulateInbound(did: string, from: string, patienceSeconds: number): Observable<{ callId: string }> {
    return this.http.post<{ callId: string }>(`${this.base}/simulator/inbound`, { did, from, patienceSeconds });
  }

  simulateCallerHangup(callId: string): Observable<unknown> {
    return this.http.post(`${this.base}/simulator/hangup/${callId}`, {});
  }

  setCrmOutage(enabled: boolean): Observable<{ crmFailing: boolean; note: string }> {
    return this.http.post<{ crmFailing: boolean; note: string }>(`${this.base}/simulator/crm-outage`, { enabled });
  }
}
