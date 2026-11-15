using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Nocturne.API.Extensions;

namespace Nocturne.API.Authorization;

/// <summary>
/// Refuses a caller limited to the last 24 hours (<see cref="HttpContextExtensions.IsCallerHistoryClamped"/>).
/// A membership holding <c>tenant.settings</c> is never clamped, but a credential can carry its own
/// limit. This filter keeps such a caller off endpoints whose effect the clamp does not bound.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RefuseHistoryClampedAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.HttpContext.IsCallerHistoryClamped())
            context.Result = new ForbidResult();
    }
}
