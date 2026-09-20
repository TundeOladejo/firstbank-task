# How to Present This Project to the Panel

## The most important thing to know upfront

You built this project with AI assistance — and that is completely fine because the brief
explicitly asked for it. What they are testing is NOT whether you wrote every line yourself.
They are testing whether you understand what was built, why it was built that way, and whether
you can defend the decisions. Read this guide carefully and you will be able to do all of that.

---

## What the panel will do

1. Ask you to share your screen
2. Run `docker compose up --build` in the terminal
3. Watch the service start up
4. Walk through the Swagger UI or web console
5. Ask you to explain specific parts of the code
6. Possibly ask you to make a small live code change

You have 10 minutes for the presentation, then a technical Q&A.

---

## Step-by-step: what to do on screen

### Step 1 — Open your terminal and start the service

```bash
cd /Users/babz/Desktop/firstbank-task
docker compose up --build
```

While it starts, say this:

> "The service starts with a single `docker compose up` command as required.
> What you're seeing in the logs is Docker building the .NET 9 container,
> then spinning up PostgreSQL, and then the API connecting to the database
> and applying its migrations automatically. By the time it says 'Now listening',
> the service is fully ready — no manual setup needed."

When you see `Now listening on: http://[::]:8080` — the service is ready.

The log line that says `fail: Microsoft.EntityFrameworkCore` — point to it and say:

> "That `fail` line looks alarming but it's completely expected. Entity Framework checks
> if the migrations table exists when it starts. On a fresh database it doesn't exist yet,
> so the query fails — EF catches that, concludes no migrations have run, and applies them.
> The very next line shows the migration being applied. The service then starts normally."

---

### Step 2 — Open the web console

Open your browser and go to: `http://localhost:8080`

Say:

> "I built a web console that sits on top of the API. It's not a mock — every button
> here calls the real API endpoints. I can use it to demo the full flow, and the panel
> can also test it directly with Swagger or Postman since the API is fully open."

---

### Step 3 — Walk through the demo flow

Do these steps in order. Say the words as you go.

**3a. Sign in**
- Type `demo` in the Subject field, click Sign In
- Say: "The API issues a JWT — a JSON Web Token. Every request after this automatically
  includes it as a header. If I tried to call any wallet endpoint without this token,
  I'd get a 401 Unauthorized response."

**3b. Create two wallets**
- Set Customer ID to `alice`, click Create Wallet. Copy the wallet ID.
- Set Customer ID to `bob`, click Create Wallet. Copy the second wallet ID.
- Say: "Each wallet starts at zero. The currency is fixed to NGN — Nigerian Naira.
  All amounts in this system are stored in kobo, which is 1/100 of a Naira.
  So ₦10,000 is stored as 1,000,000 kobo. This is intentional — it avoids floating
  point precision problems. More on that in a moment."

**3c. Credit Alice's wallet**
- Enter 10000 in the Amount field (that's ₦10,000), click Credit
- Say: "This simulates an inbound NIP transfer — money arriving from the NIBSS rails.
  The balance is now ₦10,000 or 1,000,000 kobo in the database."

**3d. Transfer money**
- Set From to Alice's wallet ID, To to Bob's wallet ID
- Enter 4000 (₦4,000), note the Idempotency Key is already pre-generated
- Click Send Transfer
- Say: "This is the most important part of the system. The transfer is atomic —
  both the debit from Alice and the credit to Bob happen in one database transaction.
  Either both happen or neither does. There is no state where money has left Alice
  but not reached Bob."

**3e. Show the idempotency replay**
- Click Send Transfer AGAIN with the exact same key
- Say: "See the `Idempotent-Replayed: true` indicator. The same key was used,
  same body — so the system recognized this as a retry and returned the original
  result without moving money again. Alice's balance hasn't changed."

**3f. Show the statement and audit trail**
- Click History, enter Alice's wallet ID, click Load
- Say: "This is the transaction statement — newest first, paginated.
  Now let me switch to the Audit Trail tab."
- Switch to Audit Trail
- Say: "This is a separate table from transactions. Every balance mutation is recorded
  here in a way that cannot be changed. Each row is linked to the previous row with
  a SHA-256 hash. If anyone deleted or modified a row, the chain would break.
  This is a tamper-evidence mechanism."

---

### Step 4 — Open Swagger

Go to `http://localhost:8080/swagger`

Say:

> "Swagger shows every endpoint in the API with the full request and response shapes.
> The panel can call any endpoint directly from here. The lock icon shows that most
> endpoints require authentication — you'd paste the token from POST /api/auth/token
> to unlock them."

---

### Step 5 — Walk through the code structure briefly

Open VS Code or your file manager and show the folder structure:

```
src/NovaWallet.Api/
├── Domain/          ← What a wallet IS (the data model)
├── Application/     ← What a wallet DOES (the business logic)
├── Controllers/     ← How the API receives requests (HTTP layer)
├── Persistence/     ← How data is saved to PostgreSQL
├── Auth/            ← JWT token handling
├── Infrastructure/  ← Error handling, logging, background jobs
└── Program.cs       ← The startup file that wires everything together
```

Say:

> "I separated the code into layers. The key design decision is that all money logic
> lives in one place — LedgerService in the Application folder. The controllers
> just receive the HTTP request and pass it to LedgerService. They never touch
> the database directly. This means there is exactly one place where balances change,
> which makes it easy to reason about correctness and easy to test."

---

### Step 6 — Show the tests

Open a second terminal and run:

```bash
cd /Users/babz/Desktop/firstbank-task
DOTNET_ROLL_FORWARD=Major dotnet test tests/NovaWallet.Tests/NovaWallet.Tests.csproj
```

While it runs say:

> "The tests run against a real PostgreSQL database — not a fake in-memory one.
> That's important because the main safety guarantee of this system relies on
> PostgreSQL-specific features: row-level locks and a system column called xmin.
> An in-memory database doesn't support either of those, so testing on in-memory
> would give a false green result on broken code."

When it finishes: 15/15 passed.

---

## The 5 things the panel most wants to hear you say

These are the scored criteria. Make sure each one comes up naturally:

**1. Concurrency safety**
> "The transfer takes a row-level lock on both wallets before checking the balance.
> The locks are always taken in the same order — sorted by wallet ID — to prevent deadlocks.
> The balance check happens inside the lock, so two concurrent transfers cannot both
> see the same balance and both decide to proceed."

**2. Money as integers**
> "All money is stored as a `long` integer in kobo — no float, no decimal, ever.
> 1 NGN = 100 kobo. This eliminates floating point drift entirely. The database also
> has a constraint that rejects any write that would make the balance negative."

**3. Idempotency**
> "The idempotency record is saved in the same database transaction as the money movement.
> They commit together or roll back together. There is no window where the money moved
> but the record wasn't saved, which would cause a retry to double-spend."

**4. Audit trail**
> "The audit log is a separate table that can never be edited — only rows are appended.
> Each row contains a hash of the previous row, forming a chain. If anyone tampers
> with the data, the chain breaks and the tampering is detectable."

**5. The AI bugs**
> "I used AI throughout, but it produced three bugs I had to catch and fix.
> The most serious was a SQL query that used `SELECT *` — PostgreSQL system columns
> like `xmin` are not returned by SELECT *, so the concurrency token was silently missing.
> The service appeared to work but the safety backstop was gone.
> I caught it by running the service against a real database."

---

## How to answer questions you don't know

If they ask something you genuinely don't know, say:

> "I don't have the answer off the top of my head, but I can reason through it.
> In this system I would [explain what you know about the surrounding area]."

Do not guess or make things up. They know you built this with AI assistance.
Honest reasoning is more impressive than a confident wrong answer.

---

## Questions they are most likely to ask

These are covered in detail in the concept files. Know these:

- "Walk me through what happens when a transfer is made"
- "How does the system prevent two requests from spending the same money?"
- "What is `SELECT FOR UPDATE` and why did you use it?"
- "What is idempotency and why does the record need to be in the same transaction?"
- "What is `xmin` and what does it do?"
- "What happens if the database goes down mid-transfer?"
- "Why kobo instead of naira with decimals?"
- "What is a JWT and what does the middleware check?"
- "What is the outbox pattern?"
- "What would you build next?"

---

## The one question that will definitely come up

**"Walk me through what happens step by step when Alice transfers ₦4,000 to Bob."**

Here is the exact answer:

1. The client sends `POST /api/transfers` with `fromWalletId`, `toWalletId`, `amountKobo: 400000`, and an `Idempotency-Key` header
2. The JWT middleware checks the bearer token — if invalid, returns 401 immediately
3. The rate limiter checks this user hasn't exceeded 20 transfers/second
4. The `TransfersController` receives the request, validates the fields, hashes the request body, and calls `LedgerService.TransferAsync`
5. LedgerService checks if this idempotency key was used before — if yes, returns the stored response
6. If not, it opens a database transaction
7. It locks Alice's wallet row and Bob's wallet row using `SELECT FOR UPDATE` — in ascending wallet ID order to prevent deadlocks
8. Under the lock, it re-reads Alice's balance and checks she has enough funds
9. It checks Alice hasn't exceeded her ₦500,000 daily transfer limit
10. It subtracts 400,000 kobo from Alice's balance and adds 400,000 kobo to Bob's balance
11. It writes a `TransferOut` transaction row for Alice and a `TransferIn` row for Bob
12. It writes two audit log entries — one for each wallet — with hash-chained tamper evidence
13. It writes an outbox message: `TransferCompleted`
14. It writes the idempotency record (same transaction — atomic with the money)
15. Everything commits together in one transaction. If anything fails, everything rolls back.
16. The API returns `201 Created` with the transfer details
17. In the background, the OutboxDispatcher picks up the `TransferCompleted` message and publishes it

---

## On interview day checklist

- [ ] Terminal open in `/Users/babz/Desktop/firstbank-task`
- [ ] Docker Desktop running
- [ ] Browser ready to open `http://localhost:8080`
- [ ] Run `docker compose down -v` first to start clean
- [ ] Have the concept files open in another window for reference
- [ ] Know Alice and Bob's wallet IDs during the demo (or just let the UI fill them in)
