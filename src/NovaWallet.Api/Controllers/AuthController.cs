using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Api.Auth;

namespace NovaWallet.Api.Controllers;

public record TokenRequest(
    [Required] string Subject,
    string? CustomerId);

public record TokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);

/// <summary>
/// Mock authentication endpoint. Issues a signed JWT so callers can exercise the protected
/// endpoints. This stands in for a real IdP and is intentionally unauthenticated.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController(TokenService tokens) : ControllerBase
{
    /// <summary>Issue a mock bearer token. POST the subject (and optional customer id).</summary>
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    public IActionResult Token([FromBody] TokenRequest request)
    {
        var (token, expiresAt) = tokens.Issue(request.Subject, request.CustomerId);
        return Ok(new TokenResponse(token, "Bearer", expiresAt));
    }
}
