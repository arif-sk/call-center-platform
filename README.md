# In-House Call Center Platform — System Design Exercise

**Role:** System Analyst / Solution Architect
**Brief:** replace a third-party call center tool with our own inbound + outbound calling
platform. CRM-integrated. 50 agents today, 500+ later. AI-ready eventually.
**Stack:** .NET 8 backend, Angular frontend.

---

## The submission

**[`docs/Call-Center-Platform-Design.pdf`](docs/Call-Center-Platform-Design.pdf)** — 15 pages,
6 diagrams, **written in plain English so a non-technical reader can follow it end to end.**
Technical terms are explained where they first appear, and there is a glossary at the back.

| § | | § | |
| --- | --- | --- | --- |
| 1 | What we are building | 6 | Getting ready for AI |
| 2 | What I need to ask you | 7 | Putting it live safely |
| 3 | What the first version should include | 8 | The working demonstration |
| 4 | How the system is put together | 9 | What I would do in the first two weeks |
| 5 | Growing from 50 to 500 | | |

Plain language, not less substance: the same decisions, the same numbers, the same trade-offs —
just explained rather than abbreviated.

### Sources

| File in [`docs/source/`](docs/source/) | What it is |
| --- | --- |
| `call-center-platform-design.md` | The plain-English document the PDF is built from |
| `call-center-platform-design-technical.md` | The same argument written for an engineering audience |
| `01-…` to `07-…` | The original long-form working notes, one per topic, where the detail lives |

To rebuild the PDF after editing the source:

```bash
cd tools/build-pdf
npm install      # first time only
node build.mjs   # renders the diagrams in headless Chrome, writes the PDF
```

The build **fails** if any diagram does not parse, rather than shipping a document with a blank
space where a diagram should be.

## The prototype

A working .NET 8 slice in [`prototype/`](prototype/README.md) — not part of the written
submission, built to demonstrate the design live.

```bash
cd prototype
dotnet test                              # 84 backend tests, ~8 s, no external dependencies
dotnet run --project src/CallCenter.Api  # http://localhost:5080
```

The frontend is an **Angular 20 single-page app** (`src/CallCenter.Web`) — agent desktop and
supervisor console — built into the API's `wwwroot`, so the command above serves it with no
`npm install` required.

It implements the reservation protocol, the agent state machine, skills-based routing with
pluggable selection policies, RNA handling, the telephony port with two real adapters, CRM
degradation, the DNC gate, PCI recording pause, and the interaction event stream — behind a
controller-based ASP.NET Core API with role-gated authorisation, and an Angular 20 client
using signals for state and RxJS for the call event stream. A
[6-minute walkthrough](prototype/README.md#the-6-minute-walkthrough) is in its README, along
with an explicit table of what is real and what is simulated.

---

## Three decisions everything else follows from

**1. We are building a routing system, not a telephony system.**
Carrier-grade media handling is a multi-year problem with a regulatory surface that has
nothing to do with our business. We own the logic and the data; we rent the PSTN. Signalling
flows through us, media does not — which is why 500 agents is a capacity-planning exercise
rather than a re-architecture, and why a backend deployment cannot drop a live call.
→ [§1.1](docs/01-requirement-analysis.md#11-the-ask-restated)

**2. Nine things ship in v1 that nobody can see.**
The port with two implementations, the event stream, server-authoritative state, reservation
tokens, idempotent webhooks, `TenantId` on every table. None of them is a feature. Every one
of them is far more expensive to retrofit than to build, and together they are the difference
between a 50-agent system that grows and one that gets rewritten.
→ [§3.5](docs/03-mvp-definition.md#35-the-invisible-v1-items-i-refuse-to-cut)

**3. The phones must keep working when anything else is broken.**
The CRM goes down → circuit breaker, calls continue. A routing worker dies → lease expires in
5 s, connected calls unaffected. A deploy goes out → drain and hand over partitions. A release
is bad → revert by agent cohort in three minutes. A cutover goes wrong → one carrier change
puts the DID back.
→ [§4.10](docs/04-system-design.md#410-failure-modes-and-designed-responses), [§7](docs/07-deployment-strategy.md)

---

## What I'd want to discuss

The parts I am least certain about, and would want a stakeholder in the room for:

- **Build-vs-buy economics (R-01).** I have designed this as if the decision is made. I would
  want to see the current licence cost and real call volumes before writing production code,
  and I have written down kill-criteria rather than assuming the answer.
- **The CRM (Q13, Q14).** The single largest estimate variance in the whole programme. A
  CRM that can't be searched by phone number in under 300 ms turns a 2-second screen-pop into
  a data-sync subsystem.
- **Real-time media streaming (§6.1).** The one AI decision that cannot be deferred, because
  it constrains the carrier we choose *now* for features we want in year two.
- **Number porting (R-02).** Almost certainly the critical path to go-live, and entirely
  outside engineering's control.
