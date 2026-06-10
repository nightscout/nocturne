using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Filters;

/// <summary>
/// Makes shared-cacheable (<c>public</c>) responses on tenant-scoped endpoints vary by the
/// <c>Cookie</c> header.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Multitenancy.TenantResolutionMiddleware"/> resolves the tenant from
/// <c>X-Forwarded-Host</c>, and <c>UseResponseCaching</c> runs after <c>UseForwardedHeaders</c>
/// so its cache key already includes the per-tenant host. This filter adds <c>Vary: Cookie</c>
/// so anonymous, public-access, and authenticated requests resolve to distinct cache entries —
/// without it the shared cache would serve one caller's cached response to another with
/// different credentials (an authenticated read could be returned to an anonymous caller, and
/// vice versa).
/// </para>
/// <para>
/// Only applied when a tenant is resolved and the response is already marked <c>public</c>;
/// it never makes anything more cacheable than the endpoint already declared.
/// </para>
/// </remarks>
public class TenantCacheVaryFilter : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var httpContext = context.HttpContext;

        if (httpContext.Items["TenantContext"] is TenantContext)
        {
            var cacheControl = httpContext.Response.Headers.CacheControl.ToString();
            if (cacheControl.Contains("public", StringComparison.OrdinalIgnoreCase))
            {
                var vary = httpContext.Response.Headers.Vary;
                var alreadyVariesByCookie = vary.Any(v =>
                    v is not null && v.Contains("Cookie", StringComparison.OrdinalIgnoreCase));
                if (!alreadyVariesByCookie)
                {
                    httpContext.Response.Headers.Append(HeaderNames.Vary, "Cookie");
                }
            }
        }

        return next();
    }
}
