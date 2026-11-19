using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Nocturne.API.Services.AppBuilds;

namespace Nocturne.API.Controllers.V4.AppBuilds;

/// <summary>
/// Turns an authorization filter's 401 or 403 on the GitHub callback into a return to the web page
/// with <c>error=signed_out</c> or <c>error=forbidden</c>. The browser arrives there by a top-level
/// redirect from GitHub, so a bare status would strand it on an API response.
/// </summary>
/// <remarks>
/// An <see cref="IAlwaysRunResultFilter"/> is the only result filter that sees a result an
/// authorization filter short-circuited with.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RedirectRefusedCallbackAttribute : Attribute, IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var code = context.Result switch
        {
            UnauthorizedResult or ChallengeResult => AppBuildException.SignedOut,
            ForbidResult => AppBuildException.Forbidden,
            IStatusCodeActionResult { StatusCode: StatusCodes.Status401Unauthorized } => AppBuildException.SignedOut,
            IStatusCodeActionResult { StatusCode: StatusCodes.Status403Forbidden } => AppBuildException.Forbidden,
            _ => null,
        };
        if (code is not null)
            context.Result = AppBuildsController.ErrorRedirect(code);
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
