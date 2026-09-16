using System.ComponentModel.DataAnnotations;

namespace NovaWallet.Api.Contracts;

public record CreateWalletRequest(
    [Required, StringLength(128, MinimumLength = 1)] string CustomerId);

public record WalletResponse(Guid WalletId, string CustomerId, string Currency, long BalanceKobo, DateTimeOffset CreatedAt);

public record BalanceResponse(Guid WalletId, string Currency, long BalanceKobo);

public record CreditRequest(
    [Range(1, long.MaxValue, ErrorMessage = "amountKobo must be a positive integer number of kobo.")] long AmountKobo,
    string? Reference);

public record TransferRequest(
    [Required] Guid FromWalletId,
    [Required] Guid ToWalletId,
    [Range(1, long.MaxValue, ErrorMessage = "amountKobo must be a positive integer number of kobo.")] long AmountKobo,
    string? Reference);

public record TransferResponse(
    Guid TransferId,
    Guid FromWalletId,
    Guid ToWalletId,
    long AmountKobo,
    long FromBalanceKobo,
    DateTimeOffset CompletedAt);

public record TransactionResponse(
    Guid Id,
    string Type,
    long AmountKobo,
    long BalanceAfterKobo,
    Guid? CounterpartyWalletId,
    Guid? TransferId,
    string? Reference,
    DateTimeOffset CreatedAt);

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

public record AuditLogResponse(
    long Id,
    Guid WalletId,
    string Action,
    long AmountKobo,
    long BalanceBeforeKobo,
    long BalanceAfterKobo,
    Guid? TransferId,
    string? CorrelationId,
    DateTimeOffset CreatedAt,
    string PreviousHash,
    string EntryHash);
