using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;

namespace Nocturne.API.RateLimiting;

/// <summary>
/// The response every rate-limit refusal answers with, whether the middleware or a limit applied
/// after authentication (<see cref="DelegatedConnectorSyncLimiter"/>, the authenticator-code bound in
/// <see cref="Services.Auth.StepUpCodeAttempts"/>) refused the request.
/// </summary>
/// <remarks>
/// The body is a problem document, so a generated client that reads a declared <c>ProblemDetails</c> 429
/// throws a body that carries <c>status</c>. <c>code</c> is the house problem code, and <c>error</c> and
/// <c>error_description</c> stay as extensions because OAuth-style clients read them.
/// </remarks>
internal static class RateLimitRejection
{
    private const string Error = "rate_limit_exceeded";
    private const string Description = "Too many requests. Please try again later.";

    /// <param name="httpContext">The refused request.</param>
    /// <param name="retryAfter">When the caller may try again, or null when the limiter does not say.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteAsync(HttpContext httpContext, TimeSpan? retryAfter, CancellationToken ct)
    {
        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (retryAfter is { } wait)
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc6585#section-4",
            Title = "Too Many Requests",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = Description,
            Extensions =
            {
                [ProblemCodeExtensions.CodeExtension] = Error,
                ["error"] = Error,
                ["error_description"] = Description,
            },
        };
        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", ct);
    }
}

/// <summary>An action's rate-limit refusal, answered by <see cref="RateLimitRejection"/>.</summary>
/// <param name="retryAfter">When the caller may try again, or null when the limit does not say.</param>
internal sealed class RateLimitRejectionResult(TimeSpan? retryAfter) : ActionResult
{
    public override Task ExecuteResultAsync(ActionContext context) =>
        RateLimitRejection.WriteAsync(context.HttpContext, retryAfter, context.HttpContext.RequestAborted);
}
