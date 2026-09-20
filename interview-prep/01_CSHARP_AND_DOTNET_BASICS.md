# C# and .NET — What You Need to Know

You don't need to know C# deeply. You need to understand the concepts well enough
to talk about them confidently. This file explains every C# feature used in this project
in plain English, with no assumed knowledge.

---

## What is .NET?

.NET is a platform made by Microsoft for building software. Think of it like the
engine of a car — your code is the car body, .NET is the engine that makes it run.

- **.NET 9** is the version used in this project (released late 2024)
- **C#** is the programming language. .NET can run several languages but C# is the main one.
- **ASP.NET Core** is the part of .NET specifically for building web APIs and websites

**What to say:** "I used .NET 9 with ASP.NET Core, which is Microsoft's framework for
building web APIs. It handles HTTP requests, routing, authentication, and middleware —
I focused on the business logic and the financial safety guarantees."

---

## Classes and Objects

A **class** is a blueprint. An **object** is a thing created from that blueprint.

```csharp
// This is a class - a blueprint for a wallet
public class Wallet
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; }
    public long BalanceKobo { get; set; }
}
```

`Guid` is a unique ID — like `"a1b2c3d4-..."`. Every wallet, transaction, and transfer
has one. They're generated automatically and are virtually guaranteed to be unique
even across millions of records.

`public` means other parts of the code can access this.

`{ get; set; }` means this is a property — it can be read and written.

**What to say about this project:** "The Domain folder contains the data models — blueprints
for a Wallet, a Transaction, an AuditLog, etc. They describe what data the system stores."

---

## `long` vs `float` vs `decimal` — Why integers for money

This is one of the most important concepts in the whole project.

| Type | What it is | Problem |
|---|---|---|
| `float` | Approximate decimal number | 0.1 + 0.2 = 0.30000000000000004 |
| `double` | Bigger approximate decimal | Same problem, slightly more precise |
| `decimal` | Precise decimal number | Still has rounding with division |
| `long` | Whole integer number | **No decimals, no rounding, ever** |

In this project, **every amount is stored as `long` kobo**.

- ₦10,000 = 1,000,000 kobo (stored as the integer `1000000`)
- ₦0.01 = 1 kobo (stored as `1`)
- There is no ₦0.005 — you can't have half a kobo

```csharp
public long BalanceKobo { get; set; }  // This is in the Wallet class
```

**Why this matters:** If you stored ₦10,000 as the float `10000.00`, and added many
small amounts together, you could end up with `9999.99999999997` due to floating point
errors. In a bank, that missing fraction is real customer money. Using integers completely
eliminates this class of bug.

**What to say:** "All money is stored as `long` integers in kobo. 1 NGN = 100 kobo.
We never use float or decimal anywhere in the money path. This eliminates floating point
drift entirely — there's no rounding, no fractions, no precision loss."

---

## `async` and `await` — Doing things without waiting

Most operations in this project talk to a database. Talking to a database takes time.
Without `async/await`, the server would sit doing nothing while waiting for the database.

With `async/await`, while one request is waiting for the database, the server can handle
other requests.

```csharp
// Without async - the server is FROZEN while waiting for the database
public WalletResponse GetBalance(Guid walletId)
{
    var wallet = db.Wallets.Find(walletId);  // server does nothing here
    return new WalletResponse(wallet);
}

// With async - the server can handle other requests while waiting
public async Task<WalletResponse> GetBalanceAsync(Guid walletId, CancellationToken ct)
{
    var wallet = await db.Wallets.FindAsync(walletId, ct);  // server is free
    return new WalletResponse(wallet);
}
```

You will see `async`, `await`, and `Task<>` everywhere in this codebase. They all
mean the same thing: "this operation waits for something, but in a smart way."

**CancellationToken** is also always passed around. It's a way to say "if the user
cancels the request (closes their browser), stop what you're doing." This prevents
orphaned database work when requests are abandoned.

---

## `record` — Immutable data containers

You'll see `record` used for the request and response objects in `Contracts/Dtos.cs`:

```csharp
public record TransferRequest(
    Guid FromWalletId,
    Guid ToWalletId,
    long AmountKobo,
    string? Reference);
```

A `record` is like a class but simpler — it's designed to just hold data.
The `?` after `string` means the field can be null (empty/missing).

Records are used for things coming in (requests) and going out (responses) because
they don't need methods — they just hold the data.

---

## Interfaces — Contracts between parts of the code

An interface says "anything that uses me must be able to do these things."

```csharp
// This is an interface - a contract
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

// This is a real implementation
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

In the tests, you can swap in a **fake clock** that returns whatever time you want.
This is called **dependency injection** — instead of hardcoding `DateTime.Now`,
the code asks for an `IClock` and can be given either the real one or a test one.

**Why this matters for the interview:** It shows the code is designed to be testable.
The daily limit window depends on what time it is — by injecting a clock, tests can
set the time to midnight to test the reset behaviour.

---

## Dependency Injection — Giving code what it needs

Instead of a class creating its own dependencies, they are "injected" from outside.

```csharp
// LedgerService doesn't create its own database connection
// It RECEIVES one from outside - this is dependency injection
public class LedgerService(
    LedgerDbContext db,        // given a database connection
    IOptions<LedgerOptions> options,  // given the configuration
    IClock clock,              // given a clock
    ILogger<LedgerService> logger)    // given a logger
{
    // ...
}
```

In `Program.cs`, you register everything:
```csharp
builder.Services.AddScoped<LedgerService>();
builder.Services.AddSingleton<IClock, SystemClock>();
```

`AddScoped` = create one per HTTP request, then throw it away.
`AddSingleton` = create one for the lifetime of the whole application.

**What to say:** "ASP.NET Core's dependency injection container automatically creates
and provides the dependencies each class needs. This makes the code testable —
in tests I can inject a fake database, a fake clock, whatever I need."

---

## `checked()` — Safe arithmetic that catches overflows

```csharp
to.BalanceKobo = checked(toBefore + req.AmountKobo);
```

Normally, if you add two very large integers and the result is too big to fit in a `long`,
it wraps around to a negative number silently. That would be catastrophic in a bank.

`checked()` means: "if this addition overflows, throw an exception instead of wrapping."

**What to say:** "I use `checked()` for all balance arithmetic. If somehow the result
would overflow a 64-bit integer, it throws an exception rather than silently producing
a wrong number. In a financial system, a wrong number is worse than an error."

---

## `Guid` — Unique IDs

```csharp
public Guid Id { get; set; }
```

A Guid (Globally Unique Identifier) looks like: `3fa85f64-5717-4562-b3fc-2c963f66afa6`

Every wallet, transaction, transfer, and audit entry has a Guid ID.
`Guid.NewGuid()` generates a new random one.

They're used instead of sequential numbers (1, 2, 3...) because:
- They're safe to generate in the application, not just the database
- They don't reveal how many records exist
- They're globally unique — two separate systems won't generate the same ID

---

## What you don't need to know

- The syntax details of LINQ (the `.Where()`, `.Select()` etc.) — just know it's a way to query
- The exact attributes like `[HttpPost]`, `[FromBody]` — just know they tell ASP.NET how to
  route requests
- The details of Entity Framework migrations — just know they manage the database schema
