# Part A — System Design

## Goals

1. Accept email and phone applications.
2. Issue exactly 1,000 licenses.
3. Never issue more than one license to the same email or phone.
4. Keep generated PDFs private.
5. Process applicants as fairly as possible according to server-side acceptance/queue order.
6. Let the UI know when a PDF is ready and retrieve it.
7. Keep the front end/API responsive during the large opening burst.

## Architecture

```text
                          +----------------+
                          |    Browser     |
                          +-------+--------+
                                  |
                                  v
                         +------------------+
                         | CDN / WAF / LB   |
                         +--------+---------+
                                  |
                 +----------------+----------------+
                 |                |                |
                 v                v                v
             +-------+        +-------+        +-------+
             | API 1 |        | API 2 |  ...   | API N |
             +---+---+        +---+---+        +---+---+
                 \                |                /
                  \_______________|_______________/
                                  |
                                  v
                       +-----------------------+
                       | Durable Application   |
                       | Queue                 |
                       +-----------+-----------+
                                   |
                                   v
                       +-----------------------+
                       | Allocation Workers    |
                       +-----------+-----------+
                                   |
                         atomic transaction
                                   |
                                   v
                       +-----------------------+
                       | Database              |
                       |                       |
                       | Applicants            |
                       | UNIQUE email          |
                       | UNIQUE phone          |
                       |                       |
                       | Licenses              |
                       | exactly 1,000 rows    |
                       +-----------+-----------+
                                   |
                                   v
                       +-----------------------+
                       | PDF Generation Worker  |
                       +-----------+-----------+
                                   |
                                   v
                       +-----------------------+
                       | Private Object Store  |
                       +-----------+-----------+
                                   |
                                   v
                       authenticated / short-lived
                              PDF retrieval
```

## Request flow

1. The browser submits email + phone.
2. The edge layer absorbs malicious traffic and distributes requests.
3. Stateless API instances validate input and enqueue an application.
4. The queue buffers the burst. The durable enqueue order is the practical definition of "first served".
5. An allocation worker processes applications.
6. In one database transaction, the system:
   - checks/enforces email uniqueness,
   - checks/enforces phone uniqueness,
   - claims one unallocated license atomically.
7. If no license remains, the application is marked unsuccessful.
8. If a license is allocated, the PDF job is submitted asynchronously.
9. The PDF is stored in private object storage.
10. The UI observes status (polling is sufficient for the exercise; SSE could provide push-style updates).
11. Download requires authorization to the application/license owner, or a short-lived signed URL issued after authorization.

## Exactly 1,000

Do not use an API-local counter as the source of truth.

A production implementation should make the database transaction the consistency boundary. For example, store exactly 1,000 license records and atomically change one from `Available` to `Allocated`.

Two concurrent workers can both observe that a license appears available, but only one transaction can successfully claim the same row.

This prevents the classic race:

```text
worker A: sees 999 allocated
worker B: sees 999 allocated
worker A: allocates
worker B: allocates
```

The allocation operation itself must be atomic.

## Duplicate prevention

The database should enforce:

```text
UNIQUE(normalized_email)
UNIQUE(normalized_phone)
```

Application-level checks are useful for fast feedback, but they are not sufficient because multiple API instances can process concurrent requests.

Normalization should be explicit and consistent (for example, trim and lowercase email; normalize phone numbers to a canonical international representation).

## Fairness

Perfect physical click ordering cannot be observed by a distributed service.

Define fairness as the order in which valid requests are durably accepted by the ingress/queue. A durable FIFO queue gives a defensible ordering model.

If multiple requests arrive at effectively the same instant, the system can only establish an ordering at some server-side observation point.

## Availability and latency

The API tier should be stateless and horizontally scalable.

The durable queue absorbs the opening spike so API requests don't wait for PDF generation or slow downstream processing.

Rate limiting/WAF/bot controls protect capacity. The queue and worker tier can drain the burst after the opening moment.

## PDF security

PDFs must not be public objects.

Use private object storage with encryption at rest. The application should authorize the requesting user before returning a PDF or issuing a short-lived signed download URL.

TLS protects data in transit.

## Failure handling

- Queue delivery: retry with acknowledgement/dead-letter semantics.
- Allocation: make the transaction atomic.
- PDF generation: retry independently; allocation should not be lost if PDF generation fails.
- Duplicate delivery of a queue message: use an application ID/idempotency key and make allocation processing idempotent.
- API instance failure: state is durable outside the API process.

## Status model

```text
Received
   |
   v
Queued
   |
   +--------------------> Rejected (no license / duplicate)
   |
   v
Allocated
   |
   v
PdfGenerating
   |
   v
Ready
```

A separate `PdfFailed` state can be used if retries are exhausted.

## Production discussion points

The interview coding task explicitly allows the queue, database and PDF generation to be stubbed. The production architecture above is therefore intentionally more complete than the C# exercise.
