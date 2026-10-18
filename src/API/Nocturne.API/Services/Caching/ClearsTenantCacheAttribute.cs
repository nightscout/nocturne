using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Services.Caching;

/// <summary>
/// Clears every <see cref="ITenantCache"/> key of the tenant after a write action succeeds.
/// </summary>
/// <remarks>
/// For the V4 writers whose rows feed several cached families at once (the legacy entry and
/// treatment projections, the statistics, the eHbA1c timeline), the whole tenant is cleared
/// rather than an enumerated list of families that would drift as caches are added. The v1 write
/// paths clear only the family they feed, through <c>WriteEffectOptions.CacheFamiliesToClear</c>
/// and the cache adapters. A refused, failed or cancelled request clears nothing, so a caller who
/// may not write cannot flush the cache.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ClearsTenantCacheAttribute : ActionFilterAttribute
{
    public ClearsTenantCacheAttribute() => Order = int.MaxValue;

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();

        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method)
            || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method))
            return;
        if (executed.Canceled || executed.Exception is not null || !Succeeded(executed.Result))
            return;

        var tenant = http.RequestServices.GetRequiredService<ITenantAccessor>();
        if (tenant.IsResolved)
        {
            await http.RequestServices.GetRequiredService<ITenantCache>()
                .RemoveTenantAsync(tenant.TenantId, http.RequestAborted);
        }
    }

    private static bool Succeeded(IActionResult? result) => result switch
    {
        null or ForbidResult or ChallengeResult => false,
        IStatusCodeActionResult { StatusCode: { } status } => status < 400,
        ObjectResult { Value: ProblemDetails } => false,
        _ => true,
    };
}
