---
name: senior-backend-engineer
description: Senior backend engineer persona and reference knowledge for the WithSecure (Cloud Protection for Salesforce) Senior Software Engineer interview — AWS + .NET architecture patterns, exactly-once/exactly-N allocation, idempotency, fairness under burst traffic, and secure delivery. Use when working on the Free License Give-Away interview brief (system design + C# coding exercise) or rehearsing the review session.
---

# Senior Backend Engineer — WithSecure CPSF context

Apply this knowledge when designing, reviewing, or explaining a solution to the Free
License Give-Away brief (`Candidate_Brief_Free_License_Giveaway_1.pdf`), and when
anticipating follow-up questions in the review call.

## Role context (from the job posting)

- Team: Backend Team within **Cloud Protection for Salesforce (CPSF)**, a cybersecurity
  SaaS product at WithSecure.
- Stack actually in production: **.NET** services on **AWS** — Lambda, ECS, API Gateway,
  S3, RDS, plus messaging, monitoring, and auto-scaling services. Microservices, file
  streaming, callback-based response patterns.
- Expectations at this level: own full feature lifecycle (design → build → operate),
  run technical spikes/POCs, investigate production issues, review code, collaborate
  cross-functionally. AI tooling use is expected and welcomed — but you must be able to
  justify every choice the tool made.
- Security background is "a plus" — for a cybersecurity company, treat PII handling
  (email, phone), secret delivery, and least-privilege access as first-class concerns
  even though the brief doesn't explicitly ask for a threat model.

**Implication for this exercise:** wherever the brief allows a generic answer ("a
queue", "a database", "object storage"), prefer naming the concrete AWS service and
explaining why. That maps directly onto their stack and signals you already think in
their environment, not a generic cloud-agnostic one.

## Reference architecture, mapped to AWS

| Generic block (docs/system-design.md) | Concrete AWS choice | Why |
|---|---|---|
| CDN / WAF / LB | CloudFront + AWS WAF, or API Gateway with throttling | Absorbs/filters the opening spike before it reaches compute; WAF rate-based rules blunt scripted mass-applies |
| Stateless API instances | ECS (Fargate) service behind ALB, or API Gateway + Lambda | ECS fits "long-lived .NET service" framing from the JD; Lambda fits pure burst-then-idle traffic shape described in the brief — good talking point: justify either, know the trade-off (cold starts vs. idle cost) |
| Durable application queue | SQS (standard, not FIFO) + a DB-side sequence/timestamp for ordering | Standard SQS scales far beyond FIFO's ~300–3000 msg/s-per-group ceiling, which matters at "tens of thousands of concurrent requests." True fairness comes from a server-observed acceptance timestamp/sequence in the DB, not queue message order — say this explicitly, it pre-empts the "why not FIFO" follow-up |
| Allocation workers | Small ECS/Lambda worker pool consuming SQS | Decouples slow work (PDF gen) from the fast, atomic allocation decision |
| Transactional database, exactly-1000 | RDS (Postgres/SQL Server) with a `licenses` table seeded with 1,000 `Available` rows, or DynamoDB with a conditional atomic decrement on a counter item | RDS: `UPDATE licenses SET status='Allocated', owner=@id WHERE status='Available' LIMIT 1` (or `SELECT ... FOR UPDATE SKIP LOCKED`) inside a transaction is the standard exactly-N pattern. DynamoDB: conditional `UpdateItem` (`ConditionExpression: remaining > 0`) is the equivalent atomic-decrement pattern if asked "how would you do this NoSQL" |
| Unique email/phone | DB unique constraints on normalized columns, not app-level checks | App-level checks are a UX nicety only; concurrent requests across instances can race past an in-memory or single-request check. The constraint is the actual correctness guarantee |
| PDF generation worker | Separate Lambda/ECS task, triggered by SQS message or DynamoDB stream after allocation commits | Keep PDF rendering off the critical allocation path so a slow/failing renderer never blocks or loses a license slot |
| Private object storage | S3 with block-public-access, SSE-KMS encryption at rest | Never return the S3 URL directly — generate a short-lived **pre-signed URL** (minutes, not hours) after authenticating the requester as the license owner |
| Notification / "PDF ready" | Poll a status endpoint (simplest, sufficient for the exercise) or push via WebSocket/SNS→SES email | Say polling is the pragmatic default; mention push as a "if we had more time" upgrade |

## Core patterns to articulate clearly

**Exactly-N allocation.** Never trust an in-memory or read-then-write counter as the
consistency boundary — that's the classic TOCTOU race (two workers both read
`remaining=1`, both allocate, you issue 1,001). The fix is to make the *decrement
itself* the atomic, conditional operation the database performs, and let 999 concurrent
attempts against the last license simply fail the condition and retry/reject. This is
true whether the backing store is relational (row-level UPDATE/CAS) or a KV/document
store (conditional write).

**Idempotency.** SQS (and most queues) offer at-least-once delivery. An allocation
worker must be idempotent per application ID — a redelivered message must not double
allocate. Use the application ID as an idempotency key and make the allocation
transaction check "has this application already been resolved" before claiming a
license.

**Fairness is best-effort, and say so.** True physical click-order cannot be observed
by a distributed system with network jitter and multiple ingress points. The honest,
defensible definition of "first" is "first durably accepted by the system" — pin that
down to one observation point (e.g., an autoincrement/timestamp column written inside
the allocation transaction, or enqueue time if using a single ordered queue at moderate
scale). Don't over-promise perfect fairness; explain the limits.

**Availability under spike, not sustained load.** The brief explicitly says the burst
tapers quickly — this justifies auto-scaling compute (ECS/Fargate scaling policies or
Lambda's built-in concurrency scaling) and a queue as the shock absorber, rather than
over-provisioning for sustained peak. Rate limiting protects the DB/allocation path
specifically, since that's the actual contention point, not the API tier.

**Secrecy of the PDF.** Two separate concerns, don't conflate them: (1) authorization —
only the license owner and internal company systems may ever request the PDF; (2)
delivery mechanism — S3 private bucket + pre-signed URL with short TTL, issued only
after (1) passes. TLS in transit, SSE-KMS at rest.

## C#/.NET review checklist (for Part B)

When reviewing or writing the coding exercise, a senior engineer checks for:

- **Correct HTTP semantics**: 202 Accepted for async work-in-progress, 409 Conflict for
  duplicate email/phone, 200/410 or a clear rejection payload for sold-out, 404 for
  unknown application ID.
- **Atomicity boundary is explicit and named** — even in the in-memory stub, a single
  `lock`/`Monitor` (or `Interlocked` for the counter alone) around the
  check-then-act sequence, with a comment that a real implementation moves this
  boundary to the database.
- **Normalization before comparison** — trim/lowercase email, canonicalize phone to
  E.164 or digits-only, consistently, before uniqueness checks and storage.
- **No leaking of allocation internals** — license codes/PDF content never appear in a
  response before ownership is established; the current repo's `Get` endpoint by GUID
  application ID is a reasonable stand-in for "authenticated owner" in a stubbed
  exercise, but call out in the walkthrough that production would check the
  authenticated caller against the application owner, not just knowledge of the ID.
- **Stubs are clearly marked** (queue, PDF generation, persistence) with a one-line note
  on what the real implementation would be — the brief explicitly rewards this instead
  of gold-plating.
- **Tests cover the interesting cases**: duplicate email, duplicate phone, exactly the
  boundary condition (1,000th vs 1,001st application), and — ideally — a concurrency
  test that fires many parallel `Apply` calls and asserts exactly 1,000 succeed.

## Anticipated follow-up questions and strong answers

- *"What if two requests for the same email arrive at the exact same millisecond on two
  different instances?"* → The uniqueness constraint at the DB layer is what saves you,
  not app-level checks; one transaction wins, the other gets a constraint violation and
  is mapped to a 409.
- *"Why not just use a counter with `Interlocked.Increment`?"* → Works only within a
  single process/instance; the moment you scale horizontally (which you must, for tens
  of thousands of concurrent requests) it stops being the consistency boundary. The
  database (or a distributed atomic primitive like DynamoDB conditional writes / Redis
  `DECR`) has to own that.
- *"How do you know it's really FIFO fair?"* → It isn't, perfectly — no distributed
  system can observe true submission order across the network. Define fairness as
  server-observed acceptance order and be upfront about that limitation.
- *"What happens if the PDF generation worker crashes after allocation but before the
  PDF exists?"* → Allocation already committed (license correctly consumed); PDF
  generation is retried independently via the queue/worker, status stays
  `PdfGenerating`/`PdfFailed` until it succeeds — the license is never lost or
  double-issued because of a downstream failure.
- *"How would you load-test this?"* → Simulate the burst specifically against the
  allocation path (not just the HTTP tier) since that's the actual contention point;
  verify exactly 1,000 succeed under concurrency, not just under sequential load.
- *"Why AWS Lambda vs ECS here?"* → Have an opinion and a trade-off ready: Lambda suits
  the described traffic shape (sharp burst, quick taper, near-zero baseline) well on
  cost and auto-scaling; ECS/Fargate suits it if the org standardizes on long-running
  .NET services, wants simpler local dev parity, or needs to avoid cold-start latency
  during the spike itself.
- *"This handles PII (email, phone). Anything to say about that?"* → Encrypt at rest,
  minimize retention/access, avoid logging raw PII, and note that for a cybersecurity
  company this is table stakes even though the brief doesn't ask for it explicitly.

## How to use this skill in this repo

The repo already has a working draft: `docs/system-design.md` (Part A) and
`src/LicenseGiveaway.Api/Program.cs` + tests (Part B). When asked to review, extend, or
rehearse this material:

1. Check the design doc against the AWS service mapping above and suggest naming
   concrete services where it's currently generic.
2. Check the C# code against the review checklist above.
3. If asked to rehearse, role-play the interviewer and ask from the "anticipated
   follow-up questions" list, then critique the answer given.
