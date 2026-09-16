namespace NovaWallet.Api.Domain;

/// <summary>
/// Append-only, immutable record of every balance mutation. This is intentionally
/// separate from <see cref="Transaction"/> so auditors have an independent trail that
/// is never updated or deleted. Rows are hash-chained for tamper-evidence.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }

    public Guid WalletId { get; set; }

    /// <summary>Action name, e.g. "CREDIT", "TRANSFER_OUT", "TRANSFER_IN".</summary>
    public string Action { get; set; } = default!;

    public long AmountKobo { get; set; }

    public long BalanceBeforeKobo { get; set; }

    public long BalanceAfterKobo { get; set; }

    public Guid? TransferId { get; set; }

    /// <summary>Correlation id of the request that caused this mutation.</summary>
    public string? CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>SHA-256 hash of the previous audit row (tamper-evidence chain).</summary>
    public string PreviousHash { get; set; } = default!;

    /// <summary>SHA-256 hash of this row's canonical contents including <see cref="PreviousHash"/>.</summary>
    public string EntryHash { get; set; } = default!;
}
