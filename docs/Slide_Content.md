# NovaWallet Ledger Service — Slide Content
### FirstBank NovaPay · Backend Engineer Assessment · 12 Slides · 10 Minutes

> Each slide shows exactly what appears on screen (headline, bullets, visuals) followed by
> the speaker notes in a blockquote. Print or read this document alongside the deck.

---

## SLIDE 1 — Title
**Duration: ~30 seconds**

---

### ON SCREEN

```
🐘  FirstBank NovaPay

NovaWallet Ledger Service
Case Study · Backend Engineer Assessment · .NET 9 / C#

⚡ Concurrency-safe transfers
🔒 JWT auth · RFC 7807 errors
🐳 docker compose up → ready

BABATUNDE OLADEJO                              September 2026
```

---

> Good morning / afternoon, thank you for your time.
>
> I'm going to walk you through the **NovaWallet Ledger Service** — a concurrency-safe,
> production-realistic wallet ledger I built for the NovaWallet module of FirstBank NovaPay.
>
> The one-sentence summary: **it's the component that must never lose, duplicate, or miscount
> a customer's money.**
>
> Everything I'll show you in the next ten minutes is a direct consequence of taking that seriously.
>
> I'll cover: the problem, the architecture, the three hard technical problems — money integrity,
> concurrency, and idempotency — then tests, AI usage, the Nigerian operating context,
> and what I'd build next.

---
---

## SLIDE 2 — The Problem & Hard Constraints
**Duration: ~1 minute**

---

### ON SCREEN

**THE PROBLEM**

| WHAT WE ARE BUILDING | HARD CONSTRAINTS (non-negotiable) |
|---|---|
| Create & manage NGN wallets | **All amounts stored as `long` in kobo — no `float`/`double`** |
| Credit wallet — inbound NIP simulation | **Balance must NEVER go negative under concurrency** |
| Atomic P2P transfer with concurrency safety | JWT bearer check on all business endpoints |
| Idempotency — safe retries, no double-spend | RFC 7807 Problem Details on every error |
| Paginated statement & audit trail | `docker compose up` — single command startup |
| Daily outbound limit (₦500k, WAT midnight reset) | Rate limiting · Outbox pattern · Correlation IDs |
| JWT bearer auth on every endpoint | Health/readiness endpoints |
| Consistent RFC 7807 error responses | |

---

> The brief gives eight functional requirements. I want to focus on the two that are
> genuinely hard to get right **at the same time**.
>
> **First: all money must be stored as integers in kobo — no float, no decimal, no rounding drift.**
>
> **Second: the balance must never go negative under any interleaving of concurrent requests.**
>
> These two interact. Get the kobo rule right but ignore concurrency — you can still double-spend.
> Get concurrency right but introduce floating-point anywhere — you silently miscount.
> I'll show how I enforced both.
>
> The other constraints — JWT auth, RFC 7807 errors, single-command startup, all four stretch goals
> — are all implemented. I'll confirm them on slide 7.

---
---

## SLIDE 3 — Architecture
**Duration: ~1 minute 30 seconds**

---

### ON SCREEN

**ARCHITECTURE**
*Clean layering — controllers stay thin, all money logic in one place*

```
┌─────────────────┐    ┌──────────────────────────────────┐    ┌─────────────────────┐
│     CLIENTS     │    │           API LAYER (.NET 9)      │    │     APPLICATION     │
│                 │    │                                    │    │                     │
│  Web Console    │───▶│  JWT Bearer Middleware             │───▶│  ┌───────────────┐ │
│  (SPA)          │    │                                    │    │  │ LedgerService │ │
│                 │    │  ┌──────────────┐ ┌─────────────┐ │    │  │ (money logic) │ │
│  Swagger UI     │───▶│  │  Wallets     │ │  Transfers  │ │    │  └───────────────┘ │
│                 │    │  │  Controller  │ │  Controller │ │    │                     │
│  Postman /      │───▶│  └──────────────┘ └─────────────┘ │    │  Outbox Dispatcher │
│  Panel          │    │                                    │    │  TokenService      │
└─────────────────┘    │  RFC 7807 Exception Handler        │    │  IClock · Hashing  │
                       │  Rate Limiter · Correlation-ID MW  │    └─────────────────────┘
                       └──────────────────────────────────┘                │
                                                                            ▼
                    ┌──────────────────────────────────────────────────────────────┐
                    │                    PostgreSQL 16                              │
                    │  wallets │ transactions │ audit_logs │ idempotency_records   │
                    │  outbox_messages                                              │
                    │  ✓  CHECK (BalanceKobo >= 0)  — defence in depth             │
                    └──────────────────────────────────────────────────────────────┘
              ── Cross-cutting: Logging · Correlation IDs · /health/live · /health/ready ──
```

---

> Three columns: Clients on the left, the API layer in the middle, Application on the right.
> PostgreSQL underneath.
>
> **The most important design decision is LedgerService — the single choke point for all
> balance mutations.** Controllers bind and validate input, then delegate entirely.
> There is exactly one place where balances change, and it is transactional.
> This makes the money invariants auditable and testable in isolation.
>
> A few things worth calling out:
>
> - The **RFC 7807 Exception Handler** means every error — model validation, domain rule,
>   infrastructure fault — goes through a single `ProblemFactory` that stamps `correlationId`,
>   `traceId`, and `timestamp` consistently. Clients always see one error shape.
>
> - The **Outbox Dispatcher** writes `TransferCompleted` events in the same transaction as the
>   balance mutation, then drains them asynchronously. At-least-once delivery, no dual-write risk.
>
> - The **CHECK constraint** on `BalanceKobo >= 0` is a database-level backstop independent of
>   application logic.

---
---

## SLIDE 4 — Money Integrity
**Duration: ~1 minute**

---

### ON SCREEN

**MONEY INTEGRITY**
*The foundation everything else builds on*

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│  Every kobo is a  long  integer.  No float.  No decimal.  No rounding drift — ever. │
└─────────────────────────────────────────────────────────────────────────────────┘
```

| INTEGER ARITHMETIC | DB-LEVEL DEFENCE | NO DIVISION WITHOUT POLICY |
|---|---|---|
| `BalanceKobo` stored as `long` | `CHECK (BalanceKobo >= 0)` | No fees / splits yet → no rounding |
| All amounts passed as `long` kobo | Backstop independent of app logic | Future: document who absorbs |
| `checked()` catches overflow | `xmin` row as concurrency token | remainder kobo before merging |
| 1 NGN = 100 kobo exactly | Migration-verified at startup | any feature that divides |

```csharp
from.BalanceKobo = fromBefore - req.AmountKobo;
to.BalanceKobo   = checked(toBefore + req.AmountKobo);
```

---

> The foundation is simple: **every monetary value is a `long` integer in kobo, everywhere.**
>
> Entity fields, DTOs, EF column mappings, arithmetic — all `long`.
> 1 NGN = 100 kobo exactly. No conversion from float anywhere in the path.
>
> `checked()` arithmetic in C# means overflow throws rather than silently wrapping to a wrong
> number — the right behaviour when the wrong number is a customer's money.
>
> The `CHECK (BalanceKobo >= 0)` database constraint is defence in depth. Even if a code bug
> escapes every application check, PostgreSQL will refuse to write a negative balance.
> It doesn't rely on application logic being correct.
>
> **Critical note for future development:** the moment any feature divides kobo — fee splits,
> interest, FX on the remittance corridor — you get remainders. You need a documented policy for
> who absorbs the leftover kobo before that code merges.
> Right now there's no division in this service, so we're clean.

---
---

## SLIDE 5 — Concurrency Safety
**Duration: ~1 minute 30 seconds**

---

### ON SCREEN

**CONCURRENCY SAFETY**
*The hardest requirement — proven under load, not just in the happy path*

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│  100 concurrent transfers against a wallet funded for exactly 50                      │
│  → 50 succeed · 50 rejected · balance drains to ₦0.00 · 0 double-spends             │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

| HOW IT WORKS | WHY THE NAIVE APPROACH FAILS |
|---|---|
| **`SELECT … FOR UPDATE` on both wallet rows** | Read-check-write without a lock: |
| Rows locked in wallet-id order (ascending Guid) | Two threads both read balance = 1000 |
| → prevents deadlocks on opposing-direction transfers | Both decide 1000 ≥ 600, both proceed |
| `ReadCommitted` transaction holds both locks | Both debit → balance ends at **-200 ❌** |
| Balance check + debit/credit + audit inside one tx | |
| **`DB CHECK (BalanceKobo >= 0)` as final backstop** | Optimistic-only under high contention: |
| `xmin` optimistic token catches any unlocked path | Retry storm hits the DB ❌ |
| | **Our approach: lock first, check inside lock ✓** |

---

> This is the headline requirement and the one I spent the most time on.
>
> **The naive approach fails like this:** two concurrent requests both read `balance = 1000`.
> Both decide 1000 ≥ 600, both proceed. Both debit. Balance ends at -200. That's a double-spend.
>
> The fix sounds simple — lock before checking — but the implementation detail that matters
> is **ordering**. If Transfer A locks wallet 1 then wallet 2, and Transfer B locks wallet 2
> then wallet 1, they deadlock.
>
> Our approach: **always lock in ascending wallet-ID order.** Both transfers take the locks in the
> same sequence, so the second queues behind the first rather than deadlocking. Once the first
> commits, the second sees the updated balance and correctly rejects if funds are exhausted.
>
> **The proof:** 100 concurrent transfers against a wallet funded for exactly 50.
> Exactly 50 succeed, exactly 50 rejected with insufficient funds, source drains to ₦0.00,
> destination holds exactly the right amount. That test passes on every run against real Postgres.

---
---

## SLIDE 6 — Idempotency
**Duration: ~1 minute**

---

### ON SCREEN

**IDEMPOTENCY**
*Safe retries — the transfer either happened once or didn't happen at all*

```
┌──────────────────┐     ┌─────────────────┐   YES  ┌──────────────────────┐
│ Client sends     │────▶│  Key found in   │───────▶│ Return stored 201    │
│ transfer +       │     │  DB? (fast path) │        │ Idempotent-Replayed: │
│ Idempotency-Key  │     └─────────────────┘        │ true                 │
└──────────────────┘              │ NO               └──────────────────────┘
                                  ▼
                         ┌─────────────────┐   YES  ┌──────────────────────┐
                         │  Same payload   │───────▶│ 409 Conflict         │
                         │  hash?          │        │ idempotency_key_     │
                         └─────────────────┘        │ reused               │
                                  │ NO               └──────────────────────┘
                                  ▼
                         ┌─────────────────────────────────────────────────┐
                         │  Execute transfer + write idempotency record    │
                         │  in ONE database transaction                    │
                         └─────────────────────────────────────────────────┘
```

**The critical design decision**
> The idempotency record is written in the **SAME** database transaction as the balance mutation.
> There is no window where the transfer commits but the record does not — so a retry can never
> double-process money. Concurrent replays race on a unique index: the loser rolls back entirely.

| Replay same key + same body | Reuse key + different body |
|---|---|
| Returns original 201 response | 409 Conflict |
| `Idempotent-Replayed: true` header | `idempotency_key_reused` error code |
| Money moved exactly once | Original transfer unchanged |

---

> Idempotency is critical for financial APIs because networks are unreliable.
> A client that times out on a transfer request doesn't know if the money moved.
> Without idempotency, a retry would move it twice.
>
> The standard approach — store the record *after* the transfer commits — has a fatal race:
> the transfer commits, a crash happens before the record is stored, the next retry re-runs it.
>
> **Our approach: the idempotency record is inserted in the same transaction as the balance
> mutation.** They commit together or roll back together. There is no window.
>
> Concurrent replays race on a unique index. First writer commits both; the loser hits a
> unique-constraint violation, rolls back the transfer entirely, returns the winner's response.
> Money moved exactly once regardless of how many concurrent retries fired.

---
---

## SLIDE 7 — All Requirements Met
**Duration: ~30 seconds**

---

### ON SCREEN

**ALL REQUIREMENTS MET**

| CAPABILITY | REQUIREMENT |
|---|---|
| **Daily Limit** | ₦500,000 outbound / wallet / WAT day. Computed from `TransferOut` rows under the same row lock — always consistent with concurrent transfers. No separate counter. No reset cron. |
| **Statement** | `GET /api/wallets/{id}/statement` · paginated, newest-first · `page` / `pageSize` · `totalCount` for client-side paging |
| **Audit Log** | Separate `audit_logs` table, append-only. SHA-256 **hash-chained** — tamper-evidence. Stores `balanceBefore`, `balanceAfter`, `correlationId` on every row. |
| **JWT Auth** | JwtBearer validates issuer · audience · lifetime · HMAC signature. Mock issuer at `/api/auth/token`. The middleware and claims handling is what's scored. |
| **Error Handling** | Every error → `application/problem+json` with `type`, machine-readable `title` (error code), `correlationId`, `traceId`, `timestamp`. Field-level map on validation errors. |
| **Rate Limiting** | Token-bucket per authenticated subject (`JWT sub` → `ClaimTypes.NameIdentifier`). `POST /api/transfers` only. |
| **Outbox Pattern** | `TransferCompleted` event written in the **same** transaction as the transfer. `OutboxDispatcher` drains asynchronously — at-least-once, no dual-write risk. |
| **Health Checks** | `/health/live` and `/health/ready` with DB connectivity check — suitable for Kubernetes probes. |

---

> Quickly confirming every requirement from the brief.
>
> The daily limit point worth calling out: it's computed from actual `TransferOut` rows under the
> same row lock that protects the balance. That means the limit check and the balance check are
> always consistent — a concurrent transfer can't sneak past the cap between a separate counter
> read and the balance write.
>
> The audit log is hash-chained — each row includes the SHA-256 of the previous row's hash.
> Tamper or delete any row, the chain breaks. That's a regulatory-grade immutability pattern.
>
> All four stretch goals are implemented: rate limiting, transactional outbox, correlation IDs,
> health/readiness endpoints.

---
---

## SLIDE 8 — Test Rigour + Demo
**Duration: ~30 seconds**

---

### ON SCREEN

**TEST RIGOUR + DEMO**

```
✓  15 / 15 tests passed · Real PostgreSQL via Testcontainers · concurrency proven under load
```

**Test results:**

| Suite | Test | Result |
|---|---|---|
| ConcurrencyTests | `Concurrent_transfers_never_overspend_and_never_go_negative` | ✓ 8 s |
| ConcurrencyTests | `Concurrent_replays_of_same_idempotency_key_process_once` | ✓ 10 s |
| LedgerIntegrationTests | `Unauthenticated_request_is_rejected` | ✓ 24 ms |
| LedgerIntegrationTests | `Create_wallet_starts_at_zero_in_ngn` | ✓ 148 ms |
| LedgerIntegrationTests | `Credit_then_transfer_moves_funds_exactly` | ✓ 381 ms |
| LedgerIntegrationTests | `Transfer_exceeding_balance_returns_422_and_does_not_move_money` | ✓ 432 ms |
| LedgerIntegrationTests | `Idempotency_replay_does_not_double_process` | ✓ 170 ms |
| LedgerIntegrationTests | `Reusing_idempotency_key_with_different_body_is_rejected` | ✓ 274 ms |
| LedgerIntegrationTests | `Statement_is_paginated_newest_first` | ✓ 7 s |
| LedgerIntegrationTests | `Daily_limit_blocks_transfer_over_cap` | ✓ 284 ms |
| ErrorHandlingTests | `Model_validation_error_uses_problem_shape_with_field_errors` | ✓ 58 ms |
| ErrorHandlingTests | `Malformed_json_body_returns_consistent_400` | ✓ 2 s |
| ErrorHandlingTests | `Domain_error_returns_problem_json_with_code` | ✓ 3 s |
| ErrorHandlingTests | `Not_found_wallet_returns_problem_json` | ✓ 184 ms |
| ErrorHandlingTests | `Same_wallet_transfer_is_rejected_with_specific_code` | ✓ 78 ms |

**[Screenshot: portal dashboard — live ₦210,000 balance after ₦40,000 transfer completed]**

---

> 15 tests, all passing, all against a **real PostgreSQL instance** via Testcontainers.
>
> Using a real database is not optional here. The concurrency guarantees rely on PostgreSQL row
> locks and the `xmin` concurrency token. An in-memory provider doesn't support `FOR UPDATE` or
> system columns, so any test using in-memory for the concurrency case would be testing nothing
> meaningful.
>
> The concurrency load test is the headline: 100 concurrent transfers, exactly 50 succeed,
> exactly 50 rejected, source drains to zero, destination holds exactly the right amount.
> Zero double-spends. Every run.
>
> The error-shape tests verify that every error path — not just the happy path — returns
> `application/problem+json` with consistent structure, including field-level error maps
> for validation failures.

---
---

## SLIDE 9 — Thank You / Q&A
**Duration: ~15 seconds (transition)**

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

> *This slide is a brief pause before the panel opens questions.
> The deeper technical interview is on 22 September — this session closes here.*
>
> Thank you. I'm happy to take any questions on the design decisions I've described.

---
---

## SLIDE 10 — AI Usage & Judgment
**Duration: ~1 minute**

---

### ON SCREEN

**AI USAGE & JUDGMENT**
*Used AI to move fast — and caught where it was wrong for a financial system*

---

**BUG 1 — Runtime crash in validation**
`[property: Required]` on positional records

> Applied `[property: Required]` to record primary-constructor params. Compiled fine.
> Crashed at runtime on every request with `InvalidOperationException` — ASP.NET Core silently
> ignores validation attrs on record *properties*; they must bind to the constructor parameter.
> **Caught** by running the service and hitting endpoints.

---

**BUG 2 — Concurrency lock silently broken** *(most critical)*
`SELECT * … FOR UPDATE` missing `xmin`

> AI wrote: `FromSqlRaw("SELECT * FROM wallets … FOR UPDATE")`.
> PostgreSQL system columns (`xmin`) are **NOT** returned by `SELECT *`. EF projected `xmin`
> as the concurrency token → *"column n.xmin does not exist"* on every transfer.
> The row lock still worked but the optimistic-lock backstop — the guard against any unlocked
> code path — was silently gone. A critical financial safety defect invisible at compile time.
> **Caught** by exercising credit/transfer against the live container.

---

**BUG 3 — Rate limiter bucketed all users together**
`httpContext.User.Identity?.Name` is null for JWT `sub`

> AI partitioned by `Identity.Name`. JwtBearer maps `sub` → `ClaimTypes.NameIdentifier`,
> not `Identity.Name` — so every caller shared the same IP-based bucket.
> **Caught** by the concurrency load test: expected 50 successes, got ~15 with `429 TooManyRequests`.
> Fixed by partitioning on `ClaimTypes.NameIdentifier ?? "sub" ?? Name ?? IP`.

---

> The brief specifically asks us to use AI and demonstrate judgment in catching where it's wrong.
>
> All three bugs would have shipped real defects in a production financial system.
>
> Bug 2 is the most instructive: the lock *appeared* to work. Money wouldn't go negative.
> But the `xmin` backstop — the guard against any code path that bypasses the explicit lock —
> was silently stripped. There was no compile error, no test failure on a mock database.
> The only way to catch it was running against real PostgreSQL and exercising the actual endpoint.
>
> That's the judgment the brief is testing: not whether you avoided AI, but whether you know
> what correct behaviour looks like and can verify it against a real system.

---
---

## SLIDE 11 — Operating Context
**Duration: ~1 minute**

---

### ON SCREEN

**OPERATING CONTEXT**
*Where the Nigerian fintech constraints touch this design specifically*

| Constraint | Where it attaches | Status |
|---|---|---|
| **₦ in kobo · no float** | Enforced end-to-end. Future: any feature that divides kobo (fee splits, FX, interest) produces remainders — document rounding policy before merging. | ✅ Implemented |
| **Tiered KYC (BVN/NIN)** | Daily limit is a function of KYC tier, not a global constant. `DailyOutboundLimitKobo` should resolve per-wallet from `KycTier`. Attaches to `TransferAsync` limit check. | ⚠ Seam identified — next build |
| **NIBSS NIP rails** | Inbound credit = external notification; idempotency must key on NIP session ID, not a client header. Outbound = `pending → settled/failed`, not instant. The outbox pattern is the right foundation for settlement callbacks. | ⚠ Outbox ready · states deferred |
| **USSD \*894#** | No JWT for feature phones. USSD gateway auth by MSISDN + PIN. Naira→kobo conversion must be server-side. Already enforced — UI conversion is convenience only. | ✅ Server-side kobo |
| **NDPA 2023** | Audit log stores `correlationId` + `customerId` — not raw BVN/NIN. Immutability vs. erasure rights: resolved by storing identifiers by reference. Retention window needed. | ⚠ Schema correct · retention TBD |
| **CBN consumer protection** | Customers must be told why a transfer failed — `insufficient_funds` / `daily_limit_exceeded` error codes do this. Honest gap: no reversal/refund path — consumer-protection requirement for failed NIP legs. | ⚠ Errors ✅ · Reversal TBD |

---

> The brief says "strong candidates will notice where these constraints matter."
> Here is exactly where each one attaches to *this ledger*.
>
> **The most important gap to call out:** the daily limit is currently a global constant.
> Under CBN's tiered KYC framework, it's a function of the customer's KYC tier.
> Tier 1 (phone-only) has lower caps than Tier 3 (full KYC). This is a one-model-field change —
> add `KycTier` to `Wallet`, update the limit resolver — but it's a real regulatory gap.
>
> **The NIP point is architectural:** outbound transfers to other banks need a `Pending` state
> and a settlement webhook. The outbox I've already built is exactly the right foundation.
>
> **The NDPA tension:** append-only audit trails conflict with data-subject erasure rights.
> Resolution: store only `customerId` (not raw PII), which this schema already does.

---
---

## SLIDE 12 — What I'd Build Next
**Duration: ~30 seconds**

---

### ON SCREEN

**WHAT I'D BUILD NEXT**
*Deliberate scope decisions — and the honest gaps*

**PRIORITY BACKLOG**

| # | What | Why |
|---|---|---|
| 1 | `KycTier` on `Wallet` → per-tier daily limit | CBN tiered KYC — most impactful regulatory gap |
| 2 | Reversal / refund endpoint | Consumer-protection requirement for failed NIP legs |
| 3 | `Pending` / `Settled` transaction states | Outbound NIP transfers are async, not instant-final |
| 4 | USSD adapter — MSISDN + PIN principal | Feature-phone / low-connectivity users on \*894# |
| 5 | NDPA retention policy on `audit_logs` | Data-subject erasure rights vs. immutable audit |

**KEY TRADE-OFFS**

| Decision | Rationale |
|---|---|
| Pessimistic locks over optimistic retry | Correct under burst load; requests queue cleanly rather than stampeding through retry loops. |
| Daily limit from history, not a counter | Always consistent; no reset cron job; no crash leaves a counter wrong. |
| Mock JWT issuer | The middleware and claims handling is what's scored — not building a full IdP. |

---

> Three deliberate trade-offs worth defending:
>
> **Pessimistic locks** — both pessimistic and optimistic can be made correct, but under high
> contention pessimistic is more predictable. Retries under optimistic concurrency can cause
> a stampede. For a money ledger I'd rather queue cleanly.
>
> **Limit from history** — slightly more expensive per transfer (one extra `SUM` query under the
> lock), but it's always consistent and needs no external scheduler.
>
> **Mock issuer** — the point of the auth requirement is the middleware and claims handling,
> not building an identity provider. The signing key is configurable via environment variable,
> and the README documents that this is a development default.
>
> Thank you — I'm ready to demo the live service, answer any questions,
> or modify code live if you'd like to see a specific change.

---
---

## Timing Summary

| # | Slide | Time |
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
| "Change the daily limit to be per KYC tier" | `Domain/Wallet.cs` — add `KycTier` enum; `Application/LedgerOptions.cs` — per-tier limit map; `Application/LedgerService.cs` — resolve limit from `wallet.KycTier` in `CurrentLimitWindowUtc()` |
| "Add a reversal endpoint" | `Controllers/TransfersController.cs` + `Application/LedgerService.cs` — new `ReverseAsync` that re-locks same wallets, inverts debit/credit, writes `Reversal` transaction type |
| "Show me where the row lock is" | `Application/LedgerService.cs` → `LockWalletAsync()` — the `SELECT … FOR UPDATE` raw SQL and the id-ordering logic |
| "Show me where idempotency is atomic" | `Application/LedgerService.cs` → `TransferAsync()` — find the `IdempotencyRecord` `.Add()` call inside the `await using var tx` block, before `SaveChangesAsync()` |
| "Make the audit endpoint admin-only" | `Controllers/WalletsController.cs` — add `[Authorize(Roles = "admin")]` to the `Audit` action; add `roles` claim in `Auth/TokenService.cs` |
