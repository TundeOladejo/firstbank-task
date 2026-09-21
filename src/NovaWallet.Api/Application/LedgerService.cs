using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NovaWallet.Api.Contracts;
using NovaWallet.Api.Domain;
using NovaWallet.Api.Persistence;

namespace NovaWallet.Api.Application;

/// <summary>
/// Core wallet ledger. All money is in kobo (long). The transfer path is concurrency-safe:
/// it takes explicit row locks on both wallets inside a transaction, ordered by id to avoid
/// deadlocks, so no interleaving of concurrent transfers can double-spend or drive a balance
/// negative. A DB check constraint on the balance is the final backstop.
/// </summary>
public class LedgerService(
    LedgerDbContext db,
    IOptions<LedgerOptions> options,
    IClock clock,
    ILogger<LedgerService> logger)
{
    private readonly LedgerOptions _options = options.Value;

    public async Task<WalletResponse> CreateWalletAsync(string customerId, CancellationToken ct)
    {
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Currency = "NGN",
            BalanceKobo = 0,
            CreatedAt = clock.UtcNow
        };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(ct);
        return new WalletResponse(wallet.Id, wallet.CustomerId, wallet.Currency, wallet.BalanceKobo, wallet.CreatedAt);
    }

    public async Task<BalanceResponse> GetBalanceAsync(Guid walletId, CancellationToken ct)
    {
        var wallet = await db.Wallets.AsNoTracking()
            .Where(w => w.Id == walletId)
            .OrderBy(w => w.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw LedgerException.NotFound($"Wallet {walletId} not found.");
        return new BalanceResponse(wallet.Id, wallet.Currency, wallet.BalanceKobo);
    }

    public async Task<PagedResponse<WalletResponse>> ListWalletsAsync(int page, int pageSize, CancellationToken ct)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.Wallets.AsNoTracking();
        var total = await query.LongCountAsync(ct);
        var items = await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new WalletResponse(w.Id, w.CustomerId, w.Currency, w.BalanceKobo, w.CreatedAt))
            .ToListAsync(ct);

        return new PagedResponse<WalletResponse>(items, page, pageSize, total);
    }

    public async Task<WalletResponse> CreditAsync(Guid walletId, long amountKobo, string? reference, string? correlationId, CancellationToken ct)
    {
        if (amountKobo <= 0)
            throw LedgerException.Validation("amountKobo must be a positive integer number of kobo.");

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            var wallet = await LockWalletAsync(walletId, ct)
                ?? throw LedgerException.NotFound($"Wallet {walletId} not found.");

            var before = wallet.BalanceKobo;
            wallet.BalanceKobo = checked(before + amountKobo);

            var transferId = (Guid?)null;
            AppendTransaction(wallet.Id, TransactionType.Credit, amountKobo, wallet.BalanceKobo, transferId, null, reference);
            await AppendAuditAsync(wallet.Id, "CREDIT", amountKobo, before, wallet.BalanceKobo, transferId, correlationId, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new WalletResponse(wallet.Id, wallet.CustomerId, wallet.Currency, wallet.BalanceKobo, wallet.CreatedAt);
        });
    }

    /// <summary>
    /// Executes a transfer. When <paramref name="idempotencyKey"/> is supplied, the idempotency
    /// record is written inside the <b>same</b> DB transaction as the balance mutation. This makes
    /// replay protection atomic with the money movement: a concurrent replay either finds the
    /// committed record (and we return the stored response) or loses the unique-index race and
    /// rolls back its transfer entirely. There is no window in which the transfer commits but the
    /// idempotency record does not.
    /// </summary>
    public async Task<(TransferResponse response, bool replayed)> TransferAsync(
        TransferRequest req, string? idempotencyKey, string? requestHash, string? correlationId, CancellationToken ct)
    {
        if (req.AmountKobo <= 0)
            throw LedgerException.Validation("amountKobo must be a positive integer number of kobo.");
        if (req.FromWalletId == req.ToWalletId)
            throw LedgerException.SameWallet("Source and destination wallets must be different.");

        // Fast replay path outside the transaction.
        if (idempotencyKey is not null)
        {
            var existing = await db.IdempotencyRecords.AsNoTracking()
                .Where(r => r.Key == idempotencyKey)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return (ReplayTransfer(existing, requestHash!), replayed: true);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            // Lock rows in a deterministic order (by Guid) to prevent deadlocks when two
            // transfers touch the same pair of wallets in opposite directions.
            var first = req.FromWalletId.CompareTo(req.ToWalletId) < 0 ? req.FromWalletId : req.ToWalletId;
            var second = first == req.FromWalletId ? req.ToWalletId : req.FromWalletId;

            var w1 = await LockWalletAsync(first, ct);
            var w2 = await LockWalletAsync(second, ct);

            var from = first == req.FromWalletId ? w1 : w2;
            var to = first == req.FromWalletId ? w2 : w1;

            if (from is null) throw LedgerException.NotFound($"Source wallet {req.FromWalletId} not found.");
            if (to is null) throw LedgerException.NotFound($"Destination wallet {req.ToWalletId} not found.");

            if (from.Currency != to.Currency)
                throw LedgerException.CurrencyMismatch("Cross-currency transfers are not supported.");

            // Daily outbound limit (per source wallet), computed over the current WAT day.
            var (dayStartUtc, dayEndUtc) = CurrentLimitWindowUtc();
            var spentToday = await db.Transactions
                .Where(t => t.WalletId == from.Id
                            && t.Type == TransactionType.TransferOut
                            && t.CreatedAt >= dayStartUtc && t.CreatedAt < dayEndUtc)
                .SumAsync(t => (long?)-t.AmountKobo, ct) ?? 0L;

            if (spentToday + req.AmountKobo > _options.DailyOutboundLimitKobo)
                throw LedgerException.DailyLimitExceeded(
                    $"Daily outbound limit of {_options.DailyOutboundLimitKobo} kobo would be exceeded. " +
                    $"Already sent {spentToday} kobo today.");

            if (from.BalanceKobo < req.AmountKobo)
                throw LedgerException.InsufficientFunds(
                    $"Insufficient funds: balance {from.BalanceKobo} kobo, requested {req.AmountKobo} kobo.");

            var transferId = Guid.NewGuid();

            var fromBefore = from.BalanceKobo;
            var toBefore = to.BalanceKobo;
            from.BalanceKobo = fromBefore - req.AmountKobo;
            to.BalanceKobo = checked(toBefore + req.AmountKobo);

            AppendTransaction(from.Id, TransactionType.TransferOut, -req.AmountKobo, from.BalanceKobo, transferId, to.Id, req.Reference);
            AppendTransaction(to.Id, TransactionType.TransferIn, req.AmountKobo, to.BalanceKobo, transferId, from.Id, req.Reference);

            await AppendAuditAsync(from.Id, "TRANSFER_OUT", -req.AmountKobo, fromBefore, from.BalanceKobo, transferId, correlationId, ct);
            await AppendAuditAsync(to.Id, "TRANSFER_IN", req.AmountKobo, toBefore, to.BalanceKobo, transferId, correlationId, ct);

            var completedAt = clock.UtcNow;
            var response = new TransferResponse(transferId, from.Id, to.Id, req.AmountKobo, from.BalanceKobo, completedAt);

            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "TransferCompleted",
                OccurredAt = completedAt,
                Payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    transferId,
                    fromWalletId = from.Id,
                    toWalletId = to.Id,
                    amountKobo = req.AmountKobo,
                    currency = from.Currency,
                    completedAt
                })
            });

            // Persist the idempotency record in the same transaction as the money movement.
            if (idempotencyKey is not null)
            {
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    Key = idempotencyKey,
                    RequestHash = requestHash!,
                    ResponseStatusCode = StatusCodes.Status201Created,
                    ResponseBody = System.Text.Json.JsonSerializer.Serialize(response),
                    CreatedAt = completedAt
                });
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (idempotencyKey is not null)
            {
                // A concurrent request with the same key may have committed first. If so, roll back
                // our transfer and return the winner's stored response — the money moved exactly once.
                var winner = await db.IdempotencyRecords.AsNoTracking()
                    .Where(r => r.Key == idempotencyKey)
                    .OrderBy(r => r.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (winner is null)
                    throw; // Not an idempotency race; surface the original failure.

                await tx.RollbackAsync(ct);
                return (ReplayTransfer(winner, requestHash!), replayed: true);
            }

            await tx.CommitAsync(ct);

            logger.LogInformation("Transfer {TransferId} of {AmountKobo} kobo from {From} to {To} completed",
                transferId, req.AmountKobo, from.Id, to.Id);

            return (response, replayed: false);
        });
    }

    private static TransferResponse ReplayTransfer(IdempotencyRecord record, string requestHash)
    {
        if (record.RequestHash != requestHash)
            throw LedgerException.IdempotencyConflict(
                "This Idempotency-Key was already used with a different request payload.");
        return System.Text.Json.JsonSerializer.Deserialize<TransferResponse>(record.ResponseBody)!;
    }

    public async Task<PagedResponse<TransactionResponse>> GetStatementAsync(Guid walletId, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.Wallets.AnyAsync(w => w.Id == walletId, ct))
            throw LedgerException.NotFound($"Wallet {walletId} not found.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.Transactions.AsNoTracking().Where(t => t.WalletId == walletId);
        var total = await query.LongCountAsync(ct);

        var items = await query
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new TransactionResponse(
                t.Id, t.Type.ToString(), t.AmountKobo, t.BalanceAfterKobo,
                t.CounterpartyWalletId, t.TransferId, t.Reference, t.CreatedAt))
            .ToListAsync(ct);

        return new PagedResponse<TransactionResponse>(items, page, pageSize, total);
    }

    public async Task<PagedResponse<AuditLogResponse>> GetAuditLogAsync(Guid walletId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.AuditLogs.AsNoTracking().Where(a => a.WalletId == walletId);
        var total = await query.LongCountAsync(ct);

        var items = await query
            .OrderByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditLogResponse(
                a.Id, a.WalletId, a.Action, a.AmountKobo, a.BalanceBeforeKobo, a.BalanceAfterKobo,
                a.TransferId, a.CorrelationId, a.CreatedAt, a.PreviousHash, a.EntryHash))
            .ToListAsync(ct);

        return new PagedResponse<AuditLogResponse>(items, page, pageSize, total);
    }

    /// <summary>
    /// Loads a wallet with a row-level write lock (SELECT ... FOR UPDATE). When the provider is
    /// PostgreSQL this serializes concurrent writers on the same row. On non-locking providers
    /// (e.g. the in-memory test provider) it degrades to a tracked read; those tests instead rely
    /// on the xmin optimistic-concurrency token to detect conflicts.
    /// </summary>
    private async Task<Wallet?> LockWalletAsync(Guid walletId, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            // Note: xmin is a system column and is NOT included by "SELECT *", yet EF projects it
            // as the concurrency token — so it must be listed explicitly, or EF emits "column xmin
            // does not exist". Take a row-level write lock with FOR UPDATE.
            return await db.Wallets
                .FromSqlRaw(
                    "SELECT \"Id\", \"CustomerId\", \"Currency\", \"BalanceKobo\", \"CreatedAt\", xmin " +
                    "FROM wallets WHERE \"Id\" = {0} FOR UPDATE", walletId)
                .SingleOrDefaultAsync(ct);
        }
        return await db.Wallets
            .Where(w => w.Id == walletId)
            .OrderBy(w => w.Id)
            .FirstOrDefaultAsync(ct);
    }

    private void AppendTransaction(Guid walletId, TransactionType type, long signedAmount, long balanceAfter, Guid? transferId, Guid? counterparty, string? reference)
    {
        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Type = type,
            AmountKobo = signedAmount,
            BalanceAfterKobo = balanceAfter,
            TransferId = transferId,
            CounterpartyWalletId = counterparty,
            Reference = reference,
            CreatedAt = clock.UtcNow
        });
    }

    private async Task AppendAuditAsync(Guid walletId, string action, long amountKobo, long before, long after, Guid? transferId, string? correlationId, CancellationToken ct)
    {
        // Hash-chain per wallet: link each audit row to the previous one for tamper-evidence.
        var prevHash = await db.AuditLogs
            .Where(a => a.WalletId == walletId)
            .OrderByDescending(a => a.Id)
            .Select(a => a.EntryHash)
            .FirstOrDefaultAsync(ct) ?? new string('0', 64);

        var createdAt = clock.UtcNow;
        var canonical = $"{walletId}|{action}|{amountKobo}|{before}|{after}|{transferId}|{createdAt:O}|{prevHash}";
        db.AuditLogs.Add(new AuditLog
        {
            WalletId = walletId,
            Action = action,
            AmountKobo = amountKobo,
            BalanceBeforeKobo = before,
            BalanceAfterKobo = after,
            TransferId = transferId,
            CorrelationId = correlationId,
            CreatedAt = createdAt,
            PreviousHash = prevHash,
            EntryHash = Hashing.Sha256Hex(canonical)
        });
    }

    /// <summary>Returns the [start, end) UTC bounds of the current daily-limit window (midnight WAT).</summary>
    private (DateTimeOffset startUtc, DateTimeOffset endUtc) CurrentLimitWindowUtc()
    {
        var tz = ResolveTimeZone(_options.DailyLimitTimeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(clock.UtcNow, tz);
        var startLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
        var endLocal = startLocal.AddDays(1);
        return (startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("WAT", TimeSpan.FromHours(1), "WAT", "WAT"); }
    }
}
