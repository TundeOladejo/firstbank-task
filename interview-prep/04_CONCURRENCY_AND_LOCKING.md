# Concurrency and Locking — The Most Important Topic

This is the hardest part of the project and the one the panel will probe most deeply.
Read this file multiple times until you can explain it clearly without notes.

---

## What is concurrency and why is it a problem?

**Concurrency** means multiple things happening at the same time.

A web server handles many requests simultaneously. In a busy banking system, it's
completely normal for hundreds of transfers to be processed at the exact same moment.

Here is the problem. Imagine Alice has ₦10,000 and two requests arrive simultaneously:
- Request A: "Transfer ₦8,000 from Alice to Bob"
- Request B: "Transfer ₦8,000 from Alice to Charlie"

**Without protection, this happens:**

```
Time 1: Request A reads Alice's balance → sees ₦10,000
Time 2: Request B reads Alice's balance → sees ₦10,000
Time 3: Request A checks: 10,000 ≥ 8,000 → yes, proceed
Time 4: Request B checks: 10,000 ≥ 8,000 → yes, proceed
Time 5: Request A subtracts ₦8,000 → writes ₦2,000
Time 6: Request B subtracts ₦8,000 → writes ₦2,000
```

Result: Alice sent ₦16,000 but only had ₦10,000. Balance is ₦2,000 instead of -₦6,000.
Alice effectively created money out of nothing. **This is a double-spend.**

---

## The solution: `SELECT ... FOR UPDATE` (Pessimistic Locking)

The solution is to **lock the row** before reading the balance.
A lock means: "I'm using this row right now. No one else can touch it until I'm done."

In PostgreSQL, `SELECT ... FOR UPDATE` takes a row-level exclusive lock:

```sql
SELECT "Id", "CustomerId", "Currency", "BalanceKobo", "CreatedAt", xmin
FROM wallets WHERE "Id" = 'abc123' FOR UPDATE
```

When Request A runs this query, it gets an exclusive lock on Alice's wallet row.
When Request B tries to run the same query, **it waits**. It cannot proceed until
Request A commits or rolls back its transaction.

**With locking, this happens:**

```
Time 1: Request A locks Alice's row
Time 2: Request B tries to lock Alice's row → WAITS
Time 3: Request A reads balance: ₦10,000
Time 4: Request A checks: 10,000 ≥ 8,000 → yes
Time 5: Request A writes ₦2,000, commits
Time 6: Request A releases the lock
Time 7: Request B gets the lock, reads balance: ₦2,000
Time 8: Request B checks: 2,000 ≥ 8,000 → NO → returns "Insufficient funds"
```

Result: Only ₦8,000 left Alice's account. The second transfer was correctly rejected.

---

## The deadlock problem and why we sort by wallet ID

Imagine two transfers happening simultaneously:
- Transfer A: Alice → Bob (locks Alice first, then Bob)
- Transfer B: Bob → Alice (locks Bob first, then Alice)

```
Time 1: Transfer A locks Alice
Time 2: Transfer B locks Bob
Time 3: Transfer A tries to lock Bob → WAITS for Transfer B
Time 4: Transfer B tries to lock Alice → WAITS for Transfer A
```

**Deadlock!** Both are waiting for each other. Neither can proceed. The database
will eventually detect this and kill one of the transactions, but it's wasteful
and causes errors.

**The solution: always lock wallets in the same order.**

In `LedgerService.cs`:

```csharp
// Always lock the wallet with the LOWER Guid first
var first  = req.FromWalletId.CompareTo(req.ToWalletId) < 0
              ? req.FromWalletId
              : req.ToWalletId;
var second = first == req.FromWalletId ? req.ToWalletId : req.FromWalletId;

var w1 = await LockWalletAsync(first, ct);
var w2 = await LockWalletAsync(second, ct);
```

Now if two transfers involve Alice and Bob:
- Transfer A: Alice → Bob → locks Alice (lower ID) first, then Bob
- Transfer B: Bob → Alice → locks Alice (lower ID) first, then Bob

Both transfers try to lock Alice first. One wins and locks Alice.
The other waits. When the first commits, the second proceeds.
**No deadlock possible.**

**What to say:** "I sort wallet IDs alphabetically and always lock the wallet with
the lower ID first. This means any two transfers involving the same pair of wallets
always try to acquire locks in the same order. That eliminates the possibility of
two transfers deadlocking each other."

---

## `xmin` — The PostgreSQL concurrency token

Every row in PostgreSQL has a hidden system column called `xmin`.
PostgreSQL automatically updates it every time the row is modified.

It's like a version number: row created = version 1, first update = version 2, etc.

EF Core can use `xmin` as an **optimistic concurrency token**:

```csharp
e.Property(w => w.Version)
    .HasColumnName("xmin")
    .HasColumnType("xid")
    .IsConcurrencyToken();
```

When EF Core saves a change, it includes the xmin value in the WHERE clause:

```sql
UPDATE wallets
SET "BalanceKobo" = 1234
WHERE "Id" = 'abc123'
AND xmin = 12345;  -- only update if the row hasn't changed since we read it
```

If another transaction modified the row between when we read it and when we're
trying to save, the xmin will be different and the UPDATE affects 0 rows.
EF Core detects this and throws `DbUpdateConcurrencyException`.

**Why we have BOTH `FOR UPDATE` and `xmin`:**
- `FOR UPDATE` is the primary safety mechanism — it serialises concurrent transfers
- `xmin` is the backstop — it catches any code path that bypasses the explicit lock

**The bug AI introduced (very important for your AI_USAGE story):**

AI initially wrote: `SELECT * FROM wallets ... FOR UPDATE`

`SELECT *` does NOT return system columns like `xmin`. PostgreSQL only returns `xmin`
if you ask for it explicitly. So EF Core tried to project `xmin` from the result set,
found it wasn't there, and threw: `column n.xmin does not exist`.

The fix was to list all columns explicitly:
```sql
SELECT "Id", "CustomerId", "Currency", "BalanceKobo", "CreatedAt", xmin
FROM wallets WHERE "Id" = {0} FOR UPDATE
```

**What to say:** "`xmin` is a PostgreSQL system column that changes every time a row
is updated. I map it as EF Core's concurrency token — it's a backstop that catches
any code path that bypasses the explicit row lock. The AI generated `SELECT *` which
doesn't return system columns, silently removing this safety net. I caught it by
running against a real PostgreSQL instance."

---

## The load test proves it works

In `tests/NovaWallet.Tests/ConcurrencyTests.cs`:

```csharp
// Fund the source for exactly 50 transfers of 1,000 kobo
// Then fire 100 concurrent transfers:
const int concurrent = 100;
const long amount = 1_000;
const int affordable = 50;
await client.CreditAsync(source, amount * affordable);  // fund for exactly 50

var tasks = Enumerable.Range(0, concurrent).Select(async i =>
{
    var c = await factory.CreateAuthenticatedClientAsync($"race-{i}");
    var resp = await c.PostAsJsonAsync("/api/transfers",
        new { fromWalletId = source, toWalletId = sink, amountKobo = amount });
    return resp.StatusCode;
});

var results = await Task.WhenAll(tasks);  // fire all 100 at once

// These assertions must hold:
Assert.Equal(affordable, succeeded);       // exactly 50 succeeded
Assert.Equal(concurrent - affordable, rejected);  // exactly 50 failed
Assert.Equal(0, await client.GetBalanceAsync(source));  // source is exactly 0
Assert.Equal(amount * affordable, await client.GetBalanceAsync(sink));  // sink has exactly the right amount
```

**What to say:** "The test fires 100 concurrent transfers against a wallet that only
has enough for 50. The assertions are exact — not 'about 50' but exactly 50 succeed,
exactly 50 return insufficient funds, the source balance is exactly zero, and the
destination holds exactly the right amount. This passes on every run against real
PostgreSQL. That's the proof."

---

## Pessimistic vs Optimistic locking — a common question

There are two approaches to concurrency:

**Pessimistic (what we use):**
- Lock the row first, then do the work
- Other requests wait while you hold the lock
- Correct under any load, even extreme concurrency
- Slightly slower per request (lock acquisition overhead)

**Optimistic:**
- Read the row, do the work, then try to save
- If the row changed since you read it (xmin is different), retry
- No waiting — but under high contention, many retries happen
- Can cause "retry storms" where everyone keeps retrying simultaneously

**Why pessimistic is right for a financial ledger:**

Under high load with many transfers hitting the same wallet (e.g., a popular merchant
receiving many payments), optimistic locking would cause many retries. Each retry
might collide with another retry. This "retry storm" hammers the database.

Pessimistic locking queues requests cleanly. The first gets the lock, does its work,
releases it. The second gets it, and so on. Predictable throughput, no storms.

**What to say:** "I chose pessimistic locking deliberately. For a financial ledger
where correctness matters more than raw throughput, pessimistic locking is more
predictable under high contention. Optimistic locking would cause retry storms
if many transfers hit the same wallet simultaneously."

---

## Execution Strategy — Automatic retry on transient failures

```csharp
var strategy = db.Database.CreateExecutionStrategy();
return await strategy.ExecuteAsync(async () =>
{
    // ... transfer logic inside here
});
```

When using connection pooling, the database connection can sometimes drop transiently
(network glitch, Postgres restart, etc.). The execution strategy automatically retries
the entire operation in these cases.

We need to wrap the whole transfer in the execution strategy because if it retries,
it must start from the beginning — not from the middle of a partially-done transfer.

**What to say:** "I wrap the transfer in an execution strategy so transient connection
failures are automatically retried. The whole operation retries from scratch — you
can't retry from the middle of a financial transaction."
