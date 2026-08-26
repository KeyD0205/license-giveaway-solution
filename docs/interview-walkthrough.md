# Interview walkthrough script

Speaking notes for the 50-minute review call. Not meant to be read verbatim — use it to
keep the structure straight and to have answers ready before the question lands.

---

## 1. Opening framing (30 seconds)

> "I'll walk through the design first, covering the seven requirements, then show the
> code and how it maps back to that design. I made a couple of deliberate scoping calls
> — I'll flag those as I go rather than let them come up as surprises."

---

## 2. Part A walkthrough (aim for ~10 minutes)

Walk the diagram top to bottom, tying every box back to a specific requirement number.
Suggested order:

1. **Edge (CDN/WAF/LB → requirement 7).** "Tens of thousands of concurrent requests in
   a short burst is a rate-limiting and absorption problem, not a compute-scaling
   problem — I want to stop bad/excess traffic before it reaches anything stateful."
2. **Stateless API tier (→ requirement 1, 7).** "Accepts email+phone, validates cheaply,
   and does the minimum possible work synchronously — it enqueues and returns
   immediately rather than doing allocation inline, so it can't become the bottleneck."
3. **Durable queue (→ requirement 5, 7).** "This is what makes the burst survivable —
   the API tier doesn't block on slow downstream work. It also gives me a concrete,
   defensible definition of 'first': the order the queue durably accepted the request,
   not physical click time, which no distributed system can actually observe."
4. **Allocation transaction (→ requirement 2, 3).** State the race explicitly, unprompted:
   "The bug to avoid is two workers both reading 'one license left' and both allocating
   it. The fix is making the decrement itself the atomic, conditional database
   operation — the losing transaction fails the condition and retries or is rejected,
   rather than reading state and deciding separately." Mention the two concrete
   implementations you'd choose between: `UPDATE licenses SET status='Allocated' WHERE
   status='Available'` inside a transaction (relational), or a conditional `UpdateItem`
   on a remaining-count item (DynamoDB-style key-value).
5. **Unique constraints (→ requirement 3).** "App-level dedup checks are a UX nicety
   only — they can't be the correctness guarantee once you have more than one API
   instance. The database unique constraint on normalized email/phone is the actual
   guarantee."
6. **PDF worker + private object storage (→ requirement 4, 6).** "Generation is
   decoupled from allocation so a slow or failing renderer can never cost someone their
   license or block the transaction. Delivery is a private bucket plus a short-lived
   signed URL, issued only after I've authorized the requester as the application
   owner." *(Be ready to define "authorized" concretely — see Q&A below, this is a
   known soft spot in the design.)*
7. **Status polling (→ requirement 6).** "Simplest thing that works for this scope;
   push notification (WebSocket/SSE) would be the upgrade with more time."

Close Part A with the honesty points before they're asked:
- Fairness is best-effort by construction, not a guarantee — say this yourself.
- The retrieval-authorization mechanism is sketched at the level the brief asks for,
  but you know exactly what's underspecified (no login system is implied anywhere) and
  have a concrete answer ready (below).

---

## 3. Part B walkthrough (aim for ~10 minutes)

1. Frame it first: "This is the in-memory stand-in the brief explicitly permits — the
   consistency boundary here is a single in-process lock, not a database transaction.
   Everything queue/DB/PDF-related is stubbed and labeled as such."
2. Walk `Apply` top to bottom:
   - Sequence stamp taken via `Interlocked.Increment` **before** the lock — "this is
     where I capture server-observed acceptance order, tying the code back to the
     fairness definition from Part A. It's stamped before the lock specifically because
     .NET's `Monitor`/`lock` isn't itself FIFO between waiting threads, so the ordering
     has to be captured explicitly rather than assumed."
   - The three-way branch inside `lock (_gate)`: duplicate check, capacity check,
     allocate — "this whole check-then-act sequence needs to be one atomic unit, which
     is exactly what the lock gives me here, and what the DB transaction would give me
     in production."
   - PDF stub dispatched **after** the lock releases — "so a slow or failing PDF job,
     even in this stub, can never contend with or block the allocation path — mirroring
     the decoupling in Part A."
3. Walk the response mapping: 202 Accepted, 409 Conflict for duplicate, 410 Gone for
   sold-out — "distinct 4xx codes for the two different rejection reasons, so a client
   can tell 'you're too late in general' apart from 'you specifically already have one'
   without parsing the body."
4. Walk the tests, and lead with the concurrency one: "the one I'd point to first is
   10,000 parallel calls asserting exactly 1,000 succeed — that's the test that
   actually exercises the race, not just the happy path."
5. State what you knowingly left out, unprompted: real input format validation
   (well-formed email/phone), and that license codes here are sequential/guessable
   (`DEMO-0001...`) which would be unacceptable in production.

---

## 4. Anticipated follow-up questions and answers

### Concurrency / correctness

**Q: What if two requests for the same email land on two different instances at the same instant?**
A: In production, the API tier is stateless and horizontally scaled, so "the same
instant" is normal, not an edge case. The in-memory exercise only has one process, so a
single lock is sufficient and correct there. In production the guarantee moves to the
database: a unique constraint on normalized email/phone means one transaction commits
and the other gets a constraint violation, which I map to 409. I wouldn't rely on an
app-level check surviving that race — it's a UX fast-path only.

**Q: Why not just use `Interlocked.Increment` on a plain counter instead of a lock?**
A: A single atomic counter would correctly cap the count at 1,000, but it can't also
enforce the email/phone uniqueness check atomically with the increment — I need one
atomic check-then-act unit covering both, which is what the lock (or, in production,
the transaction) gives me. A bare counter also stops being the consistency boundary the
moment you're not a single process, which you won't be at this traffic volume.

**Q: How would you prove the exactly-1,000 guarantee, beyond a unit test?**
A: The unit test (10,000 concurrent calls, assert exactly 1,000 accepted) is the
in-process proof. For the real system I'd want a load test that specifically hammers
the allocation transaction — not just the HTTP tier — with concurrency well above
expected peak, and assert the final row count/allocated-count in the database is
exactly 1,000 with zero duplicate emails/phones afterward.

### Fairness

**Q: Is this actually fair? What if two people click within the same millisecond?**
A: Not perfectly, and I don't claim it is. No distributed system can observe true
physical click order across a network with jitter and multiple ingress points. What I
can do is pin fairness to one concrete, defensible point: the order the system durably
accepted the request — the sequence number in the exercise code, or queue/DB
insertion order in production. That's an honest definition, not a perfect one.

**Q: Does your code actually demonstrate that ordering, or just claim it?**
A: It captures it — the `Sequence` field is stamped via `Interlocked.Increment` before
the lock is acquired, so every application has a recorded acceptance order. What it
doesn't do in this stub is *use* that order to process a backlog once the queue is
under contention — because there's no real backlog/worker separation in the in-memory
version, allocation happens synchronously inline. In production, the queue plus a
worker pool consuming it in order is what actually enforces the ordering under load.

### Security / secrecy

**Q: How does someone prove they're the license owner when retrieving the PDF?**
A: This is the part of the design I'd flag as needing a firmer answer with more time.
Since there's no login system implied anywhere in the brief, I'd treat the
`applicationId` as a bearer capability — like a pre-signed URL, knowledge of the
unguessable token *is* the authorization, and it's returned only to the submitter at
application time. If I wanted something stronger, I'd email a signed, time-limited
retrieval link to the applicant's own email address, since that's the one piece of
verified contact information the system already has.

**Q: What stops someone from brute-forcing application IDs to steal PDFs?**
A: They're GUIDs, not sequential, so brute-forcing the ID space isn't practical. In
the exercise's `GET /applications/{id}` there's no additional authentication layer
beyond knowing the ID — acceptable as a stand-in for the exercise's scope, but in
production I'd still rate-limit that endpoint and log access, since "hard to guess"
isn't the same as "authenticated."

**Q: This handles PII — email and phone. Anything to say about that, given WithSecure is a security company?**
A: Encrypt at rest, minimize logging of raw values (the app already normalizes and
could hash for logs), scope access to what services actually need it, and set a
retention/deletion policy rather than keeping applicant data indefinitely once the
campaign ends. I'd treat this as table stakes even though the brief doesn't ask for a
threat model explicitly.

### Availability / scaling

**Q: Why a queue instead of just scaling the API and DB horizontally?**
A: Scaling the API tier is necessary but not sufficient — the allocation step is
inherently a serialization point (you can't parallelize contention for the last
license away), so throwing more API instances at it doesn't help once you're
bottlenecked on that transaction. The queue's job is to let the API tier absorb the
burst at full speed and return a fast "accepted, processing" response, while the
allocation/worker tier drains the backlog at whatever rate the database can sustain
correctness at.

**Q: This is AWS + .NET in production here — how would you actually build this?**
A: CloudFront/WAF at the edge for the burst and basic bot filtering; the API tier as
either ECS/Fargate or API Gateway + Lambda depending on whether the team wants
long-running service parity with local dev or wants to lean into burst-then-idle
cost/scaling — I'd want to know which the team already standardizes on. SQS (standard,
not FIFO — FIFO's per-group throughput ceiling is well below "tens of thousands of
concurrent requests") to absorb the burst. RDS with a transactional atomic UPDATE (or
`SELECT ... FOR UPDATE SKIP LOCKED`) as the exactly-1,000 boundary, or DynamoDB with a
conditional `UpdateItem` if the team prefers NoSQL. S3 with block-public-access and
SSE-KMS for the PDFs, served via short-lived pre-signed URLs.

**Q: What if the PDF worker crashes mid-generation?**
A: The license allocation already committed before the PDF job was dispatched, so the
license itself is never lost or double-issued because of a downstream failure — that's
exactly why they're decoupled. The PDF job retries independently; status stays
`PdfGenerating` until it succeeds, or moves to a `PdfFailed` state with retry/backoff
if attempts are exhausted, without ever touching the allocation count.

### Persistence ("no DB required, a placeholder file is fine")

**Q: You're writing to a file now — walk me through that.**
A: `LicenseAllocator` takes an optional `persist` callback, defaulting to a no-op so
the unit tests stay in-memory-only and hermetic. `Program.cs`, as the composition
root, wires a real one that appends one JSON line per resolved application to
`applications.jsonl`. It's called after the lock releases, for the same reason the PDF
stub is dispatched there — a slow or blocked file write should never serialize behind
or jeopardize the actual allocation decision. It's guarded by its own separate lock
purely to stop concurrent writers from colliding on the file handle; that lock has
nothing to do with allocation correctness.

**Q: Doesn't writing after the lock releases mean you could lose a record — allocate in memory, then crash before the file write happens?**
A: Yes, and that's a real, honest gap in this stub — the in-memory dictionary and the
file can drift if the process dies between them. I chose this trade-off because it
keeps the already-tested allocation path completely untouched. The alternative — call
it Option A — is to make the file itself the consistency boundary: replace the
`Dictionary`/`HashSet` entirely with a JSON file, load it at startup, and rewrite it
*inside* the same lock as the allocation decision (via a temp-file-plus-atomic-rename
write, the same way you'd think about a DB transaction). That's the more faithful
answer to "use a file instead of a DB" — the file *is* the durability boundary — but it
reworks the allocation path I already have concurrency-tested, so I didn't take that on
given the time box. I can implement it live if you want to see it.

**Q: Which would you actually ship?**
A: Neither, exactly — in production this becomes the RDS/DynamoDB conditional-write
transaction from Part A. Between the two exercise-scale options, Option A is the more
honest answer to the specific instruction ("use a file instead of a DB"), since it
makes the file the actual boundary rather than a best-effort echo of one. I defaulted
to the safer stub here because I'd rather show you correct, tested allocation logic
than risk introducing a new bug in the last few minutes reworking it.

### Scope / trade-offs

**Q: What would you build next if given another hour?**
A: In order: (1) move the allocation boundary from the in-memory lock to a real
transactional store with the same atomic-conditional-update pattern, so the exactly-N
guarantee holds across multiple instances, not just one process; (2) wire the sequence
number into an actual ordered worker/backlog rather than just recording it; (3) real
input validation for email/phone format; (4) the signed-link retrieval-authorization
model instead of ID-as-bearer-token.

**Q: Why did you use AI tooling for parts of this, and how do you know the output is correct?**
A: *(Answer this one honestly and specifically to your own process — know exactly
which parts you generated vs. wrote by hand, and be ready to explain and defend every
line, per the brief's explicit instruction. Don't pre-script this one; it needs to be
true.)*

---

## 5. If you're running short on time

Priority order to hit if the conversation gets cut short: (1) the exactly-N atomicity
race and its fix, (2) the fairness definition and its honest limitation, (3) the
PDF/allocation decoupling, (4) the retrieval-authorization gap and your answer for it.
Those four are the ones most likely to be probed and most likely to distinguish a
senior answer from a mid-level one.
