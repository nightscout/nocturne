using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Multitenancy;

/// <summary>
/// Middleware that resolves the current tenant from the request.
/// Tenants are resolved by subdomain: <c>{slug}.{BaseDomain}</c>.
/// Requests on the apex domain (no subdomain) are either tenantless-allowed
/// cross-tenant paths or 404/503 depending on whether any tenants exist.
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
        "/api/v4/admin/tenants/validate-slug",
        "/api/metadata",
        "/api/v4/chat-identity/directory/resolve",
        "/api/v4/chat-identity/directory/pending-links",
    ];

    /// <summary>
    /// Prefixes that are cross-tenant by design and must never be gated on
    /// a resolved tenant. Admin tenant management (create, provision, member
    /// management) operates on arbitrary tenants by ID and cannot rely on
    /// subdomain resolution.
    /// </summary>
    private static readonly string[] TenantlessAllowedPrefixes =
    [
        "/api/auth/passkey/setup/",
        "/api/v4/admin/tenants",
        "/api/v4/dev-only/",
        "/api/v4/platform/",
        "/api/v4/setup/",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantAccessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
        // Check X-Forwarded-Host first (set by reverse proxies), then fall back to Host
        var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(':')[0]
                   ?? context.Request.Host.Host;
        var slug = ExtractSubdomain(host);
        var path = context.Request.Path.Value ?? "";
        var isTenantlessAllowedPath =
            TenantlessAllowedPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)) ||
            TenantlessAllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        // Tenantless-allowed paths on the apex (no slug) operate across tenants.
        if (slug == null && isTenantlessAllowedPath)
        {
            await _next(context);
            return;
        }

        // Apex domain (no subdomain) with a non-tenantless path.
        // If no tenants exist yet, return 503 setup_required so the
        // frontend redirects to /setup instead of showing a 404.
        // If exactly one tenant exists, auto-resolve to it (single-tenant mode).
        if (slug == null)
        {
            var soleTenant = await GetSoleTenantAsync(context.RequestServices);
            if (soleTenant == null)
            {
                var anyTenantExists = await AnyTenantExistsAsync(context.RequestServices);
                if (!anyTenantExists)
                {
                    _logger.LogInformation("No tenants exist — returning 503 setup_required");
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "setup_required",
                        setupRequired = true,
                    });
                    return;
                }

                // Multiple tenants but no subdomain — can't determine which one.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Single tenant: auto-resolve from the apex domain.
            tenantAccessor.SetTenant(soleTenant);
            context.Items["TenantContext"] = soleTenant;
            await _next(context);
            return;
        }

        // Subdomain present: resolve tenant by slug
        var tenantContext = await ResolveTenantBySlugAsync(context.RequestServices, slug);

        if (tenantContext == null)
        {
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
        // Strip port from BaseDomain for hostname comparison
        // (Host.Host already excludes port, but BaseDomain may include it for frontend URL construction)
        var baseDomainHost = _config.BaseDomain.Split(':')[0];

        if (!hostname.EndsWith($".{baseDomainHost}", StringComparison.OrdinalIgnoreCase))
            return null;

        var subdomain = hostname[..^(baseDomainHost.Length + 1)];
        return string.IsNullOrEmpty(subdomain) ? null : subdomain;
    }

    /// <summary>
    /// Resolves a tenant by subdomain slug.
    /// </summary>
    private async Task<TenantContext?> ResolveTenantBySlugAsync(IServiceProvider services, string slug)
    {
        var cacheKey = $"tenant:{slug}";

        if (_cache.TryGetValue(cacheKey, out TenantContext? cached))
            return cached;

        var factory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        var tenant = await context.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug);

        if (tenant == null)
            return null;

        var tenantContext = new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive);
        _cache.Set(cacheKey, tenantContext, CacheDuration);
        return tenantContext;
    }

    /// <summary>
    /// Checks whether any tenant exists at all (used to distinguish "no tenants
    /// yet" from "tenant not found" on the apex domain).
    /// </summary>
    private async Task<bool> AnyTenantExistsAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        return await context.Tenants.AsNoTracking().AnyAsync();
    }

    /// <summary>
    /// Returns the sole active tenant if exactly one exists, enabling single-tenant
    /// mode where the apex domain auto-resolves without a subdomain.
    /// Returns null when zero or multiple tenants exist.
    /// </summary>
    private async Task<TenantContext?> GetSoleTenantAsync(IServiceProvider services)
    {
        var cacheKey = "tenant:__sole__";

        if (_cache.TryGetValue(cacheKey, out TenantContext? cached))
            return cached;

        var factory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        var tenants = await context.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .Take(2)
            .ToListAsync();

        if (tenants.Count != 1)
            return null;

        var tenant = tenants[0];
        var tenantContext = new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive);
        _cache.Set(cacheKey, tenantContext, CacheDuration);
        return tenantContext;
    }
}
