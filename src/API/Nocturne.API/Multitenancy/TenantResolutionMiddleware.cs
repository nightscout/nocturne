using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Multitenancy;

/// <summary>
/// Middleware that resolves the current tenant from the request subdomain.
/// Must run before AuthenticationMiddleware in the pipeline.
/// </summary>
public class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantResolutionMiddleware> _logger;
    private readonly MultitenancyConfiguration _config;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public TenantResolutionMiddleware(
        RequestDelegate next,
        ILogger<TenantResolutionMiddleware> logger,
        IOptions<MultitenancyConfiguration> config,
        IMemoryCache cache)
    {
        _next = next;
        _logger = logger;
        _config = config.Value;
        _cache = cache;
    }

    /// <summary>
    /// Paths that operate across all tenants and don't require a resolved tenant context.
    /// These are allowed through even when no matching tenant is found.
    /// </summary>
    private static readonly string[] TenantlessAllowedPaths =
    [
        "/api/v4/me/tenants/validate-slug",
        "/api/admin/tenants/validate-slug",
        "/api/metadata",
        "/api/v4/chat-identity/directory/resolve",
        "/api/v4/chat-identity/directory/pending-links",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantAccessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
        // Check X-Forwarded-Host first (set by reverse proxies), then fall back to Host
        var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(':')[0]
                   ?? context.Request.Host.Host;
        var slug = ExtractSubdomain(host);
        var path = context.Request.Path.Value ?? "";
        var isTenantlessAllowedPath = TenantlessAllowedPaths.Any(
            p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

        // Tenantless-allowed paths on the apex (no slug) operate across tenants
        // and must not fall through to the IsDefault tenant — otherwise any
        // deployment with a default tenant blocks cross-tenant endpoints like
        // /api/v4/chat-identity/directory/pending-links behind TenantSetupMiddleware.
        if (slug == null && isTenantlessAllowedPath)
        {
            await _next(context);
            return;
        }

        var tenantContext = await ResolveTenantAsync(context.RequestServices, slug);

        if (tenantContext == null)
        {
            // Allow tenantless paths through without a resolved tenant
            if (isTenantlessAllowedPath)
            {
                await _next(context);
                return;
            }

            _logger.LogWarning("Tenant not found for slug '{Slug}'", slug);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!tenantContext.IsActive)
        {
            _logger.LogWarning("Tenant '{Slug}' is inactive", slug);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        tenantAccessor.SetTenant(tenantContext);
        context.Items["TenantContext"] = tenantContext;

        await _next(context);
    }

    private string? ExtractSubdomain(string hostname)
    {
        if (string.IsNullOrEmpty(_config.BaseDomain))
            return null;

        // Strip port from BaseDomain for hostname comparison
        // (Host.Host already excludes port, but BaseDomain may include it for frontend URL construction)
        var baseDomainHost = _config.BaseDomain.Split(':')[0];

        if (!hostname.EndsWith($".{baseDomainHost}", StringComparison.OrdinalIgnoreCase))
            return null;

        var subdomain = hostname[..^(baseDomainHost.Length + 1)];
        return string.IsNullOrEmpty(subdomain) ? null : subdomain;
    }

    private async Task<TenantContext?> ResolveTenantAsync(IServiceProvider services, string? slug)
    {
        var cacheKey = $"tenant:{slug ?? "__default__"}";

        if (_cache.TryGetValue(cacheKey, out TenantContext? cached))
            return cached;

        var factory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        var tenant = slug != null
            ? await context.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == slug)
            : await context.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.IsDefault);

        if (tenant == null)
            return null;

        var tenantContext = new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive);
        _cache.Set(cacheKey, tenantContext, CacheDuration);
        return tenantContext;
    }
}
