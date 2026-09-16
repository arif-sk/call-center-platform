# Call Center Platform — Submission Index

**Role:** System Analyst / Solution Architect
**Exercise:** design an in-house inbound + outbound call center platform to replace a
third-party tool — CRM-integrated, 50 agents today, 500+ later, AI-ready.

---

## The documents

| # | Document | What it answers |
| --- | --- | --- |
| 1 | [Requirement Analysis](01-requirement-analysis.md) | Business goals, stakeholders, assumptions, functional & non-functional requirements, risk register, out of scope |
| 2 | [Stakeholder Questions](02-stakeholder-questions.md) | 39 questions, each with *why I'm asking*, *what changes based on the answer*, and my working assumption so nothing is blocked |
| 3 | [MVP Definition](03-mvp-definition.md) | What's in v1, what's cut and what each cut costs to add later, the plan, and the success/kill criteria |
| 4 | [System Design](04-system-design.md) | Components, critical flows, data model, communication patterns, security, failure modes — with diagrams |
| 5 | [Scalability Plan](05-scalability-plan.md) | What actually breaks between 50 and 500 agents, and how the design absorbs 10× |
| 6 | [AI-Readiness Notes](06-ai-readiness.md) | The seams that make AI additive — and the one decision that can't be deferred |
| 7 | [Deployment Strategy](07-deployment-strategy.md) | Dev → staging → production, CI/CD, canary, rollback, backups, DR, and the cutover |

A working **.NET 8 prototype** accompanies these documents — see
[`../prototype/`](../prototype/README.md). It is not part of the written submission; it
exists to demonstrate the design decisions live.

---

## If you only read three things

1. **[§1.1 — What we build vs. what we buy](01-requirement-analysis.md#11-the-ask-restated).**
   We are building a *contact routing system*, not a *telephony system*. Almost every other
   decision follows from that sentence.

2. **[§3.5 — The invisible v1 items I refuse to cut](03-mvp-definition.md#35-the-invisible-v1-items-i-refuse-to-cut).**
   Nine things that ship no visible feature and are far more expensive to retrofit than to
   build. This is where the 50-agent system either does or does not become a 500-agent
   system.

3. **[§4.3.2 — The reservation protocol](04-system-design.md#432-routing-engine--the-heart).**
   The correctness core: how a call is offered to exactly one agent, exactly once, and what
   happens when that goes wrong. It is implemented and tested in the prototype.

---

## The thread running through all seven documents

> **The phones must keep working when anything else is broken.**

- The CRM going down must not stop calls → circuit breaker, cache, durable retry (§4.3.5)
- A routing worker dying must not strand calls → leases, Redis-held state, ~5 s recovery (§4.3.2)
- A deployment must not drop calls → media is carrier-side; routing drains and hands over (§7.3)
- A bad release must be reversible in minutes → canary by agent cohort, automated gates (§7.5)
- A cutover must be reversible in one change → carrier-level DID split (§7.8)

---

## Reading the diagrams

All diagrams are Mermaid and render natively on GitHub, GitLab, and in VS Code with the
Markdown Preview Mermaid extension.
