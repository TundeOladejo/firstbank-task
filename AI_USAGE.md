# AI Usage

This project was built with AI assistance, as encouraged by the brief. This file documents which
tools were used, representative prompts, and — most importantly — concrete cases where the AI's
output was wrong or naive for a financial system, how it was caught, and how it was fixed.

## Tools used

- **AI coding assistant (agentic, in-IDE)** — used to scaffold the solution, draft the domain model,
  EF Core mappings, the transfer/idempotency logic, controllers, Docker setup, and the test suite.
- Used it as a fast pair-programmer for boilerplate and for a first draft of the concurrency logic,
  then reviewed and corrected the output against how PostgreSQL and EF Core actually behave.

## Representative prompts (paraphrased) and what came back

1. **"Implement a concurrency-safe wallet transfer in EF Core + PostgreSQL where the balance can
   never go negative under concurrent load, money in kobo as integers."**
   Returned a `LedgerService.Transfer` using a transaction and a `SELECT ... FOR UPDATE` row lock.
   The shape was right, but the raw SQL had a real defect (see Bug #2) and the initial draft locked
   rows in request order rather than a canonical order, which I changed to id-ordered locking to
   avoid deadlocks.

2. **"Make the transfer endpoint idempotent with an Idempotency-Key header — replay returns the
   original result, reuse with a different body is a 409."**
   The first draft stored the idempotency record in a *separate* step from the transfer. I rejected
   that: it leaves a window where the transfer commits but the record does not (or vice versa),
   allowing a double-process on retry. I moved the record insert into the *same* transaction as the
   balance mutation, keyed by a unique index, so replay protection is atomic with the money movement.

3. **"Add data-annotation validation to the request DTOs (positive amounts, required fields)."**
   Returned records with `[property: Required]` / `[property: Range]` attributes. This compiled but
   crashed at runtime (see Bug #1).

## Cases where the AI was wrong / naive — and how I caught and fixed them

### Bug #1 — Runtime crash from validation attributes on record properties
The AI annotated positional records with `[property: Required]`, e.g.
`public record TransferRequest([property: Required] Guid FromWalletId, ...)`. It compiled, but every
request 500'd with:

> `InvalidOperationException: Record type '...' has validation metadata defined on property '...'
> that will be ignored. '...' is a parameter in the record primary constructor and validation
> metadata must be associated with the constructor parameter.`

**How caught:** running the service under `docker compose up` and hitting the endpoints — the
RFC 7807 handler surfaced a 500. The happy-path compile hid it.
**Fix:** apply the attributes to the constructor parameter (drop the `property:` target), e.g.
`[Required] Guid FromWalletId`. Fixed across `Contracts/Dtos.cs` and the token DTO.

### Bug #2 — Concurrency lock silently broken by `SELECT *` and a Postgres system column
The AI's row lock used `FromSqlRaw("SELECT * FROM wallets WHERE \"Id\" = {0} FOR UPDATE")`. Because
the `Wallet` entity maps PostgreSQL's `xmin` system column as its optimistic-concurrency token, and
`SELECT *` does **not** return system columns, EF tried to project a column that wasn't in the result
set. Every credit and transfer failed with:

> `PostgresException: column n.xmin does not exist`

This is exactly the kind of error that is easy to miss: the SQL "looks" correct, and a naive
implementation without a concurrency token would have appeared to work while providing no protection
against lost updates.
**How caught:** exercising credit/transfer against the real Postgres container returned 500s; the
logs pointed at the `xmin` projection.
**Fix:** list the columns explicitly in the raw SQL, including `xmin`, so the lock query returns the
full entity. This preserves both the `FOR UPDATE` lock and the concurrency token.

### Bug #3 — Rate-limiter partition key was effectively global for our tokens
The AI partitioned the transfer rate limiter by `httpContext.User.Identity?.Name`. Our mock tokens
only carry a `sub` claim, and the JWT bearer handler maps `sub` to `ClaimTypes.NameIdentifier`, not
to `Identity.Name` — so `Identity.Name` was null and **every** authenticated caller fell back to the
same (IP-based) partition. The concurrency load test exposed this: instead of 50 transfers
succeeding, only ~15–20 did, because the shared limiter returned `429` to the rest. A limiter that
silently throttles distinct users into one bucket is both a correctness and a fairness problem.

**How caught:** the concurrency-under-load test asserted "exactly 50 succeed" and instead saw ~20,
with the failures being `TooManyRequests` rather than the expected `insufficient_funds`.
**Fix:** partition by `ClaimTypes.NameIdentifier` first, then the raw `sub` claim, then `Identity.Name`,
then IP. The tests were also updated to use a distinct subject per concurrent client so the
rate limiter (a separate concern) does not mask the ledger-concurrency behaviour under test.

## Takeaway

AI accelerated the boilerplate and gave a reasonable first draft of the hard parts, but each of the
three bugs above would have shipped a real defect in a money system — a broken lock, a non-atomic
idempotency window, and a request-validation crash. They were caught by **actually running the
service against a real database and by writing a concurrency test with exact invariants**, not by
reading the code alone. The judgment that mattered was insisting on those checks and knowing what the
correct behaviour should be.
