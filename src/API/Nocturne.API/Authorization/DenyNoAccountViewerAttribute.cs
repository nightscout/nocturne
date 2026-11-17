using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Authorization;

/// <summary>
/// Refuses a viewer without an account: a public share link or a guest link.
/// </summary>
/// <remarks>
/// Either may hold <see cref="Scope.AlertsRead"/> so it can see an alarm that is firing now, which
/// <see cref="Controllers.V4.Monitoring.AlertsController.GetActiveAlerts"/> serves. The rest of the
/// alert surface is configuration: rule channels name chat destinations, quiet hours and sounds are
/// the household's, and none of it is what a daycare or a visiting relative was given the link for.
/// Synchronous so the scope tests, which run only synchronous filters, see it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DenyNoAccountViewerAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (Refuses(context.HttpContext))
            context.Result = new ForbidResult();
    }

    /// <summary>Whether the filter refuses the request's caller.</summary>
    public static bool Refuses(HttpContext httpContext)
    {
        // A share principal is the one unauthenticated caller that carries scopes. A caller with no
        // auth context at all is [Authorize]'s to refuse.
        var auth = httpContext.GetAuthContext();
        if (httpContext.IsShareAccess() || auth is { IsAuthenticated: false })
            return true;

        return auth?.AuthType == AuthType.Guest;
    }
}
