using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace Nocturne.API.Services.Notifications.External;

/// <summary>Turns an <see cref="ExternalNotificationRefusal"/> into the problem response a sender gets.</summary>
public static class ExternalNotificationProblems
{
    /// <summary>
    /// The refusal as ProblemDetails with its code in the <c>error</c> extension. A rate limit also
    /// carries <c>Retry-After</c> and a <c>retryAfterSeconds</c> extension.
    /// </summary>
    public static IActionResult ExternalNotificationProblem(this ControllerBase controller, ExternalNotificationRefusal refusal)
    {
        var extensions = new Dictionary<string, object?> { ["error"] = refusal.Error };
        if (refusal.RetryAfter is { } retryAfter)
        {
            var seconds = (long)Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds));
            controller.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            extensions["retryAfterSeconds"] = seconds;
        }

        return controller.Problem(
            detail: refusal.Detail,
            statusCode: refusal.StatusCode,
            title: ReasonPhrase(refusal.StatusCode),
            extensions: extensions);
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        _ => "Error",
    };
}
