using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Api.Application;
using NovaWallet.Api.Contracts;

namespace NovaWallet.Api.Controllers;

[ApiController]
[Route("api/wallets")]
[Authorize]
[Produces("application/json")]
public class WalletsController(LedgerService ledger) : ControllerBase
{
    /// <summary>List all wallets, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<WalletResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<WalletResponse>>> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => Ok(await ledger.ListWalletsAsync(page, pageSize, ct));

    /// <summary>Create a wallet for a customer. Starting balance is zero.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(WalletResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateWalletRequest request, CancellationToken ct)
    {
        var wallet = await ledger.CreateWalletAsync(request.CustomerId, ct);
        return CreatedAtAction(nameof(GetBalance), new { walletId = wallet.WalletId }, wallet);
    }

    /// <summary>Get the current balance and currency for a wallet.</summary>
    [HttpGet("{walletId:guid}/balance")]
    [ProducesResponseType(typeof(BalanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BalanceResponse>> GetBalance(Guid walletId, CancellationToken ct)
        => Ok(await ledger.GetBalanceAsync(walletId, ct));

    /// <summary>Credit a wallet (simulating an inbound NIP transfer).</summary>
    [HttpPost("{walletId:guid}/credits")]
    [ProducesResponseType(typeof(WalletResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WalletResponse>> Credit(Guid walletId, [FromBody] CreditRequest request, CancellationToken ct)
    {
        var correlationId = HttpContext.TraceIdentifier;
        return Ok(await ledger.CreditAsync(walletId, request.AmountKobo, request.Reference, correlationId, ct));
    }

    /// <summary>Paginated transaction history for a wallet, newest first.</summary>
    [HttpGet("{walletId:guid}/statement")]
    [ProducesResponseType(typeof(PagedResponse<TransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResponse<TransactionResponse>>> Statement(
        Guid walletId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => Ok(await ledger.GetStatementAsync(walletId, page, pageSize, ct));

    /// <summary>Paginated append-only audit trail for a wallet, newest first.</summary>
    [HttpGet("{walletId:guid}/audit")]
    [ProducesResponseType(typeof(PagedResponse<AuditLogResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<AuditLogResponse>>> Audit(
        Guid walletId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => Ok(await ledger.GetAuditLogAsync(walletId, page, pageSize, ct));
}
