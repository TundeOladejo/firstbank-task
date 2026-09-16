using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NovaWallet.Api.Application;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// Translates domain and infrastructure exceptions into RFC 7807 Problem Details responses,
/// keeping controllers free of try/catch and giving clients a consistent error shape.
/// </summary>
public class LedgerExceptionHandler(IProblemDetailsService problemDetails, ILogger<LedgerExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, detail) = Map(exception);

        if (status >= 500)
            logger.LogError(exception, "Unhandled error {Code}", code);
        else
            logger.LogWarning("Request failed {Code}: {Detail}", code, detail);

        context.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Type = $"https://novawallet.example/problems/{code}",
            Instance = context.Request.Path
        };
        problem.Extensions["correlationId"] = context.TraceIdentifier;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static (int status, string code, string detail) Map(Exception ex) => ex switch
    {
        LedgerException le => (le.StatusCode, le.ErrorCode, le.Message),
        DbUpdateConcurrencyException => (409, "concurrency_conflict",
            "The wallet was modified by another request. Please retry."),
        OverflowException => (422, "amount_overflow", "The resulting balance would overflow."),
        _ => (500, "internal_error", "An unexpected error occurred.")
    };
}
