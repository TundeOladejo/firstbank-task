# Security — JWT, Authentication, and Authorization

## The difference between Authentication and Authorization

**Authentication** = "Who are you?" (verifying identity)
**Authorization** = "Are you allowed to do this?" (checking permissions)

In this project:
- Authentication: "Does this request have a valid JWT token?"
- Authorization: "Is the `[Authorize]` attribute satisfied?"

---

## What is a JWT?

JWT stands for **JSON Web Token**. It's a compact, self-contained way to carry
identity information between a client and a server.

A JWT looks like three Base64-encoded strings separated by dots:
```
eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkZW1vIiwianRpIjoiYWJjMTIzIn0.HMAC_SIGNATURE
```

These three parts are:
1. **Header** — algorithm used (HS256 = HMAC-SHA256)
2. **Payload** — the claims (who you are, when it expires, etc.)
3. **Signature** — a cryptographic signature that proves the token wasn't tampered with

You can decode the first two parts — they're just Base64. The security comes from
the signature, which can only be created by someone who knows the secret signing key.

**What the payload contains in this project:**
```json
{
  "sub": "demo",           // subject - who this token is for
  "jti": "abc123-...",    // JWT ID - unique identifier for this token
  "customer_id": "cust-1", // custom claim added if provided
  "nbf": 1726500000,       // not before - when token becomes valid
  "exp": 1726503600,       // expiry - when token expires (1 hour later)
  "iss": "novawallet-mock-issuer",  // issuer - who issued this token
  "aud": "novawallet-api"  // audience - who this token is for
}
```

---

## How JWT Authentication works in this project

**Step 1 — Client gets a token:**
```
POST /api/auth/token
{"subject": "demo", "customerId": "cust-1"}
```

`TokenService.cs` creates and signs the JWT:
```csharp
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey));
var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
// ... build the token with claims ...
return new JwtSecurityTokenHandler().WriteToken(jwt);
```

The signing key is a secret stored in configuration. The signature is created by
hashing the header + payload with this secret key.

**Step 2 — Client sends the token with every request:**
```
GET /api/wallets/abc123/balance
Authorization: Bearer eyJhbGciOiJIUzI1NiJ9...
```

**Step 3 — The JWT middleware validates the token:**
In `Program.cs`:
```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,          // check the 'iss' claim
            ValidateAudience = true,         // check the 'aud' claim
            ValidateLifetime = true,         // check the 'exp' claim
            ValidateIssuerSigningKey = true, // verify the signature
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)  // allow 30 seconds of clock drift
        };
    });
```

The middleware checks:
- Is the issuer `novawallet-mock-issuer`? ✓
- Is the audience `novawallet-api`? ✓
- Is the token not expired? ✓
- Is the signature valid (was it signed by our key)? ✓

If any check fails → **401 Unauthorized** is returned immediately.
The controller never runs.

**Step 4 — The `[Authorize]` attribute:**
```csharp
[Authorize]  // This controller requires a valid token
public class WalletsController : ControllerBase
```

If the middleware successfully validated the token, the request is "authenticated."
The `[Authorize]` attribute says "only let authenticated requests through."

`AuthController` has `[AllowAnonymous]` — this endpoint doesn't require a token
(because it's the endpoint that issues tokens).

---

## Why this is a mock issuer

The brief says: "a simplified/mock issuer is fine — the point is the middleware
and claims handling, not building a full auth server."

In a real system, tokens would be issued by a dedicated identity provider (like
Auth0, Azure AD, or a custom service). The API would just validate tokens,
not issue them.

In this project, the API both issues and validates tokens. This is fine for the
assessment — what matters is that:
- The JWT middleware is correctly configured
- All endpoints check for a valid token
- Tokens have proper expiry
- The signing key is configurable (not hardcoded)

**What to say:** "This is a mock issuer. In production, a dedicated identity provider
like Azure AD or Auth0 would issue tokens. The API would only validate them. The point
of this implementation is to demonstrate that I understand JWT validation — the middleware
correctly checks issuer, audience, expiry, and signature."

---

## The signing key security

The default signing key in `appsettings.json` is:
```json
"SigningKey": "dev-only-super-secret-signing-key-change-me-1234567890"
```

The name says it all — this is a development default. In the Docker Compose file,
it's passed as an environment variable:
```yaml
Jwt__SigningKey: "dev-only-super-secret-signing-key-change-me-1234567890"
```

In production this would be injected from a secrets manager (AWS Secrets Manager,
Azure Key Vault, etc.) and never committed to source code.

**What to say:** "The signing key is clearly marked as a development default and
is configurable via environment variable. In production it would be injected from
a secrets manager. The important thing is that there's no hardcoded secret in
application code."

---

## Rate Limiting — Throttling the transfer endpoint

Rate limiting prevents a single user from making too many requests too fast.
In this project, it's applied only to `POST /api/transfers`.

In `Program.cs`:
```csharp
options.AddPolicy("transfer", httpContext =>
{
    // Identify the user by their JWT subject claim
    var key = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
              ?? httpContext.User.FindFirst("sub")?.Value
              ?? httpContext.Connection.RemoteIpAddress?.ToString()
              ?? "anonymous";

    return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
    {
        TokenLimit = 20,
        TokensPerPeriod = 20,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true
    });
});
```

**Token bucket algorithm:**
- Each user gets a "bucket" of 20 tokens
- Each transfer request uses one token
- The bucket refills at 20 tokens per second
- If the bucket is empty → `429 Too Many Requests`

**Partitioned per user:** The key is the JWT subject (`sub` claim).
Each user has their own bucket, so one user flooding requests doesn't affect others.

**The bug AI introduced (another good AI_USAGE story):**

AI used `httpContext.User.Identity?.Name`. But `Name` is null for JWT tokens that
only carry a `sub` claim — JWT Bearer maps `sub` to `ClaimTypes.NameIdentifier`,
not to `Identity.Name`. This meant ALL users shared the same IP-based bucket.

This was caught by the concurrency load test: expected 50 successful transfers,
got about 15 because they were all rate-limited as the same "user."

**What to say:** "The rate limiter is partitioned by authenticated user via their
JWT subject claim, mapped to `ClaimTypes.NameIdentifier` by JwtBearer middleware.
The AI initially used `Identity.Name` which is null for JWT sub claims — every
caller fell into the same bucket. I caught it through the concurrency load test
which asserted exact numbers."

---

## RFC 7807 Problem Details — Consistent error responses

This is the standard format for API error responses. Every error in this project
returns this shape:

```json
{
  "type": "https://novawallet.example/problems/insufficient_funds",
  "title": "insufficient_funds",
  "status": 422,
  "detail": "Insufficient funds: balance 50000 kobo, requested 400000 kobo.",
  "instance": "/api/transfers",
  "correlationId": "0HN123ABC:00000001",
  "traceId": "00-abc123...",
  "timestamp": "2026-09-17T10:30:00.000Z"
}
```

- `type` — a URI that identifies the error type (machine-readable)
- `title` — a short error code clients can branch on (e.g., `insufficient_funds`)
- `status` — the HTTP status code
- `detail` — a human-readable description of what went wrong
- `instance` — which URL was being called
- `correlationId` — unique ID for this specific request (for tracing in logs)
- `traceId` — distributed tracing ID (for cross-service correlation)
- `timestamp` — when the error occurred

**`ProblemFactory.cs`** is the single place that creates all error responses.
Whether the error comes from model validation, a domain rule, or an unhandled exception,
it all goes through `ProblemFactory` and comes out with this consistent shape.

**What to say:** "RFC 7807 is the industry standard for API error responses.
Every error in this system goes through a single `ProblemFactory` so clients
always see the same structure. The machine-readable `title` field lets clients
handle specific errors programmatically — a 'daily_limit_exceeded' error can be
handled differently from an 'insufficient_funds' error."

---

## The `CorrelationIdMiddleware`

```csharp
public async Task InvokeAsync(HttpContext context)
{
    var correlationId = context.Request.Headers.TryGetValue("X-Correlation-ID", out var value)
        ? value.ToString()
        : context.TraceIdentifier;

    context.TraceIdentifier = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;

    using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next(context);
    }
}
```

Every request gets a unique ID. This ID is:
1. Echoed back on the response header
2. Included in every log message for this request
3. Included in error responses

This means when a customer reports an error, you can search logs for their
correlation ID and see exactly what happened for that specific request.

**What to say:** "The correlation ID middleware assigns a unique ID to every request.
It honours an inbound `X-Correlation-ID` header so clients can track their own
request IDs through the system. The ID is added to the logging scope so every
log line for that request includes it. When an error occurs, the correlation ID
is in the error response so you can trace exactly what happened."
