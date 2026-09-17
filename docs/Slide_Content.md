# NovaWallet Ledger Service — Slide Content
### FirstBank NovaPay · Backend Engineer Assessment · 12 Slides · 10 Minutes

> **How to read this document**
> Each slide shows exactly what appears on screen under `### ON SCREEN`,
> followed by the speaker notes in a `> blockquote`.
> Every slide has a **Reviewer takeaway** line — the single thing a cold reader
> should leave that slide knowing. This is what makes the deck readable without a speaker.

---
---

## SLIDE 1 — Title

**Reviewer takeaway:** *This is a production-serious wallet ledger built for FirstBank NovaPay — not a tutorial project. The candidate understands what's at stake.*

---

### ON SCREEN

```
🐘  FirstBank NovaPay — Digital Factory

NovaWallet Ledger Service
Backend Engineer Case Study · .NET 9 / C#
September 2026 · Babatunde Oladejo
```

> **"The component that must never lose, duplicate, or miscount a customer's money."**
> — That is the single requirement this entire service is built around.

---
Built in C# / .NET 9 · PostgreSQL · Docker · JWT · RFC 7807

---

> Good morning / afternoon, thank you for your time.
>
> I'm going to walk you through the NovaWallet Ledger Service — a concurrency-safe,
> production-realistic wallet ledger I built for the NovaWallet module of FirstBank NovaPay.
>
> That quoted line is the brief's own framing, and it drove every design decision I'll show you.
>
> I'll cover: the problem and hard constraints, architecture, the three hard technical problems
> — money integrity, concurrency, and idempotency — then tests, AI usage, the Nigerian
> operating context, and what I'd build next.

---
---

## SLIDE 2 — The Problem & Hard Constraints

**Reviewer takeaway:** *The candidate understood which requirements were trivial to implement and which two were genuinely hard — and why they interact.*

---

### ON SCREEN

**THE PROBLEM**
*Eight functional requirements, two of which are genuinely hard to get right at the same time*

| WHAT WE ARE BUILDING | HARD CONSTRAINTS (non-negotiable) |
|---|---|
| Create & manage NGN wallets | ⚠ **All amounts stored as `long` in kobo — no `float`/`double`** |
| Credit wallet — inbound NIP simulation | ⚠ **Balance must NEVER go negative under concurrency** |
| Atomic P2P transfer with concurrency safety | JWT bearer check on all business endpoints |
| Idempotency — safe retries, no double-spend | RFC 7807 Problem Details on every error |
| Paginated statement & audit trail | `docker compose up` — single command startup |
| Daily outbound limit (₦500k, WAT midnight reset) | Rate limiting · Outbox · Correlation IDs *(stretch)* |
| JWT bearer auth on every endpoint | Health / readiness endpoints *(stretch)* |
| Consistent RFC 7807 error responses | |

**Why these two interact:** get the kobo rule right but ignore concurrency → you can still double-spend.
Get concurrency right but use a float anywhere → you silently miscount. Both must hold simultaneously.

**All eight functional requirements and all four stretch goals are implemented.**

---

> The brief gives eight functional requirements.
> I want to focus on the two marked ⚠ because they are the genuinely hard combination.
>
> Every other requirement — auth, errors, Docker startup, rate limiting — is implementation work.
> These two require careful design under concurrent load, not just code.
>
> I'll confirm all requirements are met on slide 7 and move quickly through the hard parts first.

---
---

## SLIDE 3 — Architecture

**Reviewer takeaway:** *There is exactly one place in the codebase where money moves. That's a deliberate design decision, not an accident — it's what makes the invariants auditable and testable.*

---

### ON SCREEN

**ARCHITECTURE**
*One rule drove this design: put all money logic in one place so there is one place to get right*

```
┌─────────────────┐    ┌────────────────────────────────────┐    ┌──────────────────────┐
│     CLIENTS     │    │         API LAYER (.NET 9)          │    │    APPLICATION       │
│                 │    │                                      │    │                      │
│  Web Console    │    │  ① JWT Bearer Middleware             │    │  ┌────────────────┐  │
│  (SPA)          │───▶│    validates every request           │    │  │  LedgerService │  │
│                 │    │                                      │───▶│  │                │  │
│  Swagger UI     │───▶│  ② Wallets Controller               │    │  │ ← THE ONLY     │  │
│                 │    │     Transfers Controller             │    │  │   PLACE MONEY  │  │
│  Postman /      │───▶│     (bind + validate → delegate)     │    │  │   MOVES        │  │
│  Panel          │    │                                      │    │  └────────────────┘  │
└─────────────────┘    │  ③ RFC 7807 Exception Handler        │    │                      │
                       │    one error shape for every fault   │    │  Outbox Dispatcher   │
                       │                                      │    │  (background svc)    │
                       │  ④ Rate Limiter · Correlation-ID MW  │    └──────────────────────┘
                       └────────────────────────────────────┘                 │
                                                                               ▼
               ┌──────────────────────────────────────────────────────────────────────┐
               │                        PostgreSQL 16                                  │
               │   wallets │ transactions │ audit_logs │ idempotency_records           │
               │   outbox_messages                                                     │
               │   ✓  DB CHECK (BalanceKobo >= 0) — backstop independent of app logic │
               └──────────────────────────────────────────────────────────────────────┘
         ─── Cross-cutting: Structured logging · Correlation IDs · /health/live · /health/ready ───
```

**Key decisions numbered on diagram:**
① JWT middleware runs before any controller — auth cannot be bypassed
② Controllers are thin — they never touch the database directly, only call LedgerService
③ All errors go through one ProblemFactory — clients always see one consistent shape
④ Rate limiting is per authenticated user (JWT `sub`), not per IP — fair and correct

---

> Three columns: Clients → API → Application → Postgres underneath.
>
> The numbered callouts are the design decisions worth explaining:
>
> ① Auth first — the JWT middleware validates before any controller runs, so there's no path
> to the ledger without a valid token.
>
> ② Controllers are deliberately thin. They bind input and validate it, then delegate to
> LedgerService. Every balance mutation happens in LedgerService. One place. Transactional.
> This is what makes the money invariants auditable — there's no hidden path that bypasses it.
>
> ③ One error shape — the ProblemFactory stamps correlationId, traceId, and timestamp on every
> error regardless of source. A client that gets an error can always log and trace it.
>
> ④ Rate limiting per user, not per IP — if it were per IP, users behind a NAT would all share
> one bucket. Partitioned by ClaimTypes.NameIdentifier from the JWT.

---
---

## SLIDE 4 — Money Integrity

**Reviewer takeaway:** *Every monetary value is a `long` integer in kobo everywhere in the codebase — no exceptions. The database enforces this even if the application doesn't.*

---

### ON SCREEN

**MONEY INTEGRITY**
*The foundation — if this is wrong, nothing else matters*

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│   Every kobo is a long integer.  No float.  No decimal.  No rounding drift — ever.  │
│   1 NGN = 100 kobo exactly.  All arithmetic is integer arithmetic.                   │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

| INTEGER ARITHMETIC | DB-LEVEL DEFENCE | FUTURE-PROOFING |
|---|---|---|
| `BalanceKobo` is a `long` in the entity, DTO, and DB column | `CHECK (BalanceKobo >= 0)` rejects negative writes even if application logic has a bug | **No feature divides kobo yet** — so no rounding drift exists today |
| All amounts travel as `long` kobo across every layer | `xmin` PostgreSQL system column is the EF concurrency token | Before adding fee splits / interest / FX: document which party absorbs the remainder kobo |
| `checked(toBefore + req.AmountKobo)` — overflow throws, not wraps | Migration applies and verifies the constraint at startup | This is a deliberate gap — not a bug |

```csharp
// The entire money path in two lines — no float, no decimal, no division:
from.BalanceKobo = fromBefore - req.AmountKobo;
to.BalanceKobo   = checked(toBefore + req.AmountKobo);
```

---

> The foundation is simple: `long` integer in kobo, everywhere.
>
> `checked()` in C# is the important detail — it means an arithmetic overflow throws an exception
> instead of silently wrapping to a wrong number. In a financial ledger, the wrong number is
> a customer's money, so throwing is the right choice.
>
> The database CHECK constraint is defence in depth — it doesn't trust the application.
> Even a future code change that bypasses the application-level balance check will hit the DB
> constraint and be rejected.
>
> The third column — "Future-proofing" — is about something that doesn't exist yet but will.
> The moment we add fee splits, round-up savings, or FX conversion, we introduce division on
> kobo, which produces remainders. We need a policy for who absorbs the leftover kobo before
> that code merges. I documented this assumption in the README. Right now the service is clean.

---
---

## SLIDE 5 — Concurrency Safety

**Reviewer takeaway:** *The balance-never-negative requirement was proven under actual concurrent load — 100 simultaneous requests, exact assertions, real PostgreSQL. Not just claimed.*

---

### ON SCREEN

**CONCURRENCY SAFETY**
*Proven under load, not just asserted*

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│  LOAD TEST RESULT:  100 concurrent transfers against a wallet funded for exactly 50 │
│  → exactly 50 succeed  ·  exactly 50 rejected  ·  balance = ₦0.00  ·  0 double-spends │
└────────────────────────────────────────────────────────────────────────────────────┘
```

**Why naive implementations fail:**
```
Thread A reads balance = ₦10,000                Thread B reads balance = ₦10,000
Thread A checks: 10,000 ≥ 6,000 → proceed       Thread B checks: 10,000 ≥ 6,000 → proceed
Thread A debits ₦6,000                           Thread B debits ₦6,000
                         ↓
                  Final balance = -₦2,000  ❌  (double-spend)
```

**Our solution — lock ordering prevents this entirely:**

| Step | What happens | Why |
|---|---|---|
| 1 | `SELECT … FOR UPDATE` on both wallet rows | Takes an exclusive row lock — no other transaction can read or write these rows until we commit |
| 2 | Lock wallets in **ascending Guid order** (always) | Both concurrent transfers take locks in the same sequence — prevents deadlock |
| 3 | Check balance **inside** the lock | The balance we check is the true current balance, not a stale read |
| 4 | Debit + Credit + Audit in **one transaction** | Either all three happen or none do |
| 5 | `DB CHECK (BalanceKobo >= 0)` as final backstop | PostgreSQL rejects a negative write even if application logic fails |

---

> This is the headline requirement.
>
> The naive approach has a classic race condition: two requests both read the same balance,
> both decide there are sufficient funds, and both debit. The balance goes negative.
>
> Our approach serialises concurrent transfers on the same wallet via a row-level write lock.
> The ordering rule — always lock in ascending wallet-ID order — is critical to prevent deadlocks
> when two transfers happen between the same pair of wallets in opposite directions simultaneously.
>
> The proof is an automated test: 100 goroutine-equivalent concurrent transfers, exactly 50
> funded. The test asserts: exactly 50 succeed, exactly 50 return 422 insufficient funds,
> source balance is exactly zero, destination holds exactly the right amount.
> That test passes on every run against a real PostgreSQL instance, not a mock.

---
---

## SLIDE 6 — Idempotency

**Reviewer takeaway:** *A client that retries a timed-out transfer cannot double-spend — because the idempotency record is written in the same database transaction as the money movement. They are atomic.*

---

### ON SCREEN

**IDEMPOTENCY**
*"Did my transfer go through?" — safe to retry without fear of double-spending*

**The problem idempotency solves:**
```
Client sends transfer → network timeout → client retries → transfer runs twice → ₦ moved twice ❌
```

**How the Idempotency-Key header works:**

```
Client:  POST /api/transfers
         Idempotency-Key: a1b2c3d4
                │
                ▼
         ┌─────────────────────────────┐
         │  Key already in DB?         │──── YES (same body) ──▶  Return stored 201
         │                             │                           Idempotent-Replayed: true
         │                             │──── YES (diff body) ──▶  409 Conflict
         └─────────────────────────────┘                           idempotency_key_reused
                │ NO
                ▼
         ┌──────────────────────────────────────────────────────┐
         │  BEGIN TRANSACTION                                    │
         │    Debit source wallet                                │
         │    Credit destination wallet                         │
         │    Write audit entries                               │
         │    Write idempotency record  ← same transaction      │
         │  COMMIT                                              │
         └──────────────────────────────────────────────────────┘
```

**Why "same transaction" is the critical detail:**

If the record were written *after* the transfer committed: crash between commit and record-write → retry re-runs the transfer → double-spend. ❌

Written in the same transaction: both commit or both roll back → no window → retry always safe. ✅

| Outcome | Response |
|---|---|
| Fresh transfer | 201 Created · `Idempotent-Replayed: false` |
| Replay (same key + same body) | 201 Created · `Idempotent-Replayed: true` · money moved once |
| Key reuse (same key + different body) | 409 Conflict · `idempotency_key_reused` |

---

> Idempotency is critical for financial APIs because networks are unreliable.
> A client that times out on a transfer doesn't know if the money moved.
> Without idempotency, the safe thing is to retry — and a retry would move it twice.
>
> The standard approach — store the idempotency record after the transfer commits — has a gap.
> If there's a crash between the commit and the record write, the next retry runs the transfer
> again. Two commits, two debits.
>
> Our approach closes that gap by writing the record inside the same transaction.
> They are atomic. There is no window.
>
> Concurrent replays are safe too: the unique index on the key means only one writer can commit.
> The loser rolls back its entire transfer, reads the winner's stored response, and returns it.
> The money moved exactly once no matter how many retries fire simultaneously.

---
---

## SLIDE 7 — All Requirements Met

**Reviewer takeaway:** *Every item from the brief is implemented, including all four optional stretch goals. Each one has a non-trivial design decision worth noting.*

---

### ON SCREEN

**ALL REQUIREMENTS MET**
*Every requirement from the brief, plus all four stretch goals*

| CAPABILITY | WHAT WAS BUILT | DESIGN NOTE |
|---|---|---|
| **Daily Limit** | ₦500,000 outbound / wallet / WAT midnight reset | Computed from actual `TransferOut` rows **under the same row lock** — always consistent with concurrent transfers. No separate counter. No reset cron job. |
| **Statement** | `GET /api/wallets/{id}/statement` paginated, newest-first | Returns `page`, `pageSize`, `totalCount` — caller controls paging |
| **Audit Log** | Separate `audit_logs` table, append-only | SHA-256 **hash-chained** — each row hashes the previous row's hash. Tamper or delete any row and the chain breaks. Detectable. |
| **JWT Auth** | JwtBearer validates issuer · audience · lifetime · HMAC signature | Mock issuer at `/api/auth/token`. Signing key is environment-variable configurable. |
| **Error Handling** *(stretch)* | Every error → `application/problem+json` | Consistent `type` + machine-readable `title` + `correlationId` + `traceId` + `timestamp` on every response. Field-level error map on validation failures. |
| **Rate Limiting** *(stretch)* | Token-bucket on `POST /api/transfers` | Partitioned per authenticated user by `ClaimTypes.NameIdentifier` (JWT `sub`) — not per IP |
| **Outbox Pattern** *(stretch)* | `TransferCompleted` event written in **same transaction** | `OutboxDispatcher` background service drains asynchronously — at-least-once, no dual-write risk |
| **Health Checks** *(stretch)* | `/health/live` and `/health/ready` with DB check | Suitable for Kubernetes liveness/readiness probes |

---

> Quickly confirming every requirement.
>
> Three design notes worth highlighting:
>
> The daily limit is computed from actual rows under the same row lock that protects the balance.
> That means the limit check and the balance check are always consistent — there's no race where
> a concurrent transfer sneaks past the cap.
>
> The audit log is hash-chained. Tamper or delete any row and the chain breaks.
> Recomputing the hashes will reveal the gap. That's a regulatory-grade immutability pattern.
>
> All four stretch goals are implemented. They're not bolted on — the outbox shares the same
> transaction as the transfer, rate limiting is per-user not per-IP, and the error handler
> was specifically redesigned to produce one consistent shape across all failure modes.

---
---

## SLIDE 8 — Test Rigour + Demo

**Reviewer takeaway:** *15 tests all pass against real PostgreSQL. The concurrency test isn't just a happy-path check — it fires 100 simultaneous requests and asserts exact numerical invariants.*

---

### ON SCREEN

**TEST RIGOUR + DEMO**
*Tests against real PostgreSQL via Testcontainers — an in-memory provider cannot test row locks*

```
✓  15 / 15 passed  ·  Real PostgreSQL  ·  Testcontainers  ·  WebApplicationFactory
```

| Suite | Test name | What it proves | Time |
|---|---|---|---|
| **ConcurrencyTests** | `Concurrent_transfers_never_overspend_and_never_go_negative` | 100 concurrent requests, exactly 50 succeed, balance = 0, 0 double-spends | 8 s |
| **ConcurrencyTests** | `Concurrent_replays_of_same_idempotency_key_process_once` | 25 concurrent replays of one key, money moves exactly once | 10 s |
| LedgerIntegrationTests | `Unauthenticated_request_is_rejected` | No token = 401, always | 24 ms |
| LedgerIntegrationTests | `Create_wallet_starts_at_zero_in_ngn` | New wallet balance = 0, currency = NGN | 148 ms |
| LedgerIntegrationTests | `Credit_then_transfer_moves_funds_exactly` | Money conserved: debit + credit = original amount | 381 ms |
| LedgerIntegrationTests | `Transfer_exceeding_balance_returns_422_and_does_not_move_money` | Rejected transfer leaves both balances unchanged | 432 ms |
| LedgerIntegrationTests | `Idempotency_replay_does_not_double_process` | Same key replayed = only one debit | 170 ms |
| LedgerIntegrationTests | `Reusing_idempotency_key_with_different_body_is_rejected` | Different body = 409 Conflict | 274 ms |
| LedgerIntegrationTests | `Statement_is_paginated_newest_first` | 3 credits → first page shows newest first | 7 s |
| LedgerIntegrationTests | `Daily_limit_blocks_transfer_over_cap` | Transfer over ₦500k limit → 422 | 284 ms |
| ErrorHandlingTests | `Model_validation_error_uses_problem_shape_with_field_errors` | Missing field → `application/problem+json` with field map | 58 ms |
| ErrorHandlingTests | `Malformed_json_body_returns_consistent_400` | Broken JSON → same `problem+json` shape | 2 s |
| ErrorHandlingTests | `Domain_error_returns_problem_json_with_code` | `insufficient_funds` → correct status + code | 3 s |
| ErrorHandlingTests | `Not_found_wallet_returns_problem_json` | Unknown wallet → `wallet_not_found` code | 184 ms |
| ErrorHandlingTests | `Same_wallet_transfer_is_rejected_with_specific_code` | Same source + destination → `same_wallet_transfer` code | 78 ms |

**Why real PostgreSQL is non-negotiable:** `SELECT … FOR UPDATE` and the `xmin` concurrency token are PostgreSQL features. An in-memory EF provider does not implement them. Testing concurrency on an in-memory provider would test nothing about the actual safety guarantee.

---

> 15 tests, all passing, all against real Postgres.
>
> The decision to use Testcontainers rather than an in-memory provider is deliberate.
> The concurrency guarantee depends on PostgreSQL row-level locks and the xmin system column.
> An in-memory provider doesn't support either. A test on in-memory Postgres would give a
> green result on broken concurrency code — exactly the false confidence we need to avoid in
> a financial system.
>
> The two concurrency tests are the headline. The first fires 100 concurrent transfers and
> asserts exact numbers: not "most succeed" but "exactly 50 succeed, exactly 50 fail,
> source balance is exactly zero." That's a meaningful test. It fails if the locking is wrong.

---
---

## SLIDE 9 — Thank You / Q&A
*(This slide appears at the end of the original 9-slide sample deck.
Slides 10–12 are additional slides added after it.)*

---

### ON SCREEN

```
THANKS!

Do you have any questions?

babatundeoladejo16@gmail.com
+2348102992169
https://babz-portfolio.vercel.app/
```

---

> Thank you. I'm happy to take any questions on the design decisions I've described.
> The deeper technical interview and live demo are scheduled for 22 September.

---
---

## SLIDE 10 — AI Usage & Judgment

**Reviewer takeaway:** *The candidate used AI as an accelerator but caught three bugs that would have been real financial system defects — and explains exactly how each was caught. This demonstrates judgment, not just tool use.*

---

### ON SCREEN

**AI USAGE & JUDGMENT**
*AI was used throughout — and three bugs it introduced would have shipped real defects in production*

---

**BUG 1 — Runtime crash on validation** *(severity: high — every request fails in production)*

| | |
|---|---|
| **What AI did** | Applied `[property: Required]` to positional record constructor params |
| **What happened** | Compiled and passed static analysis. Crashed at runtime with `InvalidOperationException` on every single request — ASP.NET Core silently ignores validation attributes on record *properties*; they must target the constructor *parameter* |
| **How caught** | Running the service and hitting the endpoints against the live container |
| **Fix** | Removed `property:` target so the attribute binds to the constructor parameter |

---

**BUG 2 — Concurrency lock silently broken** *(severity: critical — financial safety defect)*

| | |
|---|---|
| **What AI did** | Generated `FromSqlRaw("SELECT * FROM wallets … FOR UPDATE")` |
| **What happened** | `SELECT *` does **not** return PostgreSQL system columns. `xmin` — the EF concurrency token — was missing from the result set. EF threw `column n.xmin does not exist` on every transfer. But more dangerously: if this error were suppressed, the `xmin` optimistic-lock backstop would be **silently gone** — any code path that bypasses the explicit lock would have no safety net |
| **How caught** | Exercising credit and transfer against the live PostgreSQL container |
| **Fix** | Explicit column list in the raw SQL including `xmin` |

---

**BUG 3 — Rate limiter bucketed all users into one** *(severity: medium — caught by load test)*

| | |
|---|---|
| **What AI did** | Partitioned rate limiter by `httpContext.User.Identity?.Name` |
| **What happened** | `Identity.Name` is null for JWT tokens that only carry `sub`. JwtBearer maps `sub` → `ClaimTypes.NameIdentifier`, not `Identity.Name`. Every caller — regardless of who they were — shared the same IP-based bucket |
| **How caught** | Concurrency load test: expected 50 successes, got ~15, with `429 TooManyRequests` |
| **Fix** | Partition on `ClaimTypes.NameIdentifier ?? "sub" ?? Name ?? IP` |

---

**The pattern across all three:** AI produced code that compiled, passed static analysis, and looked correct. The bugs only appeared when running against a real system (live container + real database) and when tests asserted *exact* outcomes rather than just "didn't crash."

---

> The brief specifically asks us to demonstrate judgment in directing AI and catching where it
> is wrong.
>
> All three bugs would have shipped production defects.
>
> Bug 2 is the most instructive: the row lock worked. The balance would not have gone negative.
> But the xmin backstop — the guard for any code path that bypasses the explicit lock — was
> silently gone. No compile error. No test failure on a mock. Invisible until running real Postgres.
>
> The lesson: in a financial system, "it compiled" and "happy path tests pass" are not enough.
> You need to run against a real database, and your tests need to assert exact numerical invariants.

---
---

## SLIDE 11 — Operating Context

**Reviewer takeaway:** *The candidate didn't just implement the requirements — they identified exactly where the Nigerian fintech operating context constrains or gaps the design, and what it would take to close each gap.*

---

### ON SCREEN

**OPERATING CONTEXT**
*The brief says "strong candidates will notice where these constraints matter" — here is where they attach to this ledger specifically*

| Constraint | Where it attaches to this codebase | Status |
|---|---|---|
| **₦ in kobo · no float** | Enforced everywhere. Critical forward note: adding any feature that divides kobo (fee splits, FX, interest) produces remainders. Rounding policy must be documented before merging. | ✅ Implemented |
| **Tiered KYC — BVN/NIN** | `DailyOutboundLimitKobo` is a global constant today. CBN framework makes it a function of KYC tier: Tier 1 (phone-only) has lower caps than Tier 3 (full KYC). One field (`KycTier`) on `Wallet` + a resolver function in `TransferAsync` closes this gap. | ⚠ Gap identified — next build |
| **NIBSS NIP rails** | Inbound credit today is a synchronous internal call. Real NIP credits are external notifications NIBSS can resend — idempotency must key on NIP session ID, not a client header. Outbound transfers to other banks are `pending → settled/failed`, not instant-final. The outbox pattern already built is the right foundation. | ⚠ Outbox ready · async states deferred |
| **USSD \*894#** | No JWT for feature-phone users. USSD gateway authenticates by MSISDN + PIN and calls the API on the user's behalf. Naira → kobo conversion is already server-side (UI conversion is convenience only) — this is already correct. New principal type needed. | ✅ Kobo conversion correct · auth adapter TBD |
| **NDPA 2023** | Audit log stores `correlationId` + `customerId` — not raw BVN/NIN. Append-only immutability conflicts with NDPA data-subject erasure rights. Resolution: identifiers by reference only (already done). Retention window policy needed. | ⚠ Schema correct · retention TBD |
| **CBN consumer protection** | Failed transfer reasons are surfaced: `insufficient_funds`, `daily_limit_exceeded`, `same_wallet_transfer` — customers know why. Honest gap: no reversal/refund path. A failed outbound NIP leg must return funds — a CBN consumer-protection requirement. | ⚠ Error transparency ✅ · Reversal TBD |

---

> Each row here maps a specific operating-context constraint to a specific code seam.
>
> The KYC gap is the most important to call out: the daily limit is a global constant.
> Under CBN's tiered KYC framework, it should resolve from the wallet's tier.
> It's a small change — one field, one resolver function — but it's a real regulatory gap.
>
> The NIP point is architectural. The outbox I've built is exactly the right foundation for
> settlement callbacks, but the pending/settled transaction state machine doesn't exist yet.
>
> The NDPA tension is subtle: immutable audit logs conflict with erasure rights.
> The schema already resolves this correctly by storing only a customer identifier,
> not raw PII like BVN. The remaining gap is a retention window policy.

---
---

## SLIDE 12 — What I'd Build Next

**Reviewer takeaway:** *The candidate made deliberate trade-offs and can defend them. The backlog is prioritised by regulatory and user-safety impact, not by what was easiest to add.*

---

### ON SCREEN

**WHAT I'D BUILD NEXT**
*Deliberate scope decisions — prioritised by regulatory and user-safety impact*

**PRIORITY BACKLOG**

| # | What | Why it matters |
|---|---|---|
| 1 | `KycTier` on `Wallet` → per-tier daily limit | CBN framework compliance — biggest regulatory gap in current implementation |
| 2 | Reversal / refund endpoint | Consumer-protection requirement — a failed outbound NIP leg must return funds |
| 3 | `Pending` / `Settled` transaction states | Outbound NIP is async; treating it as instant-final is wrong in production |
| 4 | USSD adapter — MSISDN + PIN principal type | Feature-phone / low-connectivity access on \*894# |
| 5 | NDPA retention policy on `audit_logs` | Data-subject erasure rights — retention window and pseudonymisation |

**KEY TRADE-OFFS — deliberately chosen, not limitations**

| Decision made | Alternative considered | Why this choice |
|---|---|---|
| **Pessimistic locks (`FOR UPDATE`)** | Optimistic retry on `xmin` conflict | Under burst load, pessimistic locks queue requests cleanly. Optimistic under contention causes retry storms that hammer the DB. For a money ledger: queue, don't storm. |
| **Daily limit from transaction history** | Dedicated counter column, reset by cron job | History-based is always consistent with the ledger — no race between counter read and balance write. No scheduler, no crash-leaves-counter-wrong scenario. |
| **Mock JWT issuer** | Building a full JWKS/refresh flow | The brief scores middleware and claims handling, not IdP architecture. Signing key is env-configurable. Full IdP is out of scope and would distract from ledger correctness. |

---

> Three trade-offs worth defending:
>
> Pessimistic locking — under high transfer volume, optimistic concurrency generates retry noise.
> Pessimistic queues requests cleanly and the throughput difference is negligible for a wallet
> ledger where correctness matters more than raw throughput.
>
> Limit from history — one extra SUM query per transfer under the same lock. Worth it because
> there is no separate system to keep in sync, no cron job to fail, and no counter to corrupt
> if the application crashes at the wrong moment.
>
> Mock issuer — the brief is scoring JWT middleware and claims handling, which this fully
> implements. Building a JWKS endpoint with token rotation is correct production work but
> was explicitly out of scope for a 72-hour brief.
>
> Thank you. I'm ready to demo the live service, answer any questions, or modify code live
> if you'd like to see a specific change.

---
---

## Timing Summary

| # | Slide | Duration |
|---|---|---|
| 1 | Title | 0:30 |
| 2 | Problem & Hard Constraints | 1:00 |
| 3 | Architecture | 1:30 |
| 4 | Money Integrity | 1:00 |
| 5 | Concurrency Safety | 1:30 |
| 6 | Idempotency | 1:00 |
| 7 | All Requirements Met | 0:30 |
| 8 | Test Rigour + Demo | 0:30 |
| 9 | Thank You / Q&A | 0:15 |
| 10 | AI Usage & Judgment | 1:00 |
| 11 | Operating Context | 1:00 |
| 12 | What I'd Build Next | 0:30 |
| | **Total** | **~10:15** |

---

## Scored Criteria → Slides

| Criterion | Slides |
|---|---|
| Correctness & concurrency safety | 4, 5, 6 |
| Code quality & architecture | 3 |
| Test rigour | 8 |
| Security awareness | 2, 7 |
| AI fluency & judgment | 10 |
| Communication | All — especially 11, 12 |

---

## If the panel asks you to modify code live

| Prompt | Where to look |
|---|---|
| "Change the daily limit to be per KYC tier" | `Domain/Wallet.cs` — add `KycTier` enum; `Application/LedgerOptions.cs` — per-tier limit map; `Application/LedgerService.cs` — resolve `wallet.KycTier` in `TransferAsync` before the limit check |
| "Add a reversal endpoint" | `Controllers/TransfersController.cs` + `Application/LedgerService.cs` — new `ReverseAsync` that re-locks same wallets in Guid order, inverts debit/credit, writes `Reversal` transaction type |
| "Show me where the row lock is" | `Application/LedgerService.cs` → `LockWalletAsync()` — `SELECT … FOR UPDATE` raw SQL and the id-ordering comment |
| "Show me where idempotency is atomic" | `Application/LedgerService.cs` → `TransferAsync()` — the `IdempotencyRecord .Add()` call is inside the `await using var tx` block, before `SaveChangesAsync()` |
| "Make the audit endpoint admin-only" | `Controllers/WalletsController.cs` — add `[Authorize(Roles = "admin")]` to the `Audit` action; add `roles` claim in `Auth/TokenService.cs` |
