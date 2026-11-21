using System.Globalization;

namespace Nocturne.API.RateLimiting;

/// <summary>
/// The response every rate-limit refusal answers with, whether the middleware or a limit applied
/// after authentication (<see cref="DelegatedConnectorSyncLimiter"/>) refused the request.
/// </summary>
internal static class RateLimitRejection
{
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

        await httpContext.Response.WriteAsJsonAsync(
            new
            {
                error = "rate_limit_exceeded",
                error_description = "Too many requests. Please try again later.",
            },
            ct
        );
    }
}
