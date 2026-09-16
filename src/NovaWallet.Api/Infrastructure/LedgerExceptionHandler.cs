using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NovaWallet.Api.Application;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// Translates domain and infrastructure exceptions into RFC 7807 Problem Details responses via
/// <see cref="ProblemFactory"/>, so controllers stay free of try/catch and clients get one
/// consistent error shape. The mapping is deliberate about which failures are the client's fault
/// (4xx, logged at Warning) versus ours (5xx, logged at Error with the stack trace).
/// </summary>
public class LedgerExceptionHandler(IProblemDetailsService problemDetails, ILogger<LedgerExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // A cancelled request (client disconnected or the request was aborted) is not an error we can
        // report — the socket is already gone. Swallow it quietly without writing a body.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request {Path} was cancelled by the client", context.Request.Path);
            return true;
        }

        var (status, code, detail) = Map(exception);

        if (status >= 500)
            logger.LogError(exception, "Unhandled error {Code} on {Path}", code, context.Request.Path);
        else
            logger.LogWarning("Request {Path} failed {Code}: {Detail}", context.Request.Path, code, detail);

        context.Response.StatusCode = status;
        var problem = ProblemFactory.Create(context, status, code, detail);

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static (int status, string code, string detail) Map(Exception ex) => ex switch
    {
        // Domain failures carry their own status + code.
        LedgerException le => (le.StatusCode, le.ErrorCode, le.Message),

        // Optimistic-concurrency loss (the xmin token changed under us). Safe to retry.
        DbUpdateConcurrencyException => (409, "concurrency_conflict",
            "The resource was modified by another request. Please retry."),

        // Arithmetic guard on the money path (checked() overflow).
        OverflowException => (422, "amount_overflow",
            "The resulting balance would exceed the supported range."),

        // Malformed request body (bad JSON, wrong types). ASP.NET wraps these in BadHttpRequestException.
        BadHttpRequestException or JsonException => (400, "malformed_request",
            "The request body could not be read. Ensure it is valid JSON matching the schema."),

        // Database unreachable / transient connectivity: this is an availability problem, not a bug,
        // so surface 503 (retryable) rather than a generic 500.
        NpgsqlException or DbUpdateException { InnerException: NpgsqlException } => (503, "database_unavailable",
            "The service is temporarily unable to reach its datastore. Please retry shortly."),

        _ => (500, "internal_error", "An unexpected error occurred. The correlation id can be used to trace it."),
    };
}
