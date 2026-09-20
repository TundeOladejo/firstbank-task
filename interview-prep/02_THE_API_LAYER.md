# The API Layer — Controllers, Routing, and HTTP

## What an API is

An API (Application Programming Interface) is a service that receives requests over HTTP
and sends back responses. Think of it like a waiter at a restaurant:
- You (the client) tell the waiter what you want (HTTP request)
- The waiter goes to the kitchen (the business logic)
- The kitchen prepares your order (processing)
- The waiter brings your food back (HTTP response)

This project is an HTTP API. The "menu" is defined by the endpoints.

---

## The Endpoints in this project

| Method | URL | What it does |
|---|---|---|
| POST | `/api/auth/token` | Get a JWT token to use the API |
| POST | `/api/wallets` | Create a new wallet |
| GET | `/api/wallets/{id}/balance` | Check a wallet's balance |
| POST | `/api/wallets/{id}/credits` | Add money to a wallet |
| POST | `/api/transfers` | Move money between wallets |
| GET | `/api/wallets/{id}/statement` | Get transaction history |
| GET | `/api/wallets/{id}/audit` | Get the audit trail |
| GET | `/health/live` | Is the service running? |
| GET | `/health/ready` | Is the service ready (DB connected)? |

---

## HTTP Methods — What GET and POST mean

- **GET** — read something, don't change anything (check balance, get statement)
- **POST** — create something or trigger an action (create wallet, send transfer)

**What to say:** "I used POST for operations that change data — creating wallets,
crediting, transferring. I used GET for read-only operations — balance checks,
statements, audit logs. This follows REST conventions."

---

## HTTP Status Codes — The numbers in responses

Every response has a status code that tells the client what happened:

| Code | Meaning | Used when |
|---|---|---|
| 200 OK | Success | Balance check, statement returned |
| 201 Created | Created successfully | Wallet created, transfer completed |
| 400 Bad Request | Client sent bad data | Missing required field, wrong format |
| 401 Unauthorized | No valid token | Forgot the JWT bearer token |
| 404 Not Found | Doesn't exist | Wallet ID doesn't exist |
| 409 Conflict | Conflict | Idempotency key reused with different body |
| 422 Unprocessable Entity | Valid format, but business rule failed | Insufficient funds, daily limit exceeded |
| 429 Too Many Requests | Rate limited | Too many transfers too fast |
| 503 Service Unavailable | Our problem | Database is down |

**What to say:** "I used 422 for business rule failures like insufficient funds —
not 400, because the request was correctly formatted, it just couldn't be processed.
This distinction helps clients handle errors correctly."

---

## Controllers — The HTTP receivers

Controllers in this project live in `src/NovaWallet.Api/Controllers/`.
There are three:

**`WalletsController.cs`** — handles wallet operations
**`TransfersController.cs`** — handles transfers
**`AuthController.cs`** — handles token issuance

```csharp
[ApiController]
[Route("api/wallets")]    // all routes in this controller start with /api/wallets
[Authorize]               // every method requires a valid JWT token
public class WalletsController(LedgerService ledger) : ControllerBase
{
    [HttpPost]            // POST /api/wallets
    public async Task<IActionResult> Create([FromBody] CreateWalletRequest request, CancellationToken ct)
    {
        var wallet = await ledger.CreateWalletAsync(request.CustomerId, ct);
        return CreatedAtAction(nameof(GetBalance), new { walletId = wallet.WalletId }, wallet);
    }
```

**Breaking this down:**
- `[ApiController]` — tells ASP.NET this is an API controller (enables automatic validation)
- `[Route("api/wallets")]` — all routes here start with `/api/wallets`
- `[Authorize]` — every endpoint requires a valid JWT token
- `[HttpPost]` — this method handles POST requests
- `[FromBody]` — read the request data from the HTTP body (as JSON)
- `CancellationToken ct` — allows the request to be cancelled cleanly
- `CreatedAtAction` — returns a 201 status with a link to the created resource

**The controllers are deliberately thin.** They receive the request, pass it to
`LedgerService`, and return the result. No business logic. No database access.
All the important work happens in `LedgerService`.

---

## DTOs — Data Transfer Objects

DTOs (in `Contracts/Dtos.cs`) are the shapes of data coming in and going out.

```csharp
// What the client sends when creating a wallet
public record CreateWalletRequest(
    [Required, StringLength(128, MinimumLength = 1)] string CustomerId);

// What the server sends back
public record WalletResponse(
    Guid WalletId,
    string CustomerId,
    string Currency,
    long BalanceKobo,
    DateTimeOffset CreatedAt);
```

**Why DTOs exist:** The internal `Wallet` domain object has extra fields like `Version`
(the xmin concurrency token) that clients don't need to see. DTOs are a clean contract
between the API and its callers — internal implementation details are hidden.

**The `[Required]` and `[Range]` attributes** are validation rules. If a client sends
a request without `customerId`, ASP.NET automatically rejects it with a 400 error
before the controller even runs.

---

## The `Idempotency-Key` Header

In `TransfersController.cs`:

```csharp
public async Task<IActionResult> Transfer(
    [FromBody] TransferRequest request,
    [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    CancellationToken ct)
```

`[FromHeader]` means "read this from an HTTP header, not the body."

A header is metadata that travels with the request, like:
```
POST /api/transfers
Authorization: Bearer eyJ...
Idempotency-Key: a1b2c3d4-5678-...
Content-Type: application/json

{"fromWalletId": "...", "toWalletId": "...", "amountKobo": 400000}
```

The `?` after `string` means it's optional — you can send a transfer without an
idempotency key, but then it won't be safe to retry.

---

## `Program.cs` — The startup file

`Program.cs` is where the entire application is wired together. It runs once at startup.
Think of it as the master control panel.

It does these things in order:

1. **Registers services** — tells the DI container what classes exist
2. **Configures authentication** — sets up JWT validation rules
3. **Configures error handling** — sets up the RFC 7807 handler
4. **Configures rate limiting** — sets up the transfer throttle
5. **Builds the app**
6. **Runs migrations** — creates/updates the database schema
7. **Adds middleware** — sets up the request processing pipeline
8. **Maps routes** — connects URL patterns to controllers
9. **Starts listening** — begins accepting HTTP requests

**What to say:** "Program.cs is the composition root — the one place where everything
is wired together. I use ASP.NET Core's built-in dependency injection container.
Services are registered here and automatically provided to the classes that need them."

---

## Middleware — The request pipeline

Middleware is a chain of steps every request goes through, in order:

```
Request comes in
     ↓
Exception Handler (catches any error from below)
     ↓
Correlation ID Middleware (assigns a tracking ID to the request)
     ↓
Static Files (serves the web console at /)
     ↓
Swagger UI (serves the API docs)
     ↓
Authentication (validates the JWT token)
     ↓
Authorization (checks [Authorize] attributes)
     ↓
Rate Limiter (checks rate limit)
     ↓
Controller Method (your code runs)
     ↓
Response goes out
```

If any step rejects the request (invalid token, rate limit exceeded, etc.), the
response is sent immediately and the steps below don't run.

**What to say:** "ASP.NET Core has a middleware pipeline. Every request passes through
all the middleware in order. I put the exception handler first so it catches errors
from any step below it. Authentication and authorization run before the controllers
so there's no way to reach business logic without a valid token."

---

## Swagger — The interactive API documentation

Swagger (also called OpenAPI) is an industry-standard way to document APIs.
In this project it's available at `http://localhost:8080/swagger`.

It's automatically generated from the code — the `[ProducesResponseType]` attributes
on the controller methods tell Swagger what responses each endpoint can return.

The JWT bearer security button in Swagger lets the panel paste a token and call
any endpoint directly from the browser.

**What to say:** "Swagger is automatically generated from the code using the Swashbuckle
library. The panel can use it to explore and test every endpoint without needing Postman.
It shows the exact request shapes, response shapes, and possible status codes."
