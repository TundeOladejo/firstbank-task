# Interview Day — Quick Reference Cheat Sheet

Print this or keep it visible beside your screen.

---

## Before you start — run these commands

```bash
# In terminal 1 — start the service
cd /Users/babz/Desktop/firstbank-task
docker compose up --build           # data persists from last session ✓

# ONLY if you want a completely empty database:
# docker compose down -v && docker compose up --build

# In terminal 2 — ready to run tests
cd /Users/babz/Desktop/firstbank-task
# Run when panel asks for it:
DOTNET_ROLL_FORWARD=Major dotnet test tests/NovaWallet.Tests/NovaWallet.Tests.csproj
```

**URLs to have ready:**
- Web console: `http://localhost:8080`
- Swagger UI: `http://localhost:8080/swagger`

---

## The 5 things to say — no matter what else

1. "All money is stored as `long` integers in kobo. No float. 1 NGN = 100 kobo."
2. "The transfer uses `SELECT FOR UPDATE` row locks, taken in wallet-ID order to prevent deadlocks."
3. "The idempotency record is written in the same transaction as the money movement. Atomic."
4. "The audit log is hash-chained — tamper with any row and the chain breaks."
5. "The concurrency test fires 100 simultaneous requests and asserts exact numbers."

---

## The transfer flow — know this cold

**What happens when Alice transfers ₦4,000 to Bob:**

1. Client sends POST /api/transfers with Idempotency-Key header
2. JWT middleware validates the bearer token (→ 401 if invalid)
3. Rate limiter checks (→ 429 if too many)
4. TransfersController receives request, hashes the body, calls LedgerService
5. Check if idempotency key exists → if yes, return stored response
6. Open database transaction
7. Lock Alice's wallet row AND Bob's wallet row (lower ID first, prevents deadlocks)
8. Under the lock: check Alice has enough funds (→ 422 if not)
9. Under the lock: check Alice's daily limit isn't exceeded (→ 422 if exceeded)
10. Subtract from Alice, add to Bob
11. Write TransferOut row for Alice, TransferIn row for Bob
12. Write audit entries for both wallets (hash-chained)
13. Write OutboxMessage (TransferCompleted)
14. Write idempotency record
15. Commit — everything at once, or roll back if anything fails
16. Return 201 Created

---

## File locations for live coding

| If they ask about... | Open this file |
|---|---|
| Row locking | `src/NovaWallet.Api/Application/LedgerService.cs` → `LockWalletAsync()` |
| Idempotency atomic record | `LedgerService.cs` → `TransferAsync()` → find `db.IdempotencyRecords.Add` inside `await using var tx` |
| Balance check | `LedgerService.cs` → `TransferAsync()` → `if (from.BalanceKobo < req.AmountKobo)` |
| Daily limit | `LedgerService.cs` → `TransferAsync()` → `spentToday` / `DailyOutboundLimitKobo` |
| DB constraint | `src/NovaWallet.Api/Persistence/LedgerDbContext.cs` → `HasCheckConstraint` |
| KYC tier change | `Domain/Wallet.cs` → add `KycTier`, `Application/LedgerService.cs` → use it in limit check |
| Audit hash chain | `LedgerService.cs` → `AppendAuditAsync()` → `canonical` string and `Hashing.Sha256Hex` |
| Error format | `src/NovaWallet.Api/Infrastructure/ProblemFactory.cs` |
| JWT setup | `src/NovaWallet.Api/Program.cs` → `AddAuthentication` section |
| Concurrency test | `tests/NovaWallet.Tests/ConcurrencyTests.cs` |

---

## The 3 AI bugs — short version

| Bug | What AI did | What was wrong | How caught |
|---|---|---|---|
| 1 | `[property: Required]` on records | ASP.NET ignores it, every request crashed | Hit live endpoint |
| 2 | `SELECT *` with `FOR UPDATE` | `xmin` not returned by SELECT *, EF crashed, safety backstop gone | Live container |
| 3 | Rate limit by `Identity.Name` | `Name` is null for JWT, everyone shared one bucket | Load test: expected 50, got 15 |

---

## Terminology quick reference

| Term | Plain English |
|---|---|
| **kobo** | 1/100 of a Naira. Smallest unit. Used to avoid floating point. |
| **JWT** | A signed token that proves who you are. Sent with every request. |
| **SELECT FOR UPDATE** | PostgreSQL lock: "nobody else can touch this row until I'm done" |
| **xmin** | PostgreSQL's automatic row version number. Detects if a row was changed. |
| **Idempotency** | Doing the same thing twice has the same effect as doing it once |
| **Idempotency-Key** | A unique ID the client sends so the server can detect retries |
| **Outbox pattern** | Write events to the database (same transaction), send them later |
| **RFC 7807** | The standard format for API error responses |
| **EF Core** | Library that translates C# database queries to SQL |
| **Testcontainers** | Starts real Docker containers from inside tests |
| **Correlation ID** | A unique ID attached to every request for tracking in logs |
| **Migration** | A file that describes how to change the database schema |
| **LINQ** | C# syntax for querying collections and databases |
| **async/await** | Run database calls without freezing the server |
| **Dependency Injection** | Give a class what it needs from outside, not create it inside |
| **Middleware** | Steps every HTTP request passes through in order |

---

## If you're asked something you don't know

Say this:

> "I don't have that off the top of my head, but I can reason through it.
> In the context of this system, I would approach it by [explain what you do know
> about the surrounding area]."

**Do not guess. Do not pretend. They respect honesty.**

---

## Three phrases that make you sound strong

Instead of "I don't know C#" say:
> "My background is in [your actual experience]. For this project I focused on the
> design decisions and financial safety guarantees — the concurrency, idempotency,
> and audit trail patterns. Those concepts apply regardless of language."

Instead of "The AI wrote this" say:
> "I used AI to accelerate the implementation, but I reviewed every critical section,
> particularly the money path, the locking strategy, and the idempotency design.
> The three bugs I caught and fixed are documented in AI_USAGE.md."

Instead of "I'm not sure about .NET" say:
> "The .NET specifics like dependency injection and middleware are fairly standard
> patterns across frameworks. The interesting decisions in this project are
> language-agnostic — they're about database locking, atomicity, and financial
> system design."

---

## Good luck 🐘
