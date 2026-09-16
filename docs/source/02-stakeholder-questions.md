# 2. Stakeholder Questions

> **Purpose:** the questions I would ask before committing to a design or a date.
> **Format:** every question states *why I am asking* and *what changes in the design
> depending on the answer.* A question whose answer changes nothing is not worth a
> stakeholder's time.
>
> I have also recorded **my working assumption** for each, so that work is not blocked while
> we wait for answers. Silence = the assumption stands, and the risk is on record.

**Blocker key**
🔴 **Blocker** — I cannot responsibly start building without this.
🟠 **Shapes the design** — I can start, but a late answer causes rework.
🟡 **Shapes the plan** — affects estimates, sequencing or operations, not structure.

---

## A. Commercial reality — is this the right thing to build at all?

| # | Question | Why I am asking | What changes | My assumption |
| --- | --- | --- | --- | --- |
| **Q1** 🔴 | What are we paying the incumbent today — per seat, per minute, platform fee, support — and what is left in the contract term? | "Stop using a third-party tool" implies the current tool is too expensive or too limiting. I cannot tell whether "build" wins without a baseline. Carrier minutes + cloud + 2–3 permanent engineers is a real number | If the licence is cheap and the pain is *features*, the right answer may be a smaller integration project rather than a platform build. I would rather say that in week 1 than month 9 | Per-seat licence is the main cost and is material |
| **Q2** 🔴 | Is the primary driver **cost**, **control/data ownership**, **specific missing features**, or **vendor risk**? Rank them | These lead to genuinely different architectures. Cost → minimise build, thin layer over CPaaS. Control → own the data model and event stream even at higher cost. Features → identify the 3 features and check they can't be bought | Determines whether we optimise for cheapest-to-run or most-flexible | Control + data ownership first, cost second |
| **Q3** 🟠 | What is the budget envelope and the ongoing ownership model? Who runs this at 2 a.m. in year 2? | A platform you cannot operate is worse than a licence you resent. This needs a permanent on-call team, not a project team that disbands | If there is no long-term team, I would push for managed services aggressively (managed DB, managed queue, CPaaS) even at higher unit cost | 3–5 engineers to build, 2–3 to run, plus existing SRE on-call |
| **Q4** 🟡 | Is there a hard deadline tied to the incumbent contract's expiry? | Contract end dates are the most common cause of a rushed, unsafe cutover | A hard date forces the MVP to shrink and the parallel-run window to be protected first, not last | ~6 months to pilot; no cliff edge |

---

## B. Volumes — the numbers that size everything

| # | Question | Why I am asking | What changes | My assumption |
| --- | --- | --- | --- | --- |
| **Q5** 🔴 | Inbound calls per day, and the **busy-hour** distribution? Peak vs average? Seasonal spikes (campaigns, billing cycles, outages)? | Every capacity number in NFR-S is currently invented. Peak-to-average ratio drives autoscaling design far more than totals | Determines instance counts, DB sizing, whether a message broker is needed in v1, and the carrier's concurrent-channel commitment | 3,000 inbound calls/day at 50 agents; peak hour = 18% of daily volume |
| **Q6** 🔴 | Average handle time and after-call-work time today? Outbound-to-inbound ratio? | AHT × volume = concurrent calls = carrier channels = the largest recurring cost line | Drives trunk sizing and recording storage. A 12-minute AHT is a very different system from 3 minutes | AHT 5 min, ACW 45 s, 70:30 inbound:outbound |
| **Q7** 🟠 | What is the realistic 500-agent timeline — 18 months or 5 years? Is growth by hiring, acquisition, or new business units? | "Later" is doing a lot of work in the brief. Acquisition implies multi-tenancy and multi-region much sooner | If 500 is 5 years away I build a modular monolith and defer partitioning. If it is 18 months I invest in partitioning and a broker in v1 | 500 agents in 24–36 months, organic growth, one business unit |
| **Q8** 🔴 | Do we have existing carrier relationships, SIP trunks, or a PBX? Any contractual commitment to a telephony vendor? Is self-hosting media (Asterisk/FreeSWITCH) a requirement, a preference, or off the table? | This is *the* architectural fork. It determines cost model, latency profile, hiring needs and timeline | CPaaS → 4–6 months to pilot. Self-hosted SBC/media → 12+ months and specialist hires. Both can be supported behind the same port, but only one can be first | CPaaS (Twilio/Vonage-class) for v1, port kept open |

---

## C. Telephony & numbers

| # | Question | Why I am asking | What changes | My assumption |
| --- | --- | --- | --- | --- |
| **Q9** 🔴 | Which DIDs are in use today, who is the current number owner, and can they be ported? What is the porting lead time in our markets? | Porting is routinely the critical path and it is entirely outside engineering's control | Determines the cutover plan and the go-live date. May force a "new numbers + forward" pilot strategy | ~20 DIDs, portable, 4–8 weeks lead time |
| **Q10** 🟠 | What happens today outside business hours, on holidays, and during overflow? Voicemail? Answering service? Follow-the-sun? | Out-of-hours behaviour is always forgotten in the brief and always noticed on day 1 of go-live | A simple open/closed rule is an afternoon of work; follow-the-sun across time zones is a routing-model change | Business hours per queue + holiday calendar + closed announcement; voicemail is phase 2 |
| **Q11** 🔴 | Where do agents physically sit — office, home, or both? What is their network like? Are they on managed desktops? Is a browser softphone acceptable, or are desk phones mandated? | WebRTC quality is a network problem. The answer determines whether we need a SIP hardphone path at all, which is significant added scope | Browser-only → simplest. Hardphone support → we must integrate device control (a different, harder integration) | Browser softphone + USB headset; mix of office and home; managed Windows desktops |
| **Q12** 🟠 | Do agents need to make calls to international/premium destinations? What are acceptable spend limits? | Toll fraud is a real, expensive risk (NFR-SEC8) | Determines destination allow-lists and whether spend caps are v1 or v2 | Domestic + a short list of countries; hard per-agent daily cap |

---

## D. CRM integration — the highest-value, highest-risk integration

| # | Question | Why I am asking | What changes | My assumption |
| --- | --- | --- | --- | --- |
| **Q13** 🔴 | Which CRM, which version, cloud or on-prem? Can I get sandbox credentials and API docs **this week**? | "Our existing CRM" is the vaguest sentence in the brief and the biggest unknown effort. Salesforce, Dynamics, and a homegrown PHP app are three completely different projects | Directly determines the integration design, the screen-pop mechanism (embedded softphone vs standalone app), and 4–8 weeks of estimate variance | Cloud CRM with a documented REST API and OAuth2 |
| **Q14** 🔴 | Can the CRM be searched by phone number efficiently? Is the number field indexed and normalised (E.164)? How many contacts are there? What are the API rate limits? | A screen-pop in < 2 s is impossible if the lookup takes 4 s or we get rate-limited at peak. This single answer can add a whole data-sync subsystem | If lookup is slow or limited: build a local read-model of number → contact, synced by CDC/webhooks or nightly batch. That is 3–4 weeks of extra work | Indexed search, < 300 ms, generous rate limits |
| **Q15** 🟠 | Should the agent desktop live **inside** the CRM (embedded softphone panel) or be a **separate** application beside it? | This is a UX and architecture decision, not a preference. Embedding usually gives the best agent experience but ties us to the CRM's extension framework and its release cycle | Embedded → CRM-specific frontend work, lower agent friction. Standalone → portable, but agents alt-tab and we must drive the CRM's UI remotely | Standalone Angular app in v1, with a documented path to embedding |
| **Q16** 🟠 | What exactly must be written back to the CRM on call end, and is there an existing activity/task object we must match? Who owns that data model? | Write-back schema mismatches cause silent data corruption that surfaces in someone's quarterly report | Determines the ACL mapping and how much of the taxonomy (dispositions) we must adopt versus define | Standard Activity/Task object; direction, duration, outcome, agent, recording link, notes |
| **Q17** 🟡 | Does historical call data from the incumbent need migrating into the new platform, or is an archived export acceptable? | Migrating another vendor's call history — with different KPI definitions — is a project in its own right and rarely worth it | Migration → new workstream, 4+ weeks, plus KPI reconciliation pain | Export and archive; no migration into the live system |

---

## E. Routing & operating model — how the floor actually works

| # | Question | Why I am asking | What changes | My assumption |
| --- | --- | --- | --- | --- |
| **Q18** 🔴 | How are calls routed **today**? How many queues, skills, and routing rules exist? Can I see the current configuration? | The fastest way to get the requirements right is to read the incumbent's config. It is the de-facto spec, written by the people who live with it | If routing is genuinely complex (dozens of skills, conditional rules), a rules engine is warranted. If it is 4 queues and 6 skills, a simple policy engine is correct and far cheaper | ≤ 10 queues, ≤ 20 skills, skills-based + priority, no exotic conditional logic |
| **Q19** 🟠 | When several agents are eligible, who should get the call — longest idle, highest skill proficiency, least calls handled today, or preferred/last agent? | This is business policy disguised as an algorithm. Getting it wrong is visibly unfair to agents and is a morale issue, not just a technical one | Determines the selection policy interface and how configurable it must be. I will build it as a strategy either way, but the default matters | Longest-idle by default, with proficiency as a tiebreaker; configurable per queue |
| **Q20** 🟠 | What should happen when nobody answers (RNA)? Re-queue at the front or the back? Should the agent be auto-set Not-Ready? After how many RNAs is the agent logged out? | RNA policy determines whether one distracted agent can silently degrade a whole queue | Small change in code, large change in floor behaviour. Ops must own this | Re-queue at front with priority boost; agent auto Not-Ready(`RNA`); auto-logout after 3 consecutive |
| **Q21** 🟡 | What is the wrap-up (ACW) policy — unlimited, timed, or mandatory disposition before returning to Available? | Drives whether ACW is a state the agent controls or the system enforces, and it is a live reporting metric | Mandatory disposition changes the desktop flow and blocks the agent until complete | Mandatory disposition, 120 s soft timeout with supervisor alert |
| **Q22** 🟡 | What is the current Service Level target, and precisely how is it calculated (threshold seconds, and are short abandons excluded)? | R-13: reporting mismatches destroy trust in a new platform faster than bugs do. "80/20" means different things in different tools | Determines the SL formula, the interval model, and what we must reconcile during parallel run | 80% answered within 20 s; abandons < 5 s excluded |
| **Q23** 🟠 | Are there VIP/priority customers, and what determines priority — the dialled number, the IVR selection, or a CRM attribute? | If priority comes from the CRM, a CRM lookup sits **in the inbound call path** — a latency and availability dependency on a system we do not control | CRM-in-path requires a cache and a strict timeout with a safe default. This is an availability-critical design detail | Priority by dialled number and IVR choice in v1; CRM-attribute priority in phase 2 |

---

## F. Compliance, security & data

| # | Question | Why I am asking | What changes | My assumption |
| --- | --- | --- | --- | --- |
| **Q24** 🔴 | Which countries/jurisdictions do we call and receive calls from? What is the legal position on recording consent in each (one-party, two-party, announcement required)? | Recording law varies per jurisdiction and per call direction. Getting it wrong is a legal exposure, not a bug | Determines whether recording announcements are mandatory, whether we need per-jurisdiction policy, and whether recording can be default-on | Single primary jurisdiction; announcement required; recording default-on for inbound |
| **Q25** 🔴 | Do agents take **card payments** over the phone? | This decides whether PCI-DSS is in scope, which affects recording, logging, network segmentation and audit | Yes → pause/resume is mandatory in MVP (FR-G3) and DTMF must never be logged. Ideally we route payments to a DTMF-masking IVR or an external payment link | Yes, occasionally; manual pause/resume in MVP |
| **Q26** 🟠 | How long must recordings and call records be retained? Is there a regulator-mandated minimum, and a privacy-mandated maximum? | Retention drives storage cost (NFR-S6) and the deletion pipeline. "Forever" is not a retention policy, it is a liability | 7 years vs 90 days is a ~30× difference in storage cost and a different tiering design | 12 months hot/warm, 7 years archived, configurable per queue |
| **Q27** 🟠 | Where may customer data and recordings physically reside? Is a specific cloud region or on-prem mandated? Is a specific cloud provider mandated? | Residency constrains region selection, CPaaS choice and DR topology | Can eliminate certain CPaaS vendors entirely; may force a self-hosted media path (back to Q8) | Single region in our primary market; major public cloud acceptable |
| **Q28** 🟡 | Do we have a corporate IdP (Entra ID / Okta) with OIDC, and who administers group membership? Can agent skills be driven from directory groups? | Authentication is table stakes; the interesting part is whether agent/skill provisioning can be automated rather than re-keyed | Directory-driven provisioning removes an admin burden at 500 agents. Manual provisioning at that scale is a full-time job | Entra ID with OIDC; roles from groups; skills managed in-platform |
| **Q29** 🟠 | Who is allowed to listen to recordings, and does that need approval/justification capture? Who may export call data? | Recording access is the most commonly abused privilege in a contact centre | Determines whether access needs a reason-code workflow and four-eyes approval, or just role checks + audit | Role-based + full audit in v1; approval workflow in phase 2 |

---

## G. Operations, reporting & change management

| # | Question | Why I am asking | What changes | My assumption |
| --- | --- | --- | --- | --- |
| **Q30** 🟠 | Which reports do Ops actually use today, and which ones drive decisions versus which are just generated? Can I see them? | Reporting is where most contact-centre projects overrun, because everyone asks for "the same as before, plus more" | Determines the reporting data model and whether we need a warehouse in v1 or can serve from a read replica | ~6 standard reports + CDR search + CSV export |
| **Q31** 🟠 | Who will own day-to-day configuration — queues, skills, hours, dispositions? Do they want a self-service admin UI, and are they capable of using it safely? | FR-A4 exists for a reason. If Ops cannot self-serve, engineering becomes a ticket queue forever | An admin portal is a real slice of MVP effort. Cutting it saves weeks but costs years | Ops team owns config; admin portal is in MVP |
| **Q32** 🟡 | What are supervisors' must-have live controls — listen, whisper, barge, force-logout, reassign? Which are used daily today? | Supervisor tooling is frequently over-specified. Listen/whisper/barge need carrier support and add real cost | Determines whether these are v1 or v2. I currently have barge in phase 2 | Live wallboard + force-logout in v1; listen/whisper/barge in phase 2 |
| **Q33** 🟡 | What is the acceptable maintenance window, and is the centre 24×7? | Determines whether we need true zero-downtime deployment on day 1 or can deploy in a quiet window | 24×7 → blue/green and drain-based deploys are mandatory in v1, not a phase-2 nicety | Business hours, 6 days/week; deployments outside hours; but I still design for zero-downtime |
| **Q34** 🟠 | How do we want to cut over — big bang, by team, or by queue? Is a parallel-run period acceptable (running both platforms with split traffic)? | A big-bang voice cutover is the single most dangerous thing we could do here | Parallel run needs split DID routing at the carrier and doubles operational effort for a few weeks — but it is what makes the cutover safe and reversible | Phased by team, with a 2–4 week parallel run and carrier-level DID split |
| **Q35** 🟡 | What are the top 3 things agents hate about the current tool? | The fastest route to adoption. Fixing three daily irritations buys more goodwill than any feature | Directly shapes the agent desktop backlog and the pilot success criteria | Unknown — I want to sit with agents for half a day before designing the desktop |

---

## H. Future-facing — AI and roadmap

| # | Question | Why I am asking | What changes | My assumption |
| --- | --- | --- | --- | --- |
| **Q36** 🟡 | For AI, what is the *first* business outcome you want — shorter wrap-up (auto-summary), quality coverage (auto-scoring), deflection (self-service bot), or better routing (intent-based)? | "AI features" is not a requirement. Each of those has a different data and latency profile — real-time streaming transcription is a very different system from batch post-call summarisation | Determines whether we need **streaming** media access from the carrier (a hard constraint that affects CPaaS selection **now**) or only post-call recordings ([AI-Readiness §6.3](06-ai-readiness.md)) | Post-call summarisation first (batch, cheap, low-risk), real-time assist later |
| **Q37** 🟠 | Is there an approved position on sending customer audio/transcripts to a third-party AI service, and are there data-processing agreements in place? | This can veto an entire class of solution after we have designed for it | May force self-hosted models or in-region managed services — a large cost and latency difference | In-region managed AI service with a DPA; no data used for vendor training |
| **Q38** 🟡 | Is omnichannel (chat, email, WhatsApp) on the 2-year roadmap? | Voice-only and omnichannel differ in the core domain model, not just the UI. A universal-queue design is cheap to allow for now and expensive to retrofit | I have already modelled `Interaction` rather than `Call` for this reason — confirming it is deliberate, not accidental | Likely within 2 years; voice-only in this programme, model kept channel-agnostic |
| **Q39** 🟡 | Will other business units or subsidiaries use this platform? | Decides how seriously to take multi-tenancy | A "yes" makes `TenantId` a real feature rather than a cheap insurance policy | One business unit; `TenantId` present but unused (§1.8) |

---

## The five I would ask first, in one meeting

If I could only ask five questions before starting, these are the five whose answers I
cannot proceed responsibly without:

1. **Q8** — Do we have existing carrier/SIP infrastructure, and is self-hosting media in or
   out? *(the architectural fork)*
2. **Q13 + Q14** — Which CRM, and can it be searched by phone number fast enough for a 2 s
   screen-pop? *(the largest estimate variance, and the headline UX promise)*
3. **Q5 + Q6** — Real call volumes, busy hour and AHT. *(everything is sized on invented
   numbers until then)*
4. **Q24 + Q25** — Recording consent law and whether card payments are taken by phone.
   *(compliance constraints are cheap now, catastrophic later)*
5. **Q2** — Is the real driver cost, control, or features? *(decides whether we should build
   this at all, and if so, what "good" looks like)*

---

**Previous:** [← Requirement Analysis](01-requirement-analysis.md) ·
**Next:** [MVP Definition →](03-mvp-definition.md)
