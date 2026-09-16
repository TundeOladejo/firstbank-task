using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Persistence;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// Background dispatcher for the transactional outbox. It periodically drains unprocessed
/// messages and "publishes" them (here: logs them, standing in for a real broker like Kafka/SNS).
/// Because outbox rows are written in the same transaction as the balance mutation, delivery is
/// at-least-once with no dual-write inconsistency.
/// </summary>
public class OutboxDispatcher(IServiceProvider services, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        var batch = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var msg in batch)
        {
            // Replace with a real broker publish. Keeping it local keeps the service self-contained.
            logger.LogInformation("Publishing outbox event {Type} {Id}: {Payload}", msg.Type, msg.Id, msg.Payload);
            msg.ProcessedAt = DateTimeOffset.UtcNow;
            msg.Attempts++;
        }

        if (batch.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
