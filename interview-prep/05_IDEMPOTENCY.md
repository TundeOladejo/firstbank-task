# Idempotency — Safe Retries

## What is idempotency?

An operation is **idempotent** if doing it multiple times has the same effect as doing it once.

- Pressing a light switch ON is NOT idempotent — pressing it 3 times gives a different result
- Setting a light switch TO ON is idempotent — pressing it 3 times always leaves it on

In banking: **sending the same transfer request twice must not move money twice.**

---

## Why is this a real problem?

Networks are unreliable. Consider this scenario:

```
1. Alice's app sends: "Transfer ₦8,000 to Bob"
2. The server receives the request, processes it, money moves
3. The server sends back "201 OK" but... the network drops
4. Alice's app never receives the response
5. Alice's app has no idea if the transfer happened
6. It retries: "Transfer ₦8,000 to Bob" again
```

Without idempotency: ₦16,000 leaves Alice's account. She only sent ₦8,000.

With idempotency: The retry is detected as a duplicate and returns the original response.
Alice's money moves only once.

---

## How it works in this project

The client sends an `Idempotency-Key` header with a unique ID (typically a UUID):

```
POST /api/transfers
Idempotency-Key: a1b2c3d4-5678-90ab-cdef-123456789012
Content-Type: application/json

{"fromWalletId": "...", "toWalletId": "...", "amountKobo": 800000}
```

The server's logic in `LedgerService.TransferAsync`:

**Step 1 — Check if this key exists:**
```csharp
var existing = await db.IdempotencyRecords.AsNoTracking()
    .FirstOrDefaultAsync(r => r.Key == idempotencyKey, ct);
if (existing is not null)
    return (ReplayTransfer(existing, requestHash!), replayed: true);
```

If a record exists with this key, return the stored response immediately.
No money moves. The client gets back the exact same response as the first request.

**Step 2 — Process the transfer and save the idempotency record together:**
```csharp
// INSIDE the database transaction (same transaction as the balance change):
db.IdempotencyRecords.Add(new IdempotencyRecord
{
    Key = idempotencyKey,
    RequestHash = requestHash,
    ResponseStatusCode = 201,
    ResponseBody = JsonSerializer.Serialize(response),
    CreatedAt = completedAt
});
```

The idempotency record is saved **in the same database transaction** as the money movement.

---

## Why "same transaction" is the critical design decision

The question the panel will ask: "Why does the idempotency record need to be in the
same transaction?"

Here is the answer:

**If the record is saved AFTER the transfer commits:**

```
1. Transfer commits (money moves)   ← success
2. We try to save idempotency record
3. ← CRASH HERE (server dies, network drops, database blips) ←
4. Retry comes in
5. No idempotency record found
6. Transfer runs AGAIN
7. Money moves TWICE  ❌
```

**If the record is saved IN THE SAME transaction as the transfer:**

```
1. BEGIN TRANSACTION
2. Money moves
3. Idempotency record written
4. COMMIT — both happen at once, or neither does
5. ← if crash happens here, BOTH are rolled back ←
6. Retry comes in
7. If committed: idempotency record found, return stored response ✓
8. If rolled back: no record found, transfer runs fresh, safe ✓
```

**There is no scenario where money moves but the record doesn't exist.**

**What to say:** "The idempotency record is written atomically with the money movement
inside the same database transaction. They commit together or roll back together.
This closes the race condition that exists when the record is saved after the transfer —
that approach has a window where a crash between the two operations would allow a
retry to double-spend."

---

## What happens if two requests race with the same key?

Scenario: Two identical requests with the same idempotency key arrive simultaneously.

```
Request A                          Request B
   |                                   |
Check: key exists? → NO            Check: key exists? → NO
   |                                   |
BEGIN TRANSACTION                  BEGIN TRANSACTION
   |                                   |
Money moves for A                  Money moves for B
   |                                   |
Try to insert idempotency record   Try to insert idempotency record
   |                                   |
Commits successfully ✓             UNIQUE CONSTRAINT VIOLATION
                                   (key already inserted by A)
                                       |
                                   Catch the error, look up A's record
                                       |
                                   Roll back B's transfer entirely
                                       |
                                   Return A's stored response ✓
```

The `UNIQUE` constraint on the `Key` column ensures only one request can insert
a given key. The loser's transfer is rolled back — money only moved once.

```csharp
catch (DbUpdateException) when (idempotencyKey is not null)
{
    var winner = await db.IdempotencyRecords.AsNoTracking()
        .FirstOrDefaultAsync(r => r.Key == idempotencyKey, ct);
    if (winner is null)
        throw; // Not an idempotency race - real error

    await tx.RollbackAsync(ct);
    return (ReplayTransfer(winner, requestHash!), replayed: true);
}
```

---

## Key reuse with a different body — the fraud prevention case

What if someone tries this:
```
First request:  Idempotency-Key: abc123, transfer ₦1,000 to Bob
Second request: Idempotency-Key: abc123, transfer ₦1,000,000 to Charlie
```

They're using the same key but trying to change the transfer details.

The system hashes the request body and stores the hash with the idempotency record:
```csharp
requestHash = Hashing.Sha256Hex(JsonSerializer.Serialize(request));
```

When a replay comes in, it compares the hash of the new request body against the stored hash:
```csharp
if (record.RequestHash != requestHash)
    throw LedgerException.IdempotencyConflict(
        "This Idempotency-Key was already used with a different request payload.");
```

Different body → `409 Conflict` → the attack fails.

**What to say:** "Each idempotency record also stores a SHA-256 hash of the original
request body. If the same key is reused with a different request — trying to
change the destination or amount — the hash won't match and we return 409 Conflict.
This prevents using a previously successful key to authorise a different transfer."

---

## The response header

The controller sets a response header to tell the client what happened:

```csharp
Response.Headers["Idempotent-Replayed"] = replayed ? "true" : "false";
```

The client can check this header:
- `Idempotent-Replayed: false` → this was a fresh transfer
- `Idempotent-Replayed: true` → this was a replay, no money moved again

This is visible in the web console with the toast notification.
