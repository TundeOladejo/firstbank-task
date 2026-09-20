# Docker and Deployment

## What is Docker?

Docker is a tool that packages an application with everything it needs to run —
the code, the runtime, the libraries, the configuration — into a single unit called
a **container**.

Think of it like a shipping container: the same container works on any ship (any machine)
regardless of what else is on that ship. "Works on my machine" becomes "works everywhere."

---

## What is `docker compose`?

`docker compose` is a tool for running multiple Docker containers together.
This project needs two things running:
1. The .NET API
2. PostgreSQL

`docker compose up` starts both with a single command.

---

## The `docker-compose.yml` file

```yaml
services:
  db:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: novawallet
      POSTGRES_USER: novawallet
      POSTGRES_PASSWORD: novawallet
    ports:
      - "5432:5432"
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U novawallet -d novawallet"]
      interval: 5s
      timeout: 3s
      retries: 10

  api:
    build:
      context: .
      dockerfile: src/NovaWallet.Api/Dockerfile
    depends_on:
      db:
        condition: service_healthy  # wait until PostgreSQL is ready
    environment:
      ConnectionStrings__Postgres: "Host=db;Port=5432;..."
      Jwt__SigningKey: "dev-only-..."
    ports:
      - "8080:8080"
```

**Key points:**
- `depends_on: db: condition: service_healthy` — the API only starts after
  PostgreSQL passes its health check. This prevents the API from crashing
  because the database isn't ready yet.
- `Host=db` — inside Docker, services talk to each other by their service name.
  `db` is the name of the PostgreSQL service.
- The `__` in environment variable names is ASP.NET Core's way of representing
  nested configuration. `ConnectionStrings__Postgres` means `ConnectionStrings.Postgres`.

---

## The `Dockerfile` — Building the API container

```dockerfile
# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY NovaWallet.slnx ./
COPY global.json ./
COPY src/NovaWallet.Api/NovaWallet.Api.csproj src/NovaWallet.Api/
RUN dotnet restore src/NovaWallet.Api/NovaWallet.Api.csproj

COPY src/ src/
RUN dotnet publish src/NovaWallet.Api/NovaWallet.Api.csproj -c Release -o /app/publish

# Stage 2: Runtime (smaller image)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
RUN adduser --disabled-password appuser
USER appuser  # run as non-root
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NovaWallet.Api.dll"]
```

**Why two stages?**
- The SDK image is large (~750MB) — it has everything needed to compile .NET code
- The Runtime image is smaller (~200MB) — it only has what's needed to RUN .NET code
- The build stage compiles the code, the runtime stage just runs the compiled output
- Result: the deployed container is small and doesn't include compiler tools

**Why `USER appuser`?**
Running as root inside a container is a security risk — if the container is compromised,
the attacker has root access to the container. Running as a non-root user limits the damage.

**What to say:** "I use a multi-stage Dockerfile. The first stage uses the full .NET SDK
to compile the application. The second stage uses only the smaller ASP.NET runtime image
and copies just the compiled output. The container runs as a non-root user for security.
The result is a production-ready container image."

---

## How the startup works end to end

When you run `docker compose up --build`:

1. **Docker builds the API container**
   - Downloads `mcr.microsoft.com/dotnet/sdk:9.0` (if not cached)
   - Compiles the .NET code
   - Creates the runtime container

2. **Docker starts the `db` container**
   - PostgreSQL starts up
   - Docker's healthcheck runs `pg_isready` every 5 seconds
   - Once PostgreSQL accepts connections → marked as "healthy"

3. **Docker starts the `api` container** (only after `db` is healthy)
   - .NET application starts
   - Reads environment variables for configuration
   - Connects to PostgreSQL
   - Runs `MigrateAsync()` — creates tables, indexes, constraints

4. **The log line: `fail: ... SELECT FROM "__EFMigrationsHistory"`**
   - This is EF Core checking if the migrations table exists
   - On a fresh database, it doesn't → EF creates it
   - This "fail" is expected and handled internally

5. **`Applying migration '20260916054327_InitialCreate'`**
   - Creates all 5 tables with the right structure
   - Adds all indexes
   - Adds the `CHECK (BalanceKobo >= 0)` constraint

6. **`Now listening on: http://[::]:8080`**
   - The service is ready
   - Open `http://localhost:8080` to access the web console
   - Open `http://localhost:8080/swagger` for the API docs

---

## Health checks — Is the service alive?

Two endpoints:

**`GET /health/live`** — "Is the process running?"
Returns 200 if the .NET application is up and running.
Kubernetes calls this. If it fails, Kubernetes restarts the container.

**`GET /health/ready`** — "Is the service ready to handle requests?"
Returns 200 if the .NET application is running AND can reach the database.
If the database is temporarily unavailable, this returns 503.
Kubernetes stops sending traffic to the container while this fails.

In `Program.cs`:
```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<LedgerDbContext>("database");
// ...
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
```

**What to say:** "I have two health endpoints. `/health/live` confirms the process is
running. `/health/ready` confirms it can reach the database. In a Kubernetes deployment,
liveness failures cause the pod to restart, while readiness failures just remove it
from the load balancer until it recovers."

---

## The `.dockerignore` file

Just like `.gitignore`, `.dockerignore` tells Docker what to exclude when building:

```
**/bin/
**/obj/
tests/
.git/
```

This prevents compiled output and test code from being copied into the container.
Makes the build faster and the image smaller.

---

## What `.slnx` is

`NovaWallet.slnx` is a Visual Studio solution file in the newer XML format.
It groups the API project and the test project together so you can build everything
with one command:

```bash
dotnet build NovaWallet.slnx
```

And run all tests with:
```bash
dotnet test NovaWallet.slnx
```
