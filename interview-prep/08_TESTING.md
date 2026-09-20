# Testing — How the Tests Work

## Why the tests use real PostgreSQL

This is one of the most important things to understand.

Most tests use an **in-memory database** — a fake database that lives in RAM
and is faster than a real one. For many tests, this is fine.

**For this project, in-memory is NOT acceptable for concurrency tests.**

The two main safety mechanisms in this project are:
1. `SELECT ... FOR UPDATE` — PostgreSQL row-level lock
2. `xmin` — PostgreSQL system column used as a concurrency token

An in-memory EF Core provider:
- Does NOT support `SELECT ... FOR UPDATE` (it just ignores it)
- Does NOT have the `xmin` system column

If the tests ran on in-memory, the concurrency test would still pass even if the
locking code was completely broken. That's a false green — you think your code is safe
when it isn't.

**What to say:** "I used Testcontainers to spin up a real PostgreSQL instance for
the tests. This was non-negotiable because the concurrency guarantees rely on
PostgreSQL-specific features that an in-memory provider doesn't implement.
Testing on in-memory would give a false sense of security."

---

## Testcontainers — Docker for tests

**Testcontainers** is a library that starts Docker containers from within tests.
When the test suite starts, it automatically:
1. Pulls the `postgres:16-alpine` image (if not cached)
2. Starts a PostgreSQL container
3. Runs the migrations to create the schema
4. Runs all the tests
5. Stops and removes the container when done

In `tests/NovaWallet.Tests/PostgresApiFactory.cs`:

```csharp
private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
    .WithImage("postgres:16-alpine")
    .WithDatabase("novawallet")
    .WithUsername("novawallet")
    .WithPassword("novawallet")
    .WithCommand("-c", "max_connections=300")  // allow 300 connections for load tests
    .Build();

public async Task InitializeAsync()
{
    await _db.StartAsync();  // start the container
    using var scope = Services.CreateScope();
    var ctx = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
    await ctx.Database.MigrateAsync();  // create the schema
}
```

---

## WebApplicationFactory — Testing the full stack

`WebApplicationFactory<Program>` starts the entire application in memory
(without a real network port) and gives you an HTTP client to send requests to it.

This means the tests go through the full stack:
- HTTP request received
- JWT middleware validates (or rejects)
- Rate limiter checked
- Controller receives the request
- LedgerService runs the business logic
- Database is updated (real PostgreSQL)
- Response returned

This is an **integration test** — it tests how all the layers work together.

**Why not unit test LedgerService directly?**

You could mock the database and test just `LedgerService` in isolation (unit tests).
But for financial software, integration tests that go through the full stack including
a real database are more valuable. They catch problems that unit tests miss — like
the `xmin` / `SELECT *` bug.

---

## The `IClassFixture<PostgresApiFactory>` pattern

```csharp
public class LedgerIntegrationTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
```

`IClassFixture<T>` means: "create one instance of `PostgresApiFactory` and share it
across all tests in this class." The PostgreSQL container starts once and all tests
in the class use it.

This is more efficient than starting a new container for every test (which would take
a long time), and safer than sharing state between tests (each test creates its own
fresh wallets).

---

## Understanding each test

### `Unauthenticated_request_is_rejected`
```csharp
var client = factory.CreateClient();  // no token!
var resp = await client.PostAsJsonAsync("/api/wallets", new { customerId = "c1" });
Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);  // expect 401
```
Proves: You cannot reach any endpoint without a valid JWT.

---

### `Create_wallet_starts_at_zero_in_ngn`
```csharp
var id = await client.CreateWalletAsync("cust-zero");
var balance = await client.GetFromJsonAsync<BalanceDto>($"/api/wallets/{id}/balance");
Assert.Equal(0, balance!.BalanceKobo);
Assert.Equal("NGN", balance.Currency);
```
Proves: New wallets start with zero balance and NGN currency.

---

### `Credit_then_transfer_moves_funds_exactly`
```csharp
await client.CreditAsync(a, 1_000_000);  // credit 1,000,000 kobo
var resp = await client.PostAsJsonAsync("/api/transfers",
    new { fromWalletId = a, toWalletId = b, amountKobo = 250_000L });
resp.EnsureSuccessStatusCode();
Assert.Equal(750_000, await client.GetBalanceAsync(a));
Assert.Equal(250_000, await client.GetBalanceAsync(b));
```
Proves: Money is conserved. What leaves A arrives at B. No money created or destroyed.

---

### `Transfer_exceeding_balance_returns_422_and_does_not_move_money`
```csharp
await client.CreditAsync(a, 100);
var resp = await client.PostAsJsonAsync("/api/transfers",
    new { fromWalletId = a, toWalletId = b, amountKobo = 101L });
Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
Assert.Equal(100, await client.GetBalanceAsync(a));  // unchanged
Assert.Equal(0, await client.GetBalanceAsync(b));    // unchanged
```
Proves: A rejected transfer doesn't change either balance. Atomicity works.

---

### `Idempotency_replay_does_not_double_process`
```csharp
var key = Guid.NewGuid().ToString();
var body = new { fromWalletId = a, toWalletId = b, amountKobo = 200_000L };

var first = await SendTransfer(client, body, key);
var second = await SendTransfer(client, body, key);  // same key!

first.EnsureSuccessStatusCode();
second.EnsureSuccessStatusCode();
Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
Assert.Equal(300_000, await client.GetBalanceAsync(a));  // only debited once
```
Proves: Replaying the same key doesn't double-debit. The `Idempotent-Replayed` header is set.

---

### `Concurrent_transfers_never_overspend_and_never_go_negative` — THE HEADLINE TEST
```csharp
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

var results = await Task.WhenAll(tasks);  // fire all 100 simultaneously

var succeeded = results.Count(s => s == HttpStatusCode.Created);
var rejected = results.Count(s => s == HttpStatusCode.UnprocessableEntity);

Assert.Equal(affordable, succeeded);           // exactly 50 succeeded
Assert.Equal(concurrent - affordable, rejected); // exactly 50 failed
Assert.Equal(0, await client.GetBalanceAsync(source));  // drained to exactly 0
Assert.Equal(amount * affordable, await client.GetBalanceAsync(sink)); // sink has exact amount
```

**Each concurrent request uses a different `race-{i}` subject so they don't
share a rate-limit bucket** — this was a bug I had to fix after the AI
produced code where all concurrent callers shared the same bucket.

**What to say about this test:** "This is the headline test. I fund a wallet for
exactly 50 transfers and fire 100 simultaneous requests. The test asserts exact
numbers — not 'about 50' but exactly 50 succeed and exactly 50 are rejected.
The source balance must be exactly zero. The sink must hold exactly the right amount.
This fails if the row locking is broken, if there's a race condition in the balance
check, or if the database constraint is missing."

---

## Running the tests

```bash
DOTNET_ROLL_FORWARD=Major dotnet test tests/NovaWallet.Tests/NovaWallet.Tests.csproj
```

`DOTNET_ROLL_FORWARD=Major` is needed because the project targets .NET 9 but this
machine only has the .NET 10 runtime installed. This tells .NET to run .NET 9 code
on the .NET 10 runtime.

The tests take about 60 seconds because:
- Testcontainers needs to start and warm up PostgreSQL
- The concurrency tests fire many concurrent requests

**Expected output:**
```
Passed!  - Failed: 0, Passed: 15, Skipped: 0, Total: 15
```
