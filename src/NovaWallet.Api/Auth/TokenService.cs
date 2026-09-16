using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace NovaWallet.Api.Auth;

/// <summary>
/// Mock token issuer. In production this responsibility belongs to a real identity provider;
/// here it exists only to exercise the bearer middleware and claims handling.
/// </summary>
public class TokenService(IOptions<JwtOptions> options, Application.IClock clock)
{
    private readonly JwtOptions _o = options.Value;

    public (string token, DateTimeOffset expiresAt) Issue(string subject, string? customerId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = clock.UtcNow.AddMinutes(_o.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (!string.IsNullOrWhiteSpace(customerId))
            claims.Add(new Claim("customer_id", customerId));

        var jwt = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            notBefore: clock.UtcNow.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
