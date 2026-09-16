# NovaWallet Ledger Service

A simplified, concurrency-safe wallet ledger for the FirstBank NovaPay **NovaWallet** module,
built with **C# / .NET 9**, **PostgreSQL**, and **EF Core**. It is the component that must never
lose, duplicate, or miscount a customer's money, so every design choice below is made in service of
that invariant.

---

## Quick start

The service and its datastore start with a single command:

```bash
docker compose up --build
```

Then open Swagger UI: **http://localhost:8080/swagger**

The API runs on port `8080`; PostgreSQL on `5432`. Database migrations are applied automatically on
startup, so the service is ready as soon as the container reports listening.

### Try it end to end

```bash
BASE=http://localhost:8080

# 1. Get a (mock) bearer token
TOKEN=$(curl -s -X POST $BASE/api/auth/token -H 'Content-Type: application/json' \
  -d '{"subject":"demo"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessToken"])')
AUTH="Authorization: Bearer $TOKEN"

# 2. Create two wallets
W1=$(curl -s -X POST $BASE/api/wallets -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"customerId":"cust-1"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["walletId"])')
W2=$(curl -s -X POST $BASE/api/wallets -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"customerId":"cust-2"}' | python3 -c 'import sys,json;print(json.load(sys.stdin)["walletId"])')

# 3. Credit W1 with ₦10,000 (1,000,000 kobo)
curl -s -X POST $BASE/api/wallets/$W1/credits -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"amountKobo":1000000,"reference":"seed"}'

# 4. Transfer ₦4,000 with an idempotency key (retry-safe)
curl -s -X POST $BASE/api/transfers -H "$AUTH" -H "Idempotency-Key: $(uuidgen)" \
  -H 'Content-Type: application/json' \
  -d "{\"fromWalletId\":\"$W1\",\"toWalletId\":\"$W2\",\"amountKobo\":400000}"

# 5. Check balances and statement
curl -s $BASE/api/wallets/$W1/balance -H "$AUTH"
curl -s "$BASE/api/wallets/$W1/statement?page=1&pageSize=10" -H "$AUTH"
```

---

## Architecture

The service is a single ASP.NET Core Web API layered by concern:

```
src/NovaWallet.Api
├── Domain/           Entities: Wallet, Transaction, AuditLog, IdempotencyRecord, OutboxMessage
├── Contracts/        Request/response DTOs (validation lives here)
├── Application/      LedgerService (all money logic), LedgerOptions, LedgerException, IClock
├── Persistence/      LedgerDbContext + EF Core migrations
├── Auth/             JwtOptions, TokenService (mock issuer)
├── Infrastructure/   RFC 7807 exception handler, correlation-id middleware, outbox dispatcher
├── Controllers/      Auth, Wallets, Transfers
└── Program.cs        Composition root
```

**Why this shape.** Controllers stay thin: they bind and validate input, then delegate to
`LedgerService`, which owns every balance mutation. That single choke point is what makes the money
invariants auditable — there is exactly one place where balances change, and it is transactional.

### Money is always integer kobo

Every monetary value is a `long` in **kobo** (1 NGN = 100 kobo). There is no `float`/`double`
anywhere in the money path — in the entities, DTOs, EF mappings, or arithmetic. Additions use
`checked(...)` so an overflow throws rather than silently wrapping. Storing minor units as integers
removes floating-point drift entirely.

### Concurrency safety (the core requirement)

A transfer runs inside a database transaction and takes an explicit **row-level write lock** on each
wallet via `SELECT ... FOR UPDATE`:

1. Lock the two wallet rows, **ordered by wallet id**. Deterministic lock ordering prevents deadlocks
   when two transfers touch the same pair of wallets in opposite directions.
2. Re-read balances under the lock, enforce the daily limit and the sufficient-funds check.
3. Debit and credit, write both ledger rows, append audit entries, enqueue the outbox event.
4. Commit. The lock is held for the whole transaction, so concurrent transfers on the same wallet
   serialize and cannot interleave into a double-spend.

Defense in depth: a **`CHECK (BalanceKobo >= 0)`** constraint on the `wallets` table means the
database itself rejects any write that would drive a balance negative, independent of application
logic. The `wallets` row also carries PostgreSQL's `xmin` as an EF optimistic-concurrency token,
which catches lost updates on any code path that doesn't take the explicit lock.

This is proven under load by an automated test: 100 concurrent transfers against a wallet funded for
only 50 result in **exactly 50 successes and 50 rejections**, with the source draining to exactly
zero and the destination holding exactly the transferred sum.

### Idempotency

The transfer endpoint accepts an `Idempotency-Key` header. The idempotency record is written **in the
same database transaction as the balance mutation**, keyed by a unique index. This is the important
subtlety:

- **Replay with the same key + same body** returns the stored original response
  (`Idempotent-Replayed: true`), and the money moves only once.
- **Reuse of a key with a different body** is rejected with `409 Conflict` (the stored request hash
  no longer matches).
- **Concurrent replays** race on the unique index: the first to commit wins, the losers roll back
  their transfer entirely and return the winner's response. There is no window where a transfer
  commits but its idempotency record does not.

### Daily limit

A server-side outbound limit (default **₦500,000 = 50,000,000 kobo**) is enforced per source wallet.
The window is the current day at **midnight West Africa Time** (`Africa/Lagos`, UTC+1, no DST). Spend
is computed by summing that wallet's outbound transfers within the window under the same lock, so the
check is consistent with concurrent transfers.

### Audit trail

Every balance mutation appends a row to an **append-only, immutable** `audit_logs` table, separate
from `transactions` so auditors have an independent record. Each row is **hash-chained**: it stores a
SHA-256 of the previous row for that wallet plus its own contents, so any tampering or deletion breaks
the chain and is detectable.

### Errors

All errors return **RFC 7807 Problem Details** with a stable `type`, a machine-readable `title`
(error code), a human `detail`, and the request `correlationId`. Domain failures map to specific
codes (`insufficient_funds` → 422, `daily_limit_exceeded` → 422, `idempotency_key_reused` → 409,
`wallet_not_found` → 404, `validation_error` → 400).

### Security

- Every business endpoint requires a **JWT bearer token**, validated for issuer, audience, lifetime,
  and HMAC signature. `POST /api/auth/token` is a **mock issuer** standing in for a real IdP — the
  point is the middleware and claims handling, not building an auth server.
- Input is validated with data annotations; amounts must be positive integers.
- The signing key is configurable and must be supplied as a secret in any real deployment (the
  in-repo value is a clearly-labelled development default).

### Stretch goals implemented

- **Rate limiting** on the transfer endpoint (token bucket, partitioned per authenticated subject).
- **Transactional outbox** publishing a `TransferCompleted` event, drained by a background dispatcher
  (logs stand in for a real broker).
- **Correlation IDs** honoured from `X-Correlation-ID` (or generated), echoed on responses, and pushed
  into the logging scope so every log line for a request is correlated.
- **Health/readiness** endpoints at `/health/live` and `/health/ready` (the latter checks the DB).

---

## API summary

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/api/auth/token` | Issue a mock bearer token (unauthenticated) |
| POST | `/api/wallets` | Create a wallet (balance starts at 0) |
| GET | `/api/wallets/{id}/balance` | Current balance + currency |
| POST | `/api/wallets/{id}/credits` | Credit a wallet (inbound NIP simulation) |
| POST | `/api/transfers` | Atomic transfer; accepts `Idempotency-Key` |
| GET | `/api/wallets/{id}/statement` | Paginated transactions, newest first |
| GET | `/api/wallets/{id}/audit` | Paginated append-only audit trail |
| GET | `/health/live`, `/health/ready` | Liveness / readiness |

---

## Running the tests

```bash
dotnet test
```

Tests spin up a real PostgreSQL instance via **Testcontainers** (Docker must be running) and exercise
the full HTTP pipeline with `WebApplicationFactory`. A real database is required because the
concurrency guarantees depend on PostgreSQL row locks and the `xmin` token, which the in-memory
provider does not emulate. The suite includes the concurrency-under-load test described above.

---

## Assumptions & trade-offs

- **.NET 9** was chosen (the brief allows 8 or 9). The Docker image pins `dotnet/sdk:9.0`, so the
  panel's build uses .NET 9 regardless of the host SDK. A `global.json` with `rollForward: latestMajor`
  lets the project build on a machine that only has a newer SDK installed.
- **Concurrency via pessimistic row locks** (`FOR UPDATE`) rather than optimistic retry loops. For a
  ledger where correctness beats throughput, taking the lock is simpler to reason about and avoids
  retry storms under contention. The `xmin` token remains as a backstop.
- **Daily limit uses transfer history**, not a separate counter, so it is always consistent with the
  ledger and needs no reset job. WAT has no daylight saving, so the window is unambiguous.
- **Outbox "publish" logs** rather than integrating a broker, to keep the service self-contained for
  the panel while still demonstrating the pattern (write event in the same transaction, dispatch
  asynchronously, at-least-once).
- **Scope left out on purpose** (per the brief's guidance to prioritize): tiered KYC/BVN limits, the
  USSD channel, multi-currency/FX, refunds/reversals, and token refresh. The ledger core and its hard
  constraints were prioritized instead.

See `AI_USAGE.md` for how AI tools were used and where their output had to be corrected.
