namespace NovaWallet.Api.Domain;

/// <summary>
/// Transactional outbox row. Domain events (e.g. TransferCompleted) are written in the
/// same database transaction as the balance mutation, then published asynchronously by a
/// background dispatcher. This guarantees at-least-once delivery without a distributed
/// transaction between the DB and the message broker.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }

    public string Type { get; set; } = default!;

    public string Payload { get; set; } = default!;

    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }
}
