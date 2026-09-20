# The Audit Log and the Outbox Pattern

## The Audit Log — Tamper-evident history

### What it is and why it's separate

The project has two ways to look at transaction history:

1. **`transactions` table** — the operational ledger. Shows each wallet's history.
   Used for the `/statement` endpoint. Can be queried, filtered, paginated.

2. **`audit_logs` table** — the regulatory trail. Records every single balance mutation
   in a format that cannot be changed or deleted. Used for the `/audit` endpoint.

The brief specifically requires these to be **separate**. Why?

In a regulated financial institution, you need a record that auditors and regulators
can trust completely. The transactions table serves operational needs (customer
statements). The audit log serves regulatory needs (proof that nothing was tampered with).

If someone with database access wanted to cover up fraud:
- They could delete rows from the transactions table
- They cannot delete audit log rows without breaking the hash chain

---

### How the hash chain works

Each audit log row contains two hashes:

```csharp
public string PreviousHash { get; set; }  // SHA-256 hash of the PREVIOUS row
public string EntryHash { get; set; }     // SHA-256 hash of THIS row's data
```

When a new audit entry is written in `LedgerService.AppendAuditAsync`:

```csharp
// Get the hash of the most recent audit entry for this wallet
var prevHash = await db.AuditLogs
    .Where(a => a.WalletId == walletId)
    .OrderByDescending(a => a.Id)
    .Select(a => a.EntryHash)
    .FirstOrDefaultAsync(ct) ?? new string('0', 64);  // zeros for the first entry

// Build a canonical string from this entry's data
var canonical = $"{walletId}|{action}|{amountKobo}|{before}|{after}|{transferId}|{createdAt:O}|{prevHash}";

db.AuditLogs.Add(new AuditLog
{
    // ...
    PreviousHash = prevHash,
    EntryHash = Hashing.Sha256Hex(canonical)  // SHA-256 of: data + previous hash
});
```

**The chain looks like this:**

```
Entry 1 (CREDIT):
  PreviousHash = 000000000000...  (zeros, no previous)
  EntryHash    = SHA256("wallet|CREDIT|1000000|0|1000000|...|00000...") = "abc123..."

Entry 2 (TRANSFER_OUT):
  PreviousHash = "abc123..."      (hash of Entry 1)
  EntryHash    = SHA256("wallet|TRANSFER_OUT|-400000|1000000|600000|...|abc123...") = "def456..."

Entry 3 (CREDIT again):
  PreviousHash = "def456..."      (hash of Entry 2)
  EntryHash    = SHA256("wallet|CREDIT|500000|600000|1100000|...|def456...") = "ghi789..."
```

**Why tampering is detectable:**

If someone modifies Entry 2 (changes the amount from -400000 to -40000 to hide a fraud):
- Entry 2's `EntryHash` would no longer match the new data
- Entry 3's `PreviousHash` would no longer match Entry 2's `EntryHash`

The chain is broken. An auditor running the verification algorithm would detect it.

**What SHA-256 is:** SHA-256 is a cryptographic hash function. It takes any input
and produces a 64-character hexadecimal string. The same input always produces the
same output. Changing one character of the input produces a completely different output.
It's a one-way function — you can't reverse it.

**What to say:** "The audit log is hash-chained — each row includes the SHA-256 hash
of the previous row's data as part of its own hash. This creates a chain where
tampering with any entry breaks all the entries that follow it. An auditor can verify
the integrity of the entire log by recomputing the hashes."

---

### AppendAuditAsync — What gets recorded

```csharp
private async Task AppendAuditAsync(
    Guid walletId,
    string action,          // "CREDIT", "TRANSFER_OUT", or "TRANSFER_IN"
    long amountKobo,        // the amount (negative for outgoing)
    long before,            // balance BEFORE the mutation
    long after,             // balance AFTER the mutation
    Guid? transferId,       // link to the transfer (if applicable)
    string? correlationId,  // the request tracking ID
    CancellationToken ct)
```

Every time a balance changes:
- A wallet is credited → one audit entry with action "CREDIT"
- A transfer happens → two audit entries: "TRANSFER_OUT" for source, "TRANSFER_IN" for destination

The `balanceBeforeKobo` field is particularly important — it lets you see exactly
what state the wallet was in before the operation, independent of the transactions table.

---

## The Outbox Pattern — Safe event publishing

### What problem it solves

After a transfer completes, you might want to notify other systems:
- The notification service (to send Alice a push notification)
- An analytics system (to record the transaction for fraud detection)
- An event stream (for other services to react to)

The naive approach: after committing the transfer, send a message to the message broker.

**The problem with the naive approach:**

```
1. Transfer commits in database ✓
2. Try to send message to broker
3. ← CRASH or broker is unavailable ←
4. Message never sent
5. Notification service never told about the transfer
```

The transfer happened but the event was never published. Other systems are out of sync.

**The Outbox Pattern solves this:**

Instead of sending the message directly, write it to the database in the same transaction
as the transfer. A background job then reads from the database and sends the messages.

```
1. BEGIN TRANSACTION
2. Transfer commits
3. Outbox message written: {"type": "TransferCompleted", "payload": {...}}
4. COMMIT ← everything succeeds together
5. Background job reads outbox messages
6. Background job publishes to broker
7. Background job marks message as processed
```

Now if step 6 fails, step 5 retries (at-least-once delivery).
If step 4 fails, the outbox message also doesn't exist (rolled back), so nothing
is published for a transfer that didn't happen.

---

### How it's implemented

In `LedgerService.cs`, inside the transfer transaction:
```csharp
db.OutboxMessages.Add(new OutboxMessage
{
    Id = Guid.NewGuid(),
    Type = "TransferCompleted",
    OccurredAt = completedAt,
    Payload = JsonSerializer.Serialize(new
    {
        transferId,
        fromWalletId = from.Id,
        toWalletId = to.Id,
        amountKobo = req.AmountKobo,
        currency = from.Currency,
        completedAt
    })
});
```

In `Infrastructure/OutboxDispatcher.cs`:
```csharp
public class OutboxDispatcher(IServiceProvider services, ILogger<OutboxDispatcher> logger)
    : BackgroundService  // runs continuously in the background
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await DrainAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);  // poll every 2 seconds
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        // Find unprocessed messages
        var batch = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var msg in batch)
        {
            // In this project: just log it (stands in for a real broker)
            logger.LogInformation("Publishing outbox event {Type}", msg.Type);
            msg.ProcessedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);  // mark as processed
    }
}
```

`BackgroundService` is an ASP.NET Core base class for services that run continuously
in the background while the application is running. `ExecuteAsync` is called once
when the application starts and runs until it's told to stop.

**What to say:** "The outbox pattern ensures no event is lost. The `TransferCompleted`
message is written in the same database transaction as the balance mutation. A background
service polls every 2 seconds for unprocessed messages and publishes them. In this
project it logs them — in production it would publish to Kafka or SNS. Because the
message is in the same transaction as the transfer, there's no scenario where money
moves without an event being queued, or an event is queued for a transfer that didn't happen."

---

## `BackgroundService` — Long-running background jobs

```csharp
public class OutboxDispatcher : BackgroundService
```

`BackgroundService` is built into ASP.NET Core. Classes that inherit from it run
continuously in the background as long as the application is running.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)  // loop until shutdown
    {
        await DrainAsync(stoppingToken);
        await Task.Delay(PollInterval, stoppingToken);  // wait 2 seconds
    }
}
```

The `stoppingToken` is cancelled when the application is shutting down, which causes
the `while` loop to exit cleanly.

It's registered in `Program.cs`:
```csharp
builder.Services.AddHostedService<OutboxDispatcher>();
```

This is the same pattern you'd use for:
- Sending scheduled emails
- Processing background jobs
- Monitoring queues
- Any work that needs to happen continuously
