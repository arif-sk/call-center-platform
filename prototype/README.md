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

Open <http://localhost:5080>. The database is created and migrated on startup, and five agents
are seeded — there is no setup script to run first.

Point it somewhere else by editing `ConnectionStrings:CallCenter` in
`src/CallCenter.Api/appsettings.json`.

```bash
dotnet test        # 35 tests, ~1 second, no database required
```

The compiled Angular app is committed into the API's `wwwroot`, so the command above serves the
UI without an `npm install`. To change the frontend:

```bash
cd src/CallCenter.Web
npm install
npm start          # dev server on :4200, proxied to the API
npm run build      # rebuilds into ../CallCenter.Api/wwwroot
```

### Changing the schema

The schema is owned by EF Core migrations in
`src/CallCenter.Infrastructure/Persistence/Migrations`. After editing an entity or its
configuration:

```bash
dotnet ef migrations add <Name>   --project src/CallCenter.Infrastructure   --startup-project src/CallCenter.Api   --output-dir Persistence/Migrations
```

The migration lives with the `DbContext` in the infrastructure layer; the API is only named
because it is the project the tooling builds to find the connection string. To produce the SQL for
a DBA to run against a real environment rather than letting the application apply it:

```bash
dotnet ef migrations script --idempotent   --project src/CallCenter.Infrastructure   --startup-project src/CallCenter.Api
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

Four projects, and the dependencies only ever point inwards.

```
src/CallCenter.Domain            the rules. References nothing at all
  Agents/Agent.cs                the agent state machine
  Calls/Call.cs                  the life of a call, and its timings
  DomainException.cs             a rule was broken — not a fault

src/CallCenter.Application       the use cases. References only the domain
  Services/CallCenterService.cs  which call goes to which agent, and in what order
  Abstractions/                  the interfaces infrastructure has to satisfy
  Contracts/Snapshot.cs          what the screens are told, as opposed to what we store

src/CallCenter.Infrastructure    the outside world. Implements the abstractions
  Persistence/                   EF Core, the two repositories, the unit of work
  Persistence/Migrations/        the schema, versioned
  Realtime/                      the SignalR hub and publisher
  DependencyInjection.cs         registered in one call

src/CallCenter.Api               presentation. Talks to the application layer
  Program.cs, Startup.cs         host and composition root
  Controllers/  Models/  Filters/

src/CallCenter.Web               Angular: sign-in, agent desktop, supervisor console
tests/CallCenter.Tests           one project, grouped by the layer each test exercises
```

`Startup` is the only file that mentions infrastructure, and it mentions it once. Nothing in
`Controllers/` knows the database is SQL Server; nothing in `Application/` knows EF Core exists;
`Domain` has no package references whatsoever. That claim is not left to the reader — four
[dependency-rule tests](tests/CallCenter.Tests/Architecture/DependencyRuleTests.cs) read the
assembly references and fail the build if an arrow ever points outwards.

### The rules live on the entities

There is no way to reach an `Agent` and assign `State`, because every setter is private and every
transition is a method that refuses what does not make sense:

```csharp
public void SetReady(bool ready, DateTimeOffset now)
{
    if (IsHandlingCall)
    {
        throw new DomainException($"Cannot change availability while {State}.");
    }

    ChangeState(ready ? AgentState.Available : AgentState.NotReady, now);
}
```

So the state machine cannot be bypassed by a caller written next year, and thirteen tests cover
those rules with no database, no web server and no test doubles — the domain project references
nothing, so nothing is needed to test it.

The application service is left with the part that is genuinely its own: *which* call goes to
*which* agent, and in what order things happen.

### Three decisions behind the behaviour

**The server owns agent state.** The browser may *ask* to go ready; only the server decides what
state an agent is in. A client that disagrees with the platform about whether somebody is on a
call is the single most expensive bug class in this domain.

**The server sends a whole snapshot after every change.** No client-side reducers, no guessing
what changed, no screen drifting out of step. At 50 agents this is a few kilobytes a second. The
design document explains why a larger deployment sends targeted events instead.

**Routing runs after every change, inside one lock.** One instance, one `SemaphoreSlim`, so two
agents can never be handed the same call. The design document covers what replaces this when the
platform runs on more than one server.

### The HTTP layer

Laid out the classic MVC way — an explicit `Program` with a `Main`, and a `Startup` with
`ConfigureServices` and `Configure` — rather than as top-level statements.

Every endpoint is an MVC controller action. There are no minimal-API endpoints, not even
`/health`, and not one inline route lambda anywhere. The controllers are written the conventional
way: constructor injection into `private readonly` fields, one `[Http...]`-attributed method per
action with a full body, declared response types, and bound request models from `Models/`. They
return `IActionResult`, with the response type declared by attribute so the generated OpenAPI
still names `Snapshot` and `ProblemDetails` rather than falling back to an untyped body.

They contain no `try`/`catch`. The domain throws `DomainException` and knows nothing about status
codes; `DomainExceptionFilter`, registered once in `Startup`, is the single place that decides
such a refusal is a 400. Same shape wherever it is thrown, and a new action cannot forget the
contract. The `detail` field is written to be shown to the agent as-is, and the `traceId` ties a
complaint to a log line. A failure that is *not* a broken rule is deliberately left alone, so it
still surfaces as a 500 instead of being dressed up as a polite refusal.

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

Two things to be honest about in the code as it stands. The application migrates the database on
startup, which suits one instance: several starting at once would race, so a real deployment runs
migrations as their own step before the new version starts — having them as migrations rather than
`EnsureCreated` is what makes that possible. And the tests use an in-memory database, which is
fast and proves the routing rules, but proves nothing about SQL Server behaviour under
concurrency.
