namespace NovaWallet.Api.Application;

/// <summary>
/// Domain-level failures that map to a specific HTTP status and RFC 7807 problem type.
/// Throwing these keeps controllers thin and centralizes the HTTP mapping in one exception filter.
/// </summary>
public class LedgerException(int statusCode, string errorCode, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorCode { get; } = errorCode;

    public static LedgerException NotFound(string detail) => new(404, "wallet_not_found", detail);
    public static LedgerException InsufficientFunds(string detail) => new(422, "insufficient_funds", detail);
    public static LedgerException DailyLimitExceeded(string detail) => new(422, "daily_limit_exceeded", detail);
    public static LedgerException Validation(string detail) => new(400, "validation_error", detail);
    public static LedgerException Conflict(string detail) => new(409, "conflict", detail);
    public static LedgerException IdempotencyConflict(string detail) => new(409, "idempotency_key_reused", detail);
}
