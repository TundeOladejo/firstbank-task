using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NovaWallet.Api.Application;
using NovaWallet.Api.Contracts;

namespace NovaWallet.Api.Controllers;

[ApiController]
[Route("api/transfers")]
[Authorize]
[EnableRateLimiting("transfer")]
[Produces("application/json")]
public class TransfersController(LedgerService ledger) : ControllerBase
{
    /// <summary>
    /// Move funds atomically between two wallets. Concurrency-safe and idempotent.
    /// Supply an <c>Idempotency-Key</c> header to make retries safe: replaying the same key
    /// returns the original result, while reusing a key with a different body is rejected (409).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TransferResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Transfer(
        [FromBody] TransferRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        string? requestHash = null;
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (idempotencyKey.Length > 200)
                throw LedgerException.Validation("Idempotency-Key must be at most 200 characters.");
            requestHash = Hashing.Sha256Hex(JsonSerializer.Serialize(request));
        }
        else
        {
            idempotencyKey = null;
        }

        var correlationId = HttpContext.TraceIdentifier;
        var (response, replayed) = await ledger.TransferAsync(request, idempotencyKey, requestHash, correlationId, ct);

        // Replays echo the original 201 with the stored body; a fresh transfer also returns 201.
        Response.Headers["Idempotent-Replayed"] = replayed ? "true" : "false";
        return StatusCode(StatusCodes.Status201Created, response);
    }
}
