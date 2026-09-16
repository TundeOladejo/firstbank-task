namespace NovaWallet.Api.Domain;

public enum TransactionType
{
    /// <summary>Inbound funds (e.g. NIP credit).</summary>
    Credit = 1,

    /// <summary>Outbound leg of a transfer (money leaving this wallet).</summary>
    TransferOut = 2,

    /// <summary>Inbound leg of a transfer (money arriving at this wallet).</summary>
    TransferIn = 3
}
