# Call Center Platform — working prototype

A deliberately small .NET 8 + Angular 20 slice of the design in
[`../docs/Call-Center-Platform-Design.pdf`](../docs/Call-Center-Platform-Design.pdf).

It exists to make one claim concrete: **a call arrives, the platform picks an agent, the agent's
screen changes on its own, and what happened is written down.** Everything else in the design
document is deliberately absent — see [What is not here](#what-is-not-here-and-why).

## Run it

**You need:** .NET 8 SDK and SQL Server (Express or Developer edition is fine) on `localhost`.

```bash
cd prototype
dotnet run --project src/CallCenter.Api
```

Open <http://localhost:5080>. The database, its tables and five seeded agents are created on
first run — there is no setup script.

Point it somewhere else by editing `ConnectionStrings:CallCenter` in
`src/CallCenter.Api/appsettings.json`.

```bash
dotnet test        # 11 tests, ~2 seconds, no database required
```

The compiled Angular app is committed into the API's `wwwroot`, so the command above serves the
UI without an `npm install`. To change the frontend:

```bash
cd src/CallCenter.Web
npm install
npm start          # dev server on :4200, proxied to the API
npm run build      # rebuilds into ../CallCenter.Api/wwwroot
```

## The five-minute walkthrough

Open two browser windows side by side — one is the supervisor, one is the agent.

1. **Window A → sign in as Sara Haddad** (the supervisor). You get the live console.
2. **Window B → sign in as Amina Rahman.** She starts *not ready*; press **Go ready**.
   Watch window A: Sara's agent table updates without a refresh.
3. **Window A → press "Call in".** The call appears as *Ringing* in the supervisor's live calls,
   and an **Incoming call** card appears on Amina's desktop by itself.
4. **Window B → Answer.** Both screens switch to *Connected* and the talk timer starts.
5. **Window B → Hang up**, choose a disposition, **Finish**. The call moves to *Recently
   finished* with its wait and talk time, and Amina goes straight back to available.

Worth trying as well:

- **Press "Call in" with nobody ready.** The caller waits. Now press **Go ready** — the waiting
  call connects immediately, because routing runs after every change, not on a timer.
- **Press "Call in" twice with two agents ready.** They go to different agents, never the same one.
- **Press Decline.** The call returns to the queue and the agent is set not-ready, so the platform
  does not immediately offer them the same call again.
- **Press "Caller hangs up"** on a waiting call. It is recorded as abandoned — the number
  supervisors actually watch.

## How it is built

```
src/CallCenter.Api
  Program.cs                      entry point: build host, prepare database, run
  Startup.cs                      service registration and the HTTP pipeline
  Data/CallCenterDbContext.cs     two tables: Agents, Calls
  Services/CallCenterService.cs   the entire domain — queue, routing, state transitions
  Services/SnapshotPublisher.cs   how the new picture reaches the screens
  Controllers/                    the HTTP surface — MVC controllers, including /health
  Hubs/CallCenterHub.cs           the live connection
src/CallCenter.Web                Angular: sign-in, agent desktop, supervisor console
tests/CallCenter.Tests            the routing rules
```

Three decisions shape the whole thing:

**The server owns agent state.** The browser may *ask* to go ready; only the server decides what
state an agent is in. A client that disagrees with the platform about whether somebody is on a
call is the single most expensive bug class in this domain.

**The server sends a whole snapshot after every change.** No client-side reducers, no guessing
what changed, no screen drifting out of step. At 50 agents this is a few kilobytes a second. The
design document explains why a larger deployment sends targeted events instead.

**Routing runs after every change, inside one lock.** One instance, one `SemaphoreSlim`, so two
agents can never be handed the same call. The design document covers what replaces this when the
platform runs on more than one server.

The application is laid out the classic MVC way — an explicit `Program` with a `Main`, and a
`Startup` with `ConfigureServices` and `Configure` — rather than as top-level statements. What the
application depends on is in one method, the order middleware runs in is in the other, and neither
is tangled up with startup work.

Every endpoint is an MVC controller action — there are no minimal-API endpoints, not even
`/health`, and not one inline route lambda anywhere — so there is one HTTP surface to reason
about, one place where filters and
authorisation attributes will go when they are needed, and one error shape: a refused command
comes back as standard `ProblemDetails`, whose `detail` is written to be shown to the agent as-is.
Note what is *not* in the controllers: the rules. "You cannot finish a call without saying how it
ended" lives in the service and is covered by a test, not in a validation attribute that only runs
when the request happens to arrive over HTTP.

## What is not here, and why

The design document describes a production system. This is a few hours of work, and the gap is
deliberate rather than accidental:

| Not built | Where the real answer is |
| --- | --- |
| Authentication — you pick a name from a list | The company's existing identity provider |
| A telephony carrier — calls come from a button | The provider port, so the carrier can be swapped |
| CRM screen-pop | With the timeout and circuit breaker that keep phones working when the CRM is down |
| Skills-based routing, priorities, multiple queues | Here it is simply longest-waiting to longest-available |
| Outbound dialling, transfer, hold, conference | In the MVP scope, cut from the prototype |
| Recording, PCI pause, do-not-call enforcement | Compliance, which is a feature area of its own |
| Several instances, distributed routing, Redis backplane | The 50 → 500 agent path |
| The append-only event stream feeding AI | The prototype keeps one row per call instead |

Two things to be honest about in the code as it stands: `EnsureCreated` is the right tool for a
prototype and the wrong one for production, where the schema changes and needs migrations; and
the tests use an in-memory database, which is fast and proves the routing rules, but proves
nothing about SQL Server behaviour under concurrency.
