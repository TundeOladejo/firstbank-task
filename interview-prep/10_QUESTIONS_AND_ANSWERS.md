# Questions the Panel Will Ask — and How to Answer Them

This file lists the most likely questions, the correct answers, and the exact words
you can use. Read each one until you can say it naturally without reading.

---

## Architecture questions

---

**"Walk me through the folder structure."**

> "The project is divided into layers by concern.
>
> `Domain/` contains the data models — Wallet, Transaction, AuditLog, IdempotencyRecord,
> OutboxMessage. These are plain C# classes that describe the data.
>
> `Application/` contains `LedgerService` — the only place where money moves.
> All balance mutations happen here. Controllers never touch the database directly.
>
> `Controllers/` are the HTTP receivers. They validate the input and delegate to LedgerService.
> They're deliberately thin — no business logic.
>
> `Persistence/` has the database context — the class that connects to PostgreSQL and
> defines how the C# models map to database tables.
>
> `Auth/` handles JWT token issuance.
>
> `Infrastructure/` has the error handler, the correlation ID middleware, and the outbox
> background service.
>
> `Program.cs` wires everything together — it's the single composition root where all
> services are registered and the middleware pipeline is configured."

---

**"Why did you separate the domain layer from the persistence layer?"**

> "The domain objects — Wallet, Transaction, etc. — describe what the data IS.
> The database context describes how that data is STORED. These are separate concerns.
>
> If I wanted to change from PostgreSQL to a different database, I'd only change the
> persistence layer. The domain objects and the business logic wouldn't change.
>
> It also means the business logic in LedgerService is testable without a database —
> you can inject a fake database context."

---

## Money and safety questions

---

**"Why do you store money in kobo instead of naira?"**

> "Floating point numbers cannot represent all decimal values precisely.
> 0.1 + 0.2 in floating point is 0.30000000000000004 — not 0.3.
>
> In a financial system, that error IS real money. If you process millions of transactions
> with floating point rounding, customers lose or gain fractions of naira. Over time
> that adds up.
>
> By storing everything as integers in kobo, there is no decimal point, no rounding,
> no floating point drift. 1 NGN = 100 kobo exactly. The arithmetic is always exact."

---

**"What prevents the balance from going negative?"**

> "Three layers of defence:
>
> First, the application code checks `if (from.BalanceKobo < req.AmountKobo)` before
> deducting. If there aren't enough funds, it throws an InsufficientFunds error.
>
> Second, the row lock means this check is done under an exclusive lock.
> No other transaction can change the balance between the check and the deduction.
> So the check always reflects the true current balance.
>
> Third, the database itself has a CHECK constraint: `CHECK (BalanceKobo >= 0)`.
> Even if a bug in the application somehow bypassed the first two checks,
> PostgreSQL would reject the write."

---

**"What is `checked()` in C#?"**

> "In C#, if you add two large integers and the result is too big to fit in a `long`
> — a 64-bit integer — it normally wraps around to a wrong number silently.
>
> `checked()` changes that behaviour. If the arithmetic overflows, it throws an
> `OverflowException` instead. In a bank, an overflow producing a wrong balance is
> much worse than an exception stopping the transaction.
>
> I use `checked(toBefore + req.AmountKobo)` when adding to a wallet balance."

---

## Concurrency questions

---

**"How does `SELECT FOR UPDATE` work?"**

> "PostgreSQL has row-level locking. `SELECT ... FOR UPDATE` takes an exclusive lock
> on the selected rows. No other transaction can modify — or even lock — those rows
> until the current transaction commits or rolls back.
>
> So when a transfer runs, it locks both wallet rows first. If another transfer tries to
> lock either of those rows, it waits. By the time the second transfer gets the lock,
> it reads the balance that reflects the first transfer's changes.
>
> This serialises concurrent transfers on the same wallet — they queue up rather than
> racing."

---

**"What is a deadlock and how do you prevent it?"**

> "A deadlock happens when two operations are each waiting for something the other holds.
>
> Transfer A locks wallet 1, then tries to lock wallet 2.
> Transfer B locks wallet 2, then tries to lock wallet 1.
> Both are stuck waiting for each other forever.
>
> I prevent this by always locking wallets in a deterministic order — sorted by wallet ID,
> lowest first. So both Transfer A and Transfer B always try to lock the lower ID wallet first.
>
> If the lower ID wallet is wallet 1, both transfers try to lock wallet 1 first.
> One wins and gets the lock. The other waits. When the first commits, the second
> gets wallet 1, then wallet 2. No deadlock is possible."

---

**"What is `xmin` in PostgreSQL?"**

> "Every row in PostgreSQL has a system column called `xmin`. PostgreSQL automatically
> updates it every time the row is modified. It tracks which transaction created or
> last modified the row.
>
> I map it as EF Core's concurrency token. When EF saves a change to a wallet, it
> includes `WHERE xmin = [original value]` in the UPDATE statement. If another
> transaction modified the row between when we read it and when we're saving,
> the xmin will be different, the UPDATE matches 0 rows, and EF throws
> `DbUpdateConcurrencyException`.
>
> It's a backstop — it catches any code path that bypasses the explicit `FOR UPDATE` lock."

---

**"What happens if the database goes down mid-transfer?"**

> "The transfer is wrapped in a database transaction. If the connection to the database
> drops mid-transfer, the transaction automatically rolls back — none of the changes
> are saved. No partial state exists. Money hasn't left the source wallet without
> reaching the destination.
>
> When the database comes back up, the client would retry the request. If they use an
> idempotency key, the retry is safe — the idempotency record would either have been
> committed (transfer completed, retry gets stored response) or rolled back (transfer
> didn't happen, retry runs it fresh).
>
> I also use an execution strategy that automatically retries on transient database
> connection failures."

---

## Idempotency questions

---

**"What is idempotency and why does it matter?"**

> "An idempotent operation produces the same result whether you call it once or a
> hundred times. For a transfer, idempotency means: no matter how many times you
> retry, the money moves exactly once.
>
> This matters because networks are unreliable. If a client sends a transfer request
> and the response is lost in transit, the client doesn't know if the transfer happened.
> If it retries without idempotency protection, it might move the money twice.
>
> With idempotency, the retry returns the same response as the original request
> without moving money again."

---

**"Why does the idempotency record need to be in the same transaction as the transfer?"**

> "If the idempotency record were saved AFTER the transfer committed, there's a gap.
> The transfer commits. Before we save the idempotency record, there's a crash or
> a network failure. The record never gets saved. The next retry finds no record
> and runs the transfer again. Money moves twice.
>
> By saving the record in the same transaction as the transfer, they commit together.
> There is no gap. Either both the transfer and the record commit, or neither does.
> A retry always finds either a committed record or no record — there's no state
> where money moved but the record doesn't exist."

---

## Testing questions

---

**"Why did you use real PostgreSQL for tests instead of an in-memory database?"**

> "The main safety guarantee of this system — that the balance never goes negative
> under concurrent load — relies on two PostgreSQL-specific features: `SELECT FOR UPDATE`
> row-level locks and the `xmin` system column.
>
> An in-memory EF Core provider doesn't implement either of those. If I tested on
> in-memory, the concurrency test would pass even with broken locking code.
> That's a false green — the worst kind of test result.
>
> I used Testcontainers to spin up a real PostgreSQL container for every test run.
> Docker must be running for the tests to work."

---

**"What does the concurrency test actually prove?"**

> "It proves the balance-never-negative guarantee holds under realistic concurrent load.
>
> I fund a wallet for exactly 50 transfers and fire 100 simultaneous requests.
> The test asserts:
> - Exactly 50 requests succeed with 201
> - Exactly 50 requests fail with 422 insufficient funds
> - The source balance is exactly zero
> - The destination balance is exactly the right amount
>
> If the locking was broken, some of the rejected requests would have found sufficient
> funds and succeeded too, driving the source balance negative. The exact assertions
> would fail. This test fails loudly when the concurrency protection is wrong."

---

## AI usage questions

---

**"How did you use AI in this project?"**

> "I used an AI coding assistant throughout the build. I used it to scaffold the solution,
> draft the domain model, generate the EF Core configuration, draft the transfer logic,
> write tests, and create the Docker setup.
>
> It accelerated the boilerplate significantly. But for the critical parts — the locking
> strategy, the idempotency design, the hash-chaining — I reviewed and corrected the
> output because I knew what the correct behaviour should be."

---

**"Did AI make any mistakes?"**

> "Three significant ones that would have been real bugs in production.
>
> The first was using `[property: Required]` on record constructor parameters.
> It compiled and passed static analysis. But ASP.NET Core silently ignores validation
> attributes on record properties — they must be on constructor parameters.
> Every request was crashing with InvalidOperationException at runtime.
> Caught by hitting the live endpoint.
>
> The second, and most serious, was `SELECT * FROM wallets FOR UPDATE`.
> PostgreSQL system columns like `xmin` are not returned by `SELECT *`. EF Core
> was trying to read `xmin` as the concurrency token, found it wasn't there,
> and threw `column n.xmin does not exist`. The row lock still worked, but the
> optimistic-lock backstop — the guard against any unlocked code path — was silently gone.
> Caught by running against the real PostgreSQL container.
>
> The third was the rate limiter partitioned by `Identity.Name`, which is null for JWT
> tokens that carry only a `sub` claim. All users fell into the same IP-based bucket.
> Caught by the concurrency load test — expected 50 successes, got about 15."

---

## "What would you build next?" questions

---

**"What's missing from this implementation?"**

> "The biggest regulatory gap is the daily limit. It's currently a global constant
> — ₦500,000 for everyone. Under the CBN's tiered KYC framework, that limit is
> a function of the customer's KYC tier. Tier 1 (phone only, no BVN) has much lower
> limits than Tier 3 (full BVN/NIN verification). I'd add a `KycTier` field to the
> wallet and resolve the limit from that.
>
> Second missing thing is reversals. If an outbound NIP transfer to another bank fails
> after the money has left, there needs to be a way to return the funds. That's a
> CBN consumer-protection requirement.
>
> Third, the transaction states. Right now a transfer is either complete or not.
> Real NIP transfers go through a `pending → settled/failed` lifecycle. The outbox
> pattern I've built is the right foundation for handling the settlement callback."

---

**"Why did you choose pessimistic locks over optimistic locking?"**

> "Both can be made correct. But under high contention — many transfers hitting the
> same wallet simultaneously, which is realistic for a merchant account — optimistic
> locking generates retry storms. Everyone's transaction fails, everyone retries,
> they all collide again.
>
> Pessimistic locking queues requests cleanly. The first gets the lock, does its work,
> releases it. The second proceeds. Predictable throughput, no storms.
>
> For a financial ledger where correctness matters more than maximum throughput,
> I'd rather queue than retry-storm."

---

**"Why is the daily limit computed from transaction history instead of a counter?"**

> "Two reasons.
>
> First, consistency. The daily spend is a SUM query run under the same row lock that
> protects the balance. The balance check and the limit check are both done under the lock
> so they're always consistent with each other. With a separate counter, there's a risk
> of the counter being out of sync with the transaction history.
>
> Second, simplicity. A counter would need to be reset at midnight. That requires a
> scheduled job — a cron task or a scheduled function. If the job fails to run, the
> counter is never reset and everyone is blocked for the whole next day.
> History-based requires no reset — today's window is just calculated from today's rows."

---

## Questions about .NET and C# specifically

---

**"What is dependency injection?"**

> "Dependency injection is a design pattern where a class receives its dependencies
> from outside rather than creating them itself.
>
> In this project, `LedgerService` doesn't create its own database connection —
> it receives one when it's created. This is registered in `Program.cs`.
>
> The benefit is testability. In tests I can inject a fake database, a fake clock,
> or a fake logger. The class doesn't know or care whether its dependencies are real
> or fake."

---

**"What is Entity Framework Core?"**

> "Entity Framework Core is an ORM — Object-Relational Mapper. It lets you write
> C# code to query and manipulate the database instead of writing raw SQL.
>
> You write `db.Wallets.Where(w => w.CustomerId == 'alice')` and EF Core translates
> that into `SELECT * FROM wallets WHERE CustomerId = 'alice'`.
>
> The exception in this project is the row-locking query in `LockWalletAsync`.
> EF Core doesn't have a built-in way to add `FOR UPDATE` to a query, so I wrote
> the SQL directly with `FromSqlRaw`."

---

**"What is ASP.NET Core?"**

> "ASP.NET Core is Microsoft's framework for building web APIs and web applications.
> It handles the HTTP plumbing — receiving requests, routing them to the right
> controller method, serializing and deserializing JSON, managing authentication
> middleware, and sending responses.
>
> I focused on the business logic and financial safety guarantees. ASP.NET Core
> handles the web layer."
