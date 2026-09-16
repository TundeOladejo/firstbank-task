using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace NovaWallet.Api.Infrastructure;

/// <summary>
/// An <see cref="IActionResult"/> that writes a <see cref="ProblemDetails"/> via the registered
/// <see cref="IProblemDetailsService"/>. Using the same writer as the global exception handler
/// guarantees model-validation failures come back as <c>application/problem+json</c> with an
/// identical structure, rather than being content-negotiated down to plain <c>application/json</c>.
/// </summary>
public sealed class ProblemResult(ProblemDetails problem) : IActionResult, IStatusCodeActionResult
{
    public int? StatusCode => problem.Status;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        http.Response.StatusCode = problem.Status ?? StatusCodes.Status400BadRequest;

        var service = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        var written = await service.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
        });

        if (!written)
        {
            // Fallback: serialize directly with the correct media type.
            await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        }
    }
}
