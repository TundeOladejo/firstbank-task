# NovaWallet Ledger Service — Presentation Speaker Notes
## 10-Minute Case Study · FirstBank NovaPay · Backend Engineer Assessment

---

> **How to use this document**
> Read through it once before the presentation. The timing markers [in brackets] are approximate.
> Your screen shares the slides; these notes are your voice. You don't need to read word-for-word —
> use them to anchor your points and make sure you hit the scored criteria on each slide.
> The panel is specifically scoring: Correctness & Concurrency · Code Quality · Test Rigour ·
> Security Awareness · AI Fluency & Judgment · Communication.

---

## SLIDE 1 — Title [0:00 – 0:30]

Good [morning / afternoon], thank you for your time.

I'm going to walk you through the **NovaWallet Ledger Service** — a concurrency-safe, production-realistic wallet ledger I built for the NovaWallet module of FirstBank NovaPay.

The one-sentence summary of what this service is: **it's the component that must never lose, duplicate, or miscount a customer's money.**

Everything I'll show you in the next ten minutes is a direct consequence of taking that seriously.

I'll cover: the problem, the architecture, the three hard problems — money integrity, concurrency, and idempotency — then tests, AI usage, the Nigerian operating context, and what I'd build next.

---

## SLIDE 2 — The Problem & Hard Constraints [0:30 – 1:30]

The brief gives eight functional requirements. I want to focus on the two that are genuinely hard to get right at the same time:

**First: all money must be stored as integers in kobo — no float, no decimal, no rounding drift.**

**Second: the balance must never go negative under any interleaving of concurrent requests.**

These two interact. If you get the kobo rule right but ignore concurrency, you can still double-spend. If you get concurrency right but introduce floating-point anywhere in the money path, you can silently miscount. I'll show how I enforced both.

The other five hard constraints — JWT auth, RFC 7807 errors, docker compose up startup, and all four stretch goals — are all implemented. I'll flag them on slide 7. For now, let me show you the architecture.

---

## SLIDE 3 — Architecture [1:30 – 3:00]

This is the architecture. Three columns: Clients on the left, the API layer in the middle, and the Application layer on the right. PostgreSQL underneath.

**The most important design decision is the gold box: LedgerService.**

Controllers bind and validate input, then delegate entirely to LedgerService. LedgerService is the single choke point for all balance mutations. There is exactly one place where balances change, and it is transactional. This makes the money invariants auditable and testable in isolation.

A few things worth calling out:

- The **RFC 7807 Exception Handler** sits between the controllers and the domain. Every error — whether from model validation, a domain rule, or an infrastructure fault — goes through a single `ProblemFactory` that stamps `correlationId`, `traceId`, and `timestamp` consistently. Clients always see one error shape.

- The **Outbox Dispatcher** is a hosted background service. It writes `TransferCompleted` events in the same transaction as the balance mutation, then drains them asynchronously. This is the at-least-once delivery pattern — no dual-write risk between the database and a message broker.

- The cross-cutting band at the bottom shows logging with correlation IDs, and the health/readiness endpoints at `/health/live` and `/health/ready` — suitable for Kubernetes probes.

---

## SLIDE 4 — Money Integrity [3:00 – 4:00]

The foundation is simple: **every monetary value is a `long` integer in kobo, everywhere.**

Entity fields, DTOs, EF column type mappings, arithmetic — all `long`. 1 NGN = 100 kobo exactly. No conversion from float anywhere in the path.

`checked()` arithmetic in C# means overflow throws an exception rather than silently wrapping to a wrong number. That's the right behaviour when the wrong number would be a customer's money.

The database CHECK constraint — `CHECK (BalanceKobo >= 0)` — is defence in depth. Even if a code bug escapes every application-level check, PostgreSQL will refuse to write a negative balance. It doesn't rely on the application logic being correct.

**The important note for future development:** the moment you introduce any feature that divides kobo — fee splits, interest calculations, FX on the remittance corridor — you get remainders. You need an explicit policy for who absorbs the leftover kobo before that code is merged. I documented this assumption in the README. Right now there's no division in this service, so we're clean.

---

## SLIDE 5 — Concurrency Safety [4:00 – 5:30]

This is the headline requirement, and the one I spent the most time on.

**The naive approach fails like this:** two concurrent requests both read `balance = 1000`. Both decide 1000 ≥ 600, so both proceed. Both debit 600. The balance ends at -200. That's a double-spend.

The fix sounds simple — lock the row before checking the balance — but the implementation detail that matters is *ordering*. If Transfer A locks wallet 1 then wallet 2, and Transfer B locks wallet 2 then wallet 1, they deadlock. 

Our approach: **always lock the two wallets in ascending wallet-ID order**. Both transfers take the locks in the same sequence, so the second one queues behind the first rather than deadlocking. Once the first commits, the second sees the updated balance and correctly rejects if funds are exhausted.

The lock is `SELECT … FOR UPDATE` inside a `ReadCommitted` transaction. The transaction holds both locks until commit, so no interleaving is possible.

**Here's the proof:** I wrote an automated test that fires 100 concurrent transfers against a wallet funded for exactly 50 of them. The assertion is:

- **Exactly 50 succeed**
- **Exactly 50 are rejected with insufficient funds**
- Source wallet balance drains to **exactly ₦0.00**
- Destination holds **exactly the right amount**
- Zero double-spends

That test passes on every run, against a real PostgreSQL instance — not a mock.

---

## SLIDE 6 — Idempotency [5:30 – 6:30]

Idempotency is critical for financial APIs because networks are unreliable. A client that times out on a transfer request doesn't know if the money moved. Without idempotency, a retry would move it twice.

The standard approach — store the idempotency record *after* the transfer commits — has a fatal race: the transfer commits, then a crash before the record is stored means the next retry runs the transfer again.

**Our approach: the idempotency record is inserted in the same database transaction as the balance mutation.** They commit together or roll back together. There is no window.

Concurrent replays race on a unique index on the key. The first writer commits both the transfer and the record. The second writer hits the unique constraint violation, rolls back its transfer entirely, reads the winner's stored response, and returns it. The money moved exactly once regardless of how many concurrent retries fired.

Two outcomes clients need to handle:
- **Same key + same body** → 201, `Idempotent-Replayed: true` header, money moved once
- **Same key + different body** → 409 Conflict, `idempotency_key_reused` error code, original transfer unchanged

---

## SLIDE 7 — All Requirements Met [6:30 – 7:00]

Quickly confirming every requirement from the brief:

- **Daily limit**: ₦500,000 per wallet per WAT day. Computed from actual `TransferOut` rows under the same row lock, not a separate counter — so it's always consistent with concurrent transfers.

- **Audit log**: separate `audit_logs` table, append-only, hash-chained. Each row stores a SHA-256 of the previous row's hash — tamper-evidence detectable by recomputing the chain. Separate from `transactions` as the brief requires.

- **Statement**: paginated, newest-first, with `totalCount` for client-side paging.

- **JWT**: full validation of issuer, audience, lifetime, HMAC signature. Mock issuer at `/api/auth/token` stands in for a real IdP.

- **Errors**: every path returns `application/problem+json` with a machine-readable error code the client can branch on.

- **All four stretch goals**: rate limiting per authenticated subject, transactional outbox, correlation IDs in every log and response, health/readiness endpoints.

---

## SLIDE 8 — Test Rigour [7:00 – 7:30]

15 tests, all passing, all against a **real PostgreSQL instance** via Testcontainers.

Using a real database is not optional here. The concurrency guarantees rely on PostgreSQL row locks and the `xmin` concurrency token. An in-memory provider doesn't support `FOR UPDATE` or system columns, so a test using in-memory for the concurrency case would be testing nothing meaningful. I made a deliberate choice to use Testcontainers and spin up real Postgres for every test run.

The test suite covers:
- The **concurrency load test** — 100 concurrent transfers, exact assertions
- The **idempotency race test** — 25 concurrent replays of the same key, money moves once
- All domain rules: insufficient funds, daily cap, same-wallet rejection, wallet not found
- **Error shape tests** — assert every error returns `application/problem+json` with consistent extensions

---

## SLIDE 9 — AI Usage & Judgment [7:30 – 8:30]

The brief specifically asks us to use AI and demonstrate judgment in catching where it's wrong. I used an AI coding assistant throughout, and it caught three bugs that would have been real defects in production.

**Bug 1 — Runtime crash on validation.** AI used `[property: Required]` on positional record parameters. It compiled. In production every request would 500 with an `InvalidOperationException` because ASP.NET Core silently ignores validation attributes on record *properties* — they must be on constructor *parameters*. Caught by running the service and hitting endpoints.

**Bug 2 — Concurrency lock silently broken.** This is the most serious one. AI generated `SELECT * FROM wallets … FOR UPDATE`. PostgreSQL system columns — including `xmin`, which is the EF concurrency token — are not returned by `SELECT *`. The lock still worked, but the `xmin` backstop — the guard against any unlocked code path — was silently gone. The error only appeared at runtime: `column n.xmin does not exist`. Caught by exercising credit and transfer against the live container. Without that test, this defect would have been invisible.

**Bug 3 — Rate limiter bucketed all users together.** AI partitioned by `User.Identity.Name`, which is null for JWT tokens that only carry a `sub` claim. JwtBearer maps `sub` to `ClaimTypes.NameIdentifier`, not `Identity.Name`. Every caller shared the same IP-based bucket. Caught by the concurrency load test: expected 50 successes, got ~15, with `429 TooManyRequests` responses.

The judgment that matters is: running the service against a real database, writing a concurrency test with exact invariants, and knowing what the correct behaviour should be before you can tell when the AI got it wrong.

---

## SLIDE 10 — Operating Context [8:30 – 9:30]

The brief says "strong candidates will notice where these constraints matter." Here is exactly where each one attaches to this ledger.

**Kobo and no float**: already enforced. The constraint that matters going forward is: any future feature that divides — fee splits, interest, FX — needs a documented rounding policy before merging.

**Tiered KYC (BVN/NIN)**: the daily limit is currently a global constant. Under CBN's tiered KYC framework, it's a function of the customer's KYC tier. Tier 1 (phone-only) has lower caps than Tier 3 (full KYC). This attaches directly to the `CurrentLimitWindowUtc()` check in `LedgerService.TransferAsync`. The fix is a `KycTier` field on the wallet and a resolver function — a one-model-field change, but a real regulatory gap.

**NIBSS NIP rails**: inbound credit is an external notification that NIBSS can resend — so idempotency must be keyed on the NIP session ID, not a client-supplied header. Outbound transfers to other banks are `pending → settled/failed`, not instant-final. The outbox pattern I've already built is the right foundation for the settlement callback.

**USSD `*894#`**: feature phones have no JWT. The USSD gateway authenticates by MSISDN + PIN and calls the API on the user's behalf — a different principal type. The naira→kobo conversion must be server-side; USSD sends plain text amounts. Our API always receives kobo, so this is already correct.

**NDPA 2023**: the audit log stores `correlationId` and `customerId` — not raw BVN or NIN. The immutability of `audit_logs` is in tension with a data-subject erasure request. The resolution: store identifiers by reference and define a retention window. Schema is already correct; retention policy is documented as deferred.

**CBN consumer protection**: customers must be told why a transfer failed — `insufficient_funds` and `daily_limit_exceeded` codes satisfy this. The honest gap is a **reversal/refund path**. A failed outbound NIP leg must return funds — a consumer-protection requirement. That's the next thing to build.

---

## SLIDE 11 — Demo & What I'd Build Next [9:30 – 10:00]

The service is running now. `docker compose up --build` starts everything. Migrations apply automatically. The web console at `localhost:8080` drives all the API endpoints — what you see in the screenshot is a live transfer that just completed: ₦40,000 sent, source balance ₦210,000, "Transfer complete" toast, stat cards updated from real data.

For the technical interview I'll walk through the full live demo: sign in, create wallets, credit, transfer with an idempotency key, replay it to show `Idempotent-Replayed: true`, try to exceed the daily cap, browse the statement and audit trail.

**The two trade-offs I made deliberately:**

One — pessimistic row locks over optimistic retry loops. Both can be correct. Under high contention, pessimistic is more predictable: requests queue cleanly rather than stampeding through retry loops. For a money ledger I'd rather queue than retry-storm.

Two — daily limit from transaction history, no reset cron job. Slightly more expensive per transfer, but always consistent and needs no scheduled task. No risk of a crash leaving a counter wrong.

**What I'd build next, in order:**

1. `KycTier` on Wallet → per-tier daily limit driven by the CBN framework
2. Reversal / refund endpoint — consumer-protection requirement
3. Pending / settled transaction states for NIP outbound transfers
4. NDPA retention policy on `audit_logs`
5. USSD adapter — MSISDN + PIN principal type

Thank you. I'm happy to take any questions, and I'm ready to modify code live if you'd like to see a specific change.

---

## Timing Summary

| Slide | Content | Target time |
|---|---|---|
| 1 | Title & intro | 0:30 |
| 2 | Problem & constraints | 1:00 |
| 3 | Architecture | 1:30 |
| 4 | Money integrity | 1:00 |
| 5 | Concurrency safety | 1:30 |
| 6 | Idempotency | 1:00 |
| 7 | All requirements | 0:30 |
| 8 | Tests | 0:30 |
| 9 | AI usage | 1:00 |
| 10 | Operating context | 1:00 |
| 11 | Demo & next steps | 0:30 |
| **Total** | | **~10:00** |

---

## Quick reference — scored criteria mapped to slides

| Assessment criterion | Key slides |
|---|---|
| Correctness & concurrency safety | 4, 5, 6 |
| Code quality & architecture | 3 |
| Test rigour | 8 |
| Security awareness | 2, 7 |
| AI fluency & judgment | 9 |
| Communication | All — especially 10, 11 |

---

## If they ask you to modify code live

Likely prompts and where to look:

- *"Change the daily limit to be per KYC tier"* → `Domain/Wallet.cs` (add `KycTier` enum), `Application/LedgerOptions.cs` (per-tier limits), `Application/LedgerService.cs` (`CurrentLimitWindowUtc()` → resolve limit from wallet's tier)
- *"Add a reversal endpoint"* → `Controllers/TransfersController.cs` + `Application/LedgerService.cs` — new `ReverseAsync` that re-locks the same wallets, inverts the debit/credit, writes a `Reversal` transaction type
- *"Show me where the row lock is"* → `Application/LedgerService.cs`, `LockWalletAsync()` method — the `SELECT … FOR UPDATE` raw SQL
- *"Show me where idempotency is made atomic"* → same file, `TransferAsync()` — look for the `IdempotencyRecord` `Add()` call inside the `await using var tx` block, before `SaveChangesAsync()`
- *"Make the audit log endpoint admin-only"* → `Controllers/WalletsController.cs` — add `[Authorize(Roles = "admin")]` to the `Audit` action, add `roles` claim to `TokenService`
