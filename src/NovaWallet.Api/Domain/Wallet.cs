namespace NovaWallet.Api.Domain;

/// <summary>
/// A customer wallet. Balance is stored in <b>kobo</b> (integer minor units) to avoid
/// any floating-point drift in the money path. 1 NGN = 100 kobo.
/// </summary>
public class Wallet
{
    public Guid Id { get; set; }

    /// <summary>External customer identifier this wallet belongs to.</summary>
    public string CustomerId { get; set; } = default!;

    /// <summary>ISO-4217 currency code. Fixed to NGN for NovaWallet.</summary>
    public string Currency { get; set; } = "NGN";

    /// <summary>Current balance in kobo. Never negative.</summary>
    public long BalanceKobo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// PostgreSQL system column (xmin) mapped as a concurrency token for optimistic
    /// concurrency control. EF Core throws <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// if the row changed between read and write.
    /// </summary>
    public uint Version { get; set; }
}
