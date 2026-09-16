namespace NovaWallet.Api.Application;

public class LedgerOptions
{
    public const string SectionName = "Ledger";

    /// <summary>Daily outbound transfer limit per wallet, in kobo. Default ₦500,000 = 50,000,000 kobo.</summary>
    public long DailyOutboundLimitKobo { get; set; } = 50_000_000;

    /// <summary>
    /// IANA time zone whose midnight defines the daily-limit window boundary.
    /// West Africa Time (UTC+1) has no DST, so a fixed offset is also safe.
    /// </summary>
    public string DailyLimitTimeZone { get; set; } = "Africa/Lagos";
}
