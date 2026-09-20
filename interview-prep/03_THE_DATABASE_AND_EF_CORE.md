# The Database and Entity Framework Core

## What database is used?

**PostgreSQL 16** — a powerful, open-source relational database.
It's one of the most popular databases in the world for financial and enterprise systems.

The project uses PostgreSQL specifically because two critical features of this service
rely on Postgres-specific functionality:
1. `SELECT ... FOR UPDATE` — row-level locking (explained in the concurrency file)
2. `xmin` — a system column PostgreSQL adds to every row (also in the concurrency file)

---

## What is Entity Framework Core?

Entity Framework Core (EF Core) is a tool that lets you talk to a database using C#
code instead of writing raw SQL queries.

Without EF Core you'd write:
```sql
SELECT * FROM wallets WHERE "Id" = 'a1b2c3d4-...'
```

With EF Core you write:
```csharp
var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, ct);
```

EF Core translates the C# code into SQL and sends it to the database.

**What to say:** "I used Entity Framework Core as the ORM — Object-Relational Mapper.
It handles translating C# objects to database rows and back. The exception is the
row-locking query in `LockWalletAsync`, where I dropped down to raw SQL because
EF doesn't have a built-in way to add `FOR UPDATE`."

---

## The database tables

The database has 5 tables. Here's what each one stores:

### `wallets` table
```
Id            (UUID)    - unique identifier for the wallet
CustomerId    (text)    - which customer owns this wallet
Currency      (text)    - always "NGN"
BalanceKobo   (bigint)  - current balance in kobo (NEVER negative - DB constraint enforces this)
CreatedAt     (timestamptz)
xmin          (system)  - PostgreSQL's internal version number for the row
```

### `transactions` table
```
Id                 (UUID)
WalletId           (UUID)    - which wallet this transaction is for
Type               (int)     - 1=Credit, 2=TransferOut, 3=TransferIn
AmountKobo         (bigint)  - positive for credits/in, negative for out
BalanceAfterKobo   (bigint)  - what the balance was AFTER this transaction
TransferId         (UUID?)   - links the two legs of a transfer together
CounterpartyWalletId (UUID?) - the other wallet in a transfer
Reference          (text?)   - optional note
CreatedAt          (timestamptz)
```

Every transfer creates **two rows** in this table:
- One `TransferOut` row on Alice's wallet (negative amount)
- One `TransferIn` row on Bob's wallet (positive amount)
Both rows have the same `TransferId` so you can see they're from the same transfer.

### `audit_logs` table
```
Id               (bigint)   - auto-incrementing, always increases
WalletId         (UUID)
Action           (text)     - "CREDIT", "TRANSFER_OUT", or "TRANSFER_IN"
AmountKobo       (bigint)
BalanceBeforeKobo (bigint)  - what the balance was BEFORE
BalanceAfterKobo  (bigint)  - what the balance was AFTER
TransferId       (UUID?)
CorrelationId    (text?)    - the request tracking ID
CreatedAt        (timestamptz)
PreviousHash     (text)     - SHA-256 hash of the PREVIOUS row in this wallet's chain
EntryHash        (text)     - SHA-256 hash of THIS row's data + PreviousHash
```

This is **separate from transactions** on purpose. It's a regulatory-grade audit trail
that cannot be modified or deleted. The hashing makes it tamper-evident.

### `idempotency_records` table
```
Id               (UUID)
Key              (text)    - the client's Idempotency-Key header value (UNIQUE)
RequestHash      (text)    - SHA-256 of the request body (catches reuse with different body)
ResponseStatusCode (int)
ResponseBody     (text)    - the JSON response that was returned originally
CreatedAt        (timestamptz)
```

The `UNIQUE` constraint on `Key` is what makes concurrent replays safe —
only one writer can insert a given key, the second one gets a unique violation.

### `outbox_messages` table
```
Id          (UUID)
Type        (text)     - "TransferCompleted"
Payload     (text)     - JSON with transfer details
OccurredAt  (timestamptz)
ProcessedAt (timestamptz?) - null until the background job processes it
Attempts    (int)
```

---

## LedgerDbContext — The database connection

`Persistence/LedgerDbContext.cs` is the class that represents the database connection.

```csharp
public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    // Each DbSet is a "table accessor"
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    // ...
```

Each `DbSet<T>` is how you access one table. When you write `db.Wallets.FirstOrDefault(...)`,
you're querying the `wallets` table.

The `OnModelCreating` method is where you configure the tables:

```csharp
b.Entity<Wallet>(e =>
{
    e.ToTable("wallets");
    e.HasKey(w => w.Id);  // Id is the primary key
    e.Property(w => w.CustomerId).HasMaxLength(128).IsRequired();
    // ...
    // This is the constraint that prevents negative balances AT THE DATABASE LEVEL:
    e.ToTable(t => t.HasCheckConstraint("ck_wallets_balance_nonnegative", "\"BalanceKobo\" >= 0"));
});
```

**What to say about the CHECK constraint:**
"I added a database-level constraint that prevents the balance from being written as
negative. This is defence in depth — it doesn't trust the application logic.
Even if there were a bug in the application that tried to write a negative balance,
the database would reject it."

---

## Migrations — How the database schema is created

EF Core Migrations are like version control for your database schema.
When you add a new field to a model class, you create a migration — a C# file that
describes how to change the database to match.

The migration file is in `src/NovaWallet.Api/Persistence/Migrations/`.

In `Program.cs`:
```csharp
await db.Database.MigrateAsync();
```

This runs at startup and applies any pending migrations. That's why `docker compose up`
gives you a fully set-up database with no manual steps — the application sets itself up.

**What to say:** "EF Core migrations handle the database schema. I run `MigrateAsync()`
at startup so the database is always in sync with the code. When the panel runs
`docker compose up`, a fresh database is created and the migration is applied automatically."

---

## Transactions — Atomic database operations

A database transaction is a group of operations that either ALL succeed or ALL fail.
There is no partial state.

```csharp
await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

// All of these are inside the transaction:
wallet.BalanceKobo = fromBefore - req.AmountKobo;
AppendTransaction(...);
AppendAuditAsync(...);
db.IdempotencyRecords.Add(...);
db.OutboxMessages.Add(...);

await db.SaveChangesAsync(ct);  // Write everything
await tx.CommitAsync(ct);       // Make it permanent
```

If `SaveChangesAsync` throws (e.g., the database connection drops), the transaction
is automatically rolled back — none of the changes are saved.

`IsolationLevel.ReadCommitted` means: "only read data that has been committed by
other transactions." This prevents reading temporary/partial state from other
concurrent transactions.

**What to say:** "All changes in a transfer are wrapped in a single database transaction.
The balance deduction, the transaction records, the audit entries, the idempotency record,
and the outbox message all commit together. If anything fails, everything rolls back.
There is no partial state — money can't leave Alice without reaching Bob."

---

## AsNoTracking — Reading without overhead

```csharp
var wallet = await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == walletId, ct);
```

By default, EF Core "tracks" every object it reads — it watches for changes so it can
save them later. This uses memory and time.

`AsNoTracking()` says "I'm just reading this, I'm not going to change it."
Used for balance checks and statement queries where we only need to read.

NOT used when we're about to change the balance — we need tracking for updates.

---

## LINQ — Querying in C#

LINQ (Language Integrated Query) is how you filter, sort, and transform data in C#.
You'll see it everywhere:

```csharp
// Get transfers from today for this wallet, ordered newest first
var items = await db.Transactions
    .AsNoTracking()
    .Where(t => t.WalletId == walletId)           // filter
    .OrderByDescending(t => t.CreatedAt)           // sort newest first
    .Skip((page - 1) * pageSize)                   // skip pages
    .Take(pageSize)                                // take this many
    .Select(t => new TransactionResponse(...))      // transform to DTO
    .ToListAsync(ct);                              // execute and get results
```

EF Core translates this entire chain into one SQL query.

**What to say:** "I used LINQ for all database queries. EF Core translates LINQ
to SQL, which means I write type-safe C# code and the ORM handles the SQL generation.
The exception is the row-locking query which needed raw SQL."
