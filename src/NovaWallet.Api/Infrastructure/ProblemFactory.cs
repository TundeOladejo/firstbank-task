using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// Single source of truth for the shape of every error the API returns. Whether an error comes from
/// model validation, a thrown domain exception, or an unhandled fault, it is built here so clients
/// always see the same RFC 7807 structure with the same extension fields (error code, correlation id,
/// trace id, timestamp). This consistency is what lets a caller reliably branch on <c>title</c>.
/// </summary>
public static class ProblemFactory
{
    public const string TypeBase = "https://novawallet.example/problems/";

    private static readonly IReadOnlyDictionary<int, string> DefaultTitles = new Dictionary<int, string>
    {
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [403] = "Forbidden",
        [404] = "Not Found",
        [409] = "Conflict",
        [422] = "Unprocessable Entity",
        [429] = "Too Many Requests",
        [500] = "Internal Server Error",
        [503] = "Service Unavailable",
    };

    /// <summary>Builds a problem for a single error code (domain or infrastructure failure).</summary>
    public static ProblemDetails Create(HttpContext ctx, int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Type = TypeBase + code,
            Instance = ctx.Request.Path,
        };
        Stamp(problem, ctx);
        return problem;
    }

    /// <summary>Builds a problem for model-validation failures, including a field-level error map.</summary>
    public static ValidationProblemDetails CreateValidation(HttpContext ctx, ModelStateDictionary modelState)
    {
        var errors = modelState
            .Where(kvp => kvp.Value is { Errors.Count: > 0 })
            .ToDictionary(
                kvp => NormalizeField(kvp.Key),
                kvp => kvp.Value!.Errors.Select(e =>
                    string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage).ToArray());

        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "validation_error",
            Detail = "One or more fields failed validation.",
            Type = TypeBase + "validation_error",
            Instance = ctx.Request.Path,
        };
        Stamp(problem, ctx);
        return problem;
    }

    private static void Stamp(ProblemDetails problem, HttpContext ctx)
    {
        // correlationId is the business-facing id (also echoed on the X-Correlation-ID header);
        // traceId ties the response to distributed tracing spans; timestamp aids log correlation.
        problem.Extensions["correlationId"] = ctx.TraceIdentifier;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier;
        problem.Extensions["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
    }

    // Lower-camel the field name so it matches the JSON the client actually sent.
    private static string NormalizeField(string key)
    {
        if (string.IsNullOrEmpty(key)) return "$";
        return char.ToLowerInvariant(key[0]) + key[1..];
    }

    public static string TitleFor(int status) =>
        DefaultTitles.TryGetValue(status, out var t) ? t : "Error";
}
