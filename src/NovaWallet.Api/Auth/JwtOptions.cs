namespace NovaWallet.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "novawallet-mock-issuer";
    public string Audience { get; set; } = "novawallet-api";

    /// <summary>
    /// HMAC signing key. MUST be supplied via configuration/secret in any real environment.
    /// A development default is provided only so the service starts out-of-the-box for the panel.
    /// </summary>
    public string SigningKey { get; set; } = "dev-only-super-secret-signing-key-change-me-1234567890";

    public int AccessTokenMinutes { get; set; } = 60;
}
