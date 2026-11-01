using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Nocturne.API.Services.Analytics;

namespace Nocturne.API.Services.Caching;

/// <summary>
/// Clears the <see cref="TenantCacheFamily"/> families a controller's writes feed, after a write
/// action succeeds.
/// </summary>
/// <remarks>
/// Each controller names every family whose cached value its records can change, except the legacy
/// read families (<see cref="TenantCacheFamily.Entries"/>, <see cref="TenantCacheFamily.Treatments"/>),
/// which the repositories clear on every write through <see cref="LegacyReadCacheInvalidation"/>. The
/// eHbA1c family is keyed by year and its completed years live for weeks, so it is cleared only for
/// the years the write's <see cref="TenantCacheWriteFootprint"/> reaches, or whole when the footprint
/// is empty. A refused, failed or cancelled request clears nothing, so a caller who may not write
/// cannot flush the cache.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ClearsTenantCacheAttribute : ActionFilterAttribute
{
    public ClearsTenantCacheAttribute(params string[] families)
    {
        Families = families;
        Order = int.MaxValue;
    }

    public IReadOnlyList<string> Families { get; }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var footprint = Families.Contains(TenantCacheFamily.EHbA1c) ? TenantCacheWriteFootprint.Open(http) : null;

        var executed = await next();

        if (HttpMethods.IsGet(http.Request.Method)
            || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method))
            return;
        if (executed.Canceled || executed.Exception is not null || !Succeeded(executed.Result))
            return;

        var cache = http.RequestServices.GetRequiredService<ITenantCache>();
        foreach (var family in Families)
        {
            if (family == TenantCacheFamily.EHbA1c)
            {
                foreach (var year in DataOverviewService.EHbA1cCacheFamilies(footprint!.Timestamps))
                    await cache.RemoveFamilyAsync(year, http.RequestAborted);
            }
            else
            {
                await cache.RemoveFamilyAsync(family, http.RequestAborted);
            }
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
