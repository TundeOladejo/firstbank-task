namespace NovaWallet.Api.Domain;

/// <summary>
/// A ledger entry against a single wallet. Each transfer produces two rows
/// (a <see cref="TransactionType.TransferOut"/> and a <see cref="TransactionType.TransferIn"/>)
/// linked by <see cref="TransferId"/>.
/// </summary>
public class Transaction
{
    public Guid Id { get; set; }

    public Guid WalletId { get; set; }

    public TransactionType Type { get; set; }

    /// <summary>Signed amount in kobo relative to the wallet: positive credits, negative debits.</summary>
    public long AmountKobo { get; set; }

    /// <summary>Wallet balance in kobo immediately after this entry was applied.</summary>
    public long BalanceAfterKobo { get; set; }

    /// <summary>Correlates the two legs of a transfer. Null for plain credits.</summary>
    public Guid? TransferId { get; set; }

    /// <summary>The counterparty wallet for transfers. Null for plain credits.</summary>
    public Guid? CounterpartyWalletId { get; set; }

    public string? Reference { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
