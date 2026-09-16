/**
 * Typed mirrors of the API contracts in `CallCenter.Api/Contracts/ApiContracts.cs`.
 *
 * These are hand-written rather than generated, which is the right call at prototype size but the
 * wrong one at product size: with an OpenAPI document already published at /swagger/v1/swagger.json,
 * a real build would generate this file in CI so the client cannot silently drift from the server.
 */

// ---------------------------------------------------------------- agent

export type AgentState =
  | 'LoggedOut'
  | 'Available'
  | 'Reserved'
  | 'Ringing'
  | 'OnCall'
  | 'AfterCallWork'
  | 'NotReady';

export type AgentRole = 'Agent' | 'Supervisor' | 'Admin';

export interface AgentProfile {
  id: string;
  name: string;
  extension: string;
  team: string;
  skills: Record<string, number>;
  state: AgentState;
}

export interface SessionResponse {
  token: string;
  role: AgentRole;
  agent: AgentProfile;
}

export interface AgentDetail {
  id: string;
  name: string;
  state: AgentState;
  reason: string | null;
  callsHandled: number;
  consecutiveRna: number;
  /** Present only while an offer is live — see docs §4.3.3 (reconnect reconciles, not resets). */
  reservationToken: string | null;
  reservationExpiresAt: string | null;
  currentCall: CallView | null;
}

export interface AgentStateResponse {
  state: AgentState;
  reason: string | null;
}

// ---------------------------------------------------------------- configuration

export interface QueueSummary {
  id: string;
  name: string;
  displayName: string;
  requiredSkills: string[];
  basePriority: number;
  ringTimeoutSeconds: number;
  slaThresholdSeconds: number;
  selectionPolicy: string;
}

export interface Bootstrap {
  provider: string;
  supervisorAgentId: string;
  agents: AgentProfile[];
  queues: QueueSummary[];
  dids: Record<string, string>;
  dispositions: string[];
  notReadyReasons: string[];
}

// ---------------------------------------------------------------- calls

export type CallStatus =
  | 'Initiated' | 'Queued' | 'Offered' | 'Ringing' | 'Connected'
  | 'Wrapping' | 'Completed' | 'Abandoned' | 'Failed' | 'Blocked';

export interface CallView {
  id: string;
  direction: 'Inbound' | 'Outbound';
  status: CallStatus;
  from: string;
  to: string;
  queue: string | null;
  priority: number;
  agent: string | null;
  contact: string | null;
  waitSeconds: number;
  talkSeconds: number;
  rnaCount: number;
  onHold: boolean;
  recording: boolean;
  recordingPaused: boolean;
  disposition: string | null;
  initiatedAt: string;
  endedAt: string | null;
  endReason: string | null;
}

export interface CallEventView {
  seq: number;
  type: string;
  occurredAt: string;
  correlationId: string;
  /** JSON string — the server stores the event body verbatim (JSONB in production). */
  payload: string;
}

export interface OutboundCallResponse {
  callId: string;
  to: string;
  status: CallStatus;
  contact: CrmContact | null;
}

// ---------------------------------------------------------------- CRM

export type ScreenPopOutcome = 'Found' | 'Unknown' | 'Ambiguous' | 'Unavailable';

export interface CrmContact {
  externalId: string;
  displayName: string;
  company: string;
  tier: string;
  lastInteraction: string;
}

// ---------------------------------------------------------------- wallboard

export interface QueueStats {
  name: string;
  displayName: string;
  waiting: number;
  longestWaitSeconds: number;
  slaThresholdSeconds: number;
  serviceLevelPct: number;
  offeredToday: number;
  answeredToday: number;
  abandonedToday: number;
  averageWaitSeconds: number;
}

export interface AgentSnapshot {
  id: string;
  name: string;
  team: string;
  state: AgentState;
  reason: string | null;
  secondsInState: number;
  callsHandled: number;
  skills: string[];
}

export interface WallboardSnapshot {
  at: string;
  queues: QueueStats[];
  agents: AgentSnapshot[];
  agentStateCounts: Partial<Record<AgentState, number>>;
  liveCalls: number;
  crmCircuitOpen: boolean;
  pendingCrmWriteBacks: number;
  provider: string;
}

// ---------------------------------------------------------------- realtime payloads
//
// One interface per hub message. Naming them means a server-side rename breaks the build here
// instead of silently producing an empty panel at 09:00 on a Monday.

export interface CallOfferedMessage {
  callId: string;
  reservationToken: string;
  expiresAt: string;
  direction: 'Inbound' | 'Outbound';
  from: string;
  to: string;
  queue: string;
  waitSeconds: number;
  ringTimeoutSeconds: number;
}

export interface ScreenPopMessage {
  callId: string;
  outcome: ScreenPopOutcome;
  contact: CrmContact | null;
  candidates: CrmContact[];
  elapsedMs: number;
}

export interface CallConnectedMessage {
  callId: string;
  answeredAt: string;
  recording?: boolean;
}

export interface CallEndedMessage {
  callId: string;
  reason: string;
  talkSeconds: number;
  requiresDisposition: boolean;
}

export interface CallRevokedMessage {
  callId: string;
  reason: string;
  agentState: AgentState;
  loggedOut: boolean;
}

export interface AgentStateChangedMessage {
  agentId: string;
  name: string;
  state: AgentState;
  reason: string | null;
  currentCallId: string | null;
}

export interface SessionEndedMessage {
  reason: string;
}

export interface CallEventMessage {
  callId: string;
  type: string;
  occurredAt: string;
  payload: string;
}

export interface ApiError {
  error: string;
}
