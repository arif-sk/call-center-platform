# Call Center Platform — .NET 8 Prototype

> **What this is:** a working slice of the design in [`../docs`](../docs/README.md), built to
> prove the parts of it that are easy to claim and hard to do — the reservation protocol,
> the telephony port, graceful degradation, and the event stream.
>
> **What this is not:** a product. There is no real carrier, no OIDC and no Redis. Every one of
> those substitutions is marked in the code with what production uses instead.

---

## Run it

```bash
cd prototype
dotnet test                              # 84 backend tests against real SQL Server
dotnet run --project src/CallCenter.Api  # http://localhost:5080
```

**Requires SQL Server** reachable at `localhost` with Windows authentication — the schema is
created on startup. Point it anywhere else (LocalDB, a container, Azure SQL) without touching
code:

```bash
# LocalDB instead of a full instance
dotnet run --project src/CallCenter.Api --ConnectionStrings:CallCenter "Server=(localdb)\MSSQLLocalDB;Database=CallCenterPrototype;Trusted_Connection=True;TrustServerCertificate=True"

# and for the tests
set CALLCENTER_TEST_SQL=Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True
```

Then open:

| URL | What it is |
| --- | --- |
| <http://localhost:5080> | Sign in — pick an agent, or the supervisor console |
| <http://localhost:5080/agent> | **Agent desktop** — presence, incoming offers, call controls, wrap-up, outbound |
| <http://localhost:5080/supervisor> | **Supervisor console** — wallboard, call simulator, live event stream, call traces |
| <http://localhost:5080/swagger> | API surface |

No carrier account, no API keys, **and no `npm install`** — the Angular bundle is committed to
the API's `wwwroot` so one command starts everything. That is deliberate:
see [§3.5, "the invisible v1 items I refuse to cut"](../docs/03-mvp-definition.md#35-the-invisible-v1-items-i-refuse-to-cut).

### Working on the frontend

```bash
cd src/CallCenter.Web
npm install
npm start        # ng serve on :4200, proxying /api and /hubs to the .NET app on :5080
npm run build    # rebuilds into ../CallCenter.Api/wwwroot
npm test         # 16 component/service tests, headless Chrome
```

**For the live demo:** open the supervisor console in one window and two or three agent
desktops in others (sign in as different agents in separate browser profiles or private windows —
the server enforces one session per agent), then drive calls from the simulator.

---

## The 6-minute walkthrough

Each step exists to show one decision from the documents.

| # | Do this | What it proves |
| --- | --- | --- |
| **1** | Sign in as **Amara Okafor**, click **Available**. Watch her appear on the wallboard. | Server-authoritative presence (FR-B1) |
| **2** | On the supervisor console, **place an inbound call** to `+442045550100` (support) from Priya Raman's number. | DID → queue → skills match (FR-C1/C4) |
| **3** | The agent's phone rings **and** the screen-pop fills in — separately. | Ringing and CRM lookup are deliberately decoupled (NFR-P1 vs NFR-P2) |
| **4** | Open the browser console and try answering with a made-up token — `409 Conflict`. | An agent cannot answer a call they were not offered (NFR-SEC3) |
| **5** | **Ignore** the call and let the countdown expire. | RNA: agent goes Not-Ready(`RNA`), the caller is re-queued **with a priority boost** (FR-C7) |
| **6** | Sign in a second agent, go Available, and let the re-queued call route to them. | Recovery without operator intervention |
| **7** | Answer, then **Pause recording** and send DTMF. Open the call trace. | PCI pause is journalled; **the digits never appear in the event stream** (FR-G3, NFR-SEC6) |
| **8** | Hang up → wrap-up → submit disposition. | Mandatory disposition; CRM write-back queued asynchronously (Q21, FR-F3) |
| **9** | Dial **+44 7700 900999** outbound. | DNC gate: a synchronous, fail-closed block in the call path (FR-D4, R-07) |
| **10** | Click **Break the CRM**, then place another call. | The CRM is not allowed to take the phones down: the call routes and connects; only the screen-pop degrades (FR-F5) |
| **11** | Click **Burst: 5 calls** across different DIDs. | Priority ordering — billing (8) outranks sales (6) outranks support (5) (FR-C5) |
| **12** | Click **Impatient caller (5 s)** and leave everyone Not Ready. | Abandon tracking, with short abandons excluded from service level (FR-C8, Q22) |
| **13** | Close an agent's browser tab while Available; wait ~45 s. | Zombie-agent detection — the queue-killer that FR-B4 exists to prevent |
| **14** | Click any row in **Calls**. | The whole call reconstructed from its event stream — the answer to "what happened to call X?" (NFR-M4) and the substrate for AI (doc 6) |
| **15** | Call `/api/v1/wallboard` with an *agent* token — `403 Forbidden`. | Authorisation is a controller attribute, enforced server-side, not a hidden menu item (FR-A2) |

---

## What is real, and what is simulated

Being explicit about this is the point — a prototype that blurs it is not evidence of anything.

| Concern | Prototype | Production (per the docs) |
| --- | --- | --- |
| **Reservation protocol** | ✅ Real — atomic CAS, signed tokens, TTL expiry, server-side validation | Same logic, as a Redis Lua script (§4.3.2) |
| **Agent state machine** | ✅ Real — every transition validated server-side | Identical |
| **Skills routing & selection policies** | ✅ Real — three strategies, per-queue configurable | Identical |
| **RNA / re-queue / priority boost** | ✅ Real | Identical |
| **Telephony port** | ✅ Real — two implementations behind `ITelephonyProvider` | Add carrier adapters; nothing above the port changes |
| **Webhook signature verification** | ✅ Real HMAC-SHA1 + replay protection, unit-tested | Same, with the replay cache in Redis |
| **CRM circuit breaker, timeout, cache, retry queue** | ✅ Real | Same, against a real CRM |
| **Event stream** | ✅ Real — append-only, queryable, replayable | Same, plus a transactional outbox to a bus (§4.8) |
| **Idempotent call termination** | ✅ Real | Keyed on `(provider, providerCallId, eventType, sequence)` |
| **Carrier & media** | ⚠️ Simulated provider (ring, answer, abandon, no-answer, latency) | CPaaS + WebRTC; media never touches our servers |
| **Presence / queue state** | ⚠️ In-memory, behind `PlatformState` | Redis, partitioned and leased (§4.3.2, §5.3) |
| **Database** | ✅ Real — **SQL Server**, same schema and provider as production, reset on every start | Same engine, plus monthly table partitioning, Always On availability groups and log-backup PITR (§4.7, §7.9) |
| **Authorisation** | ✅ Real — an ASP.NET Core authentication scheme + `[Authorize(Roles = …)]` on the controllers | Identical attributes; only the scheme changes |
| **Authentication** | ⚠️ Server-held session tokens behind that scheme | OIDC + PKCE, MFA for supervisors (NFR-SEC2). Controllers read claims, not tokens, so they do not change |
| **Realtime backplane** | ⚠️ Single instance | SignalR + Redis backplane (§4.3.4) |
| **Frontend** | ✅ Real — Angular 20 SPA: standalone components, signals for state, RxJS for the call event stream, typed HTTP client, functional interceptor and route guards | Same app; add the CPaaS WebRTC SDK for real audio |
| **Recording storage, warm transfer, supervisor barge, IVR** | ❌ Not built | MVP scope (§3.2) |

---

## Layout

```
prototype/
├── src/
│   ├── CallCenter.Domain/        pure domain — no dependencies, fully unit-tested
│   │   ├── Agent.cs              agent aggregate
│   │   ├── AgentStateMachine.cs  every transition validated here
│   │   ├── Call.cs               call aggregate + Reservation + CallEvent
│   │   ├── CallQueue.cs          queue config, queued call
│   │   └── Routing/              selection policies (strategy per queue)
│   ├── CallCenter.Telephony/     the port and its two adapters
│   │   ├── ITelephonyProvider.cs the boundary the whole platform talks through
│   │   ├── SimulatedTelephonyProvider.cs
│   │   └── TwilioTelephonyProvider.cs   real HTTP + real signature verification
│   ├── CallCenter.Web/               Angular 20 SPA
│   │   ├── src/app/core/             models · api.service · realtime.service (SignalR)
│   │   │                             session.service · auth.interceptor · guards
│   │   ├── src/app/features/login/
│   │   ├── src/app/features/agent/   desktop container + 6 presentational components
│   │   ├── src/app/features/supervisor/  console container + 6 presentational components
│   │   └── proxy.conf.json           ng serve → the .NET API
│   ├── CallCenter.Api/
│   │   ├── Controllers/              MVC controllers — the HTTP surface
│   │   │   ├── ApiControllerBase.cs  identity from claims; CommandResult → status code
│   │   │   ├── SessionController.cs  [AllowAnonymous] login + bootstrap
│   │   │   ├── AgentsController.cs   [Authorize] presence
│   │   │   ├── CallsController.cs    [Authorize] call controls, wrap-up, outbound
│   │   │   ├── SupervisorController.cs  [Authorize(Roles="Supervisor")] wallboard, CDR, audit
│   │   │   └── SimulatorController.cs   demo only — removed from routing with a real carrier
│   │   ├── Contracts/                request/response DTOs with validation attributes
│   │   ├── Security/                 authentication scheme → claims principal
│   │   ├── Services/
│   │   │   ├── RoutingEngine.cs      matching, reservations, RNA, sweepers ← start here
│   │   │   ├── CallOrchestrator.cs   lifecycle + agent commands
│   │   │   ├── PlatformState.cs      the Redis stand-in (atomic reserve lives here)
│   │   │   ├── InteractionEventStore.cs
│   │   │   ├── CrmClient.cs          ACL: timeout, circuit breaker, cache, retry queue
│   │   │   ├── SuppressionService.cs DNC gate, fails closed
│   │   │   └── WallboardService.cs   1 Hz aggregated push
│   │   ├── Realtime/CallCenterHub.cs
│   │   ├── Endpoints/ApiEndpoints.cs
│   │   ├── Data/                     EF Core: calls, events, state log, suppression, audit
│   │   └── wwwroot/                  built Angular bundle (committed, so dotnet run is enough)
│   └── ...
├── e2e/browser-smoke.mjs         drives two real Chrome tabs through a whole call
└── tests/CallCenter.Tests/       84 tests
    ├── AgentStateMachineTests.cs     legal/illegal transitions, self-assignment refused
    ├── ReservationTests.cs           exclusivity under concurrency, token validation, expiry
    ├── RoutingPolicyTests.cs         fairness, determinism, priority ordering
    ├── TelephonyAdapterTests.cs      signature verify, replay, payload translation, E.164
    ├── AuthorizationTests.cs         401/403 by role, model validation at the edge
    └── EndToEndCallFlowTests.cs      full flows through the real app
```

### If you only read three files

1. **`PlatformState.TryReserve`** — the compare-and-set the entire platform rests on.
2. **`RoutingEngine.MatchCycleAsync`** — the matching loop, and why its cost stays flat as the
   centre grows.
3. **`CrmClient.LookupByNumberAsync`** — the rule that a system we do not control cannot take
   the phones down.

---

## The tests worth looking at

```
ReservationTests.Concurrent_reservations_of_the_same_agent_produce_exactly_one_winner
    64 threads race for one agent. Exactly one wins. This is the invariant that stops a
    customer being bridged to an agent who is already talking.

EndToEndCallFlowTests.An_inbound_call_reaches_a_skilled_agent_and_produces_a_complete_audit_trail
    Arrival → queue → skills match → reservation → screen-pop → forged-token rejection →
    answer → hold → PCI pause → hang-up → wrap-up → CDR → event trace. ~130 ms.

EndToEndCallFlowTests.A_call_that_ends_twice_is_only_counted_once
    Added after the first live run produced two CallEnded events for one hang-up. The kind
    of bug that silently corrupts a quarter of productivity reporting.

EndToEndCallFlowTests.Calls_keep_flowing_when_the_crm_is_down
    The CRM fails 100%. Calls still route, ring and connect.

TwilioWebhookVerificationTests.A_tampered_payload_is_rejected
    Inflating CallDuration on a signed webhook is rejected — our most exposed public endpoint.

AuthorizationTests.An_agent_session_cannot_reach_the_supervisor_surface
    A valid agent token gets 403 on the wallboard, the CDR and the audit log. Authorisation is
    a controller attribute, so forgetting it fails closed.

AuthorizationTests.An_unrecognised_role_is_downgraded_to_agent_rather_than_honoured
    The client asks for a role; the server decides what it gets.

e2e/browser-smoke.mjs  (21 checks, two real Chrome tabs)
    A call placed in the supervisor's tab is offered in the agent's tab over SignalR, with a
    screen-pop and a working reservation token — then answered, PCI-paused, hung up, dispositioned
    and replayed from its event trace. The one assertion no unit test can make.
```

The entire suite runs in about five seconds with no carrier, no network and no database
server. That is the return on building the second telephony adapter in v1, and it is why the
[deployment strategy](../docs/07-deployment-strategy.md) can assume load and chaos testing are
routine rather than heroic.

---

## Configuration

`src/CallCenter.Api/appsettings.json`:

```jsonc
{
  "Telephony": {
    "Provider": "simulated",          // "twilio" swaps the adapter; nothing above the port changes
    "Simulation": {
      "CallerPatienceSeconds": 90,    // how long a simulated caller waits before abandoning
      "OutboundAnswerProbability": 0.75,
      "ProviderLatencyMs": 40         // makes the latency budget in §4.4 visible
    }
  },
  "Routing": {
    "MaxConsecutiveRna": 3,           // unanswered calls before the agent is signed out
    "RnaPriorityBoost": 2,            // the caller is not punished for the agent's miss
    "HeartbeatTimeoutSeconds": 45     // zombie-agent detection (FR-B4)
  },
  "Crm": {
    "TimeoutMs": 800,                 // hard cap; the call is offered regardless
    "CircuitBreakerThreshold": 5,
    "ForceFailure": false             // the supervisor console toggles this live
  }
}
```

---

## Known limitations

Stated plainly, because a prototype that pretends to be complete is worse than one that does not:

- **Single instance.** No Redis backplane, so SignalR does not scale past one node here. The
  partitioning and leasing design is in [§5.3](../docs/05-scalability-plan.md), not in this code.
- **No real media.** Nobody's voice is carried. The agent desktop is a control surface, not a
  softphone — a real build embeds the CPaaS WebRTC SDK at exactly this point, behind the same
  `RealtimeService` boundary.
- **Warm transfer is blind only.** The consult leg (§4.6) is designed but not implemented.
- **No IVR.** The DID maps straight to a queue; menu handling is MVP scope, not prototype scope.
- **Database resets on every start.** Deliberate, so demos are reproducible. Tests create and
  drop a database per class, which is why the suite takes ~2 minutes against SQL Server rather
  than the seconds it took against an in-process database — the honest cost of testing on the
  engine we actually ship on.
- **Session auth is a bearer token in memory.** Real OIDC adds no architectural insight to a
  demo and a lot of setup friction. The *authorisation* half is real: it runs through an
  ASP.NET Core authentication scheme and `[Authorize]` attributes, so swapping in OIDC bearer
  validation changes one registration in `Program.cs` and no controller.
