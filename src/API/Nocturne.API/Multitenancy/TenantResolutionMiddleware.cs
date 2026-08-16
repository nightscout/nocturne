using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
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
    private readonly BaseDomainOptions _config;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public TenantResolutionMiddleware(
        RequestDelegate next,
        ILogger<TenantResolutionMiddleware> logger,
        IOptions<BaseDomainOptions> config,
        IMemoryCache cache)
    {
        _next = next;
        _logger = logger;
        _config = config.Value;
        _cache = cache;
    }

    /// <summary>
    /// A path served without a resolved tenant, optionally narrowed to a single HTTP method.
    /// </summary>
    /// <param name="Path">The path, matched case-insensitively.</param>
    /// <param name="Method">
    /// The only method admitted tenantlessly, or null to admit every method. Naming a method
    /// matters where a path carries both a cross-tenant read and a tenant-affecting write.
    /// </param>
    /// <param name="Prefix">
    /// Whether <paramref name="Path"/> admits everything beneath it. Needed by controllers whose
    /// routes carry an id segment; prefer an exact entry, which cannot admit a route added later.
    /// </param>
    private readonly record struct TenantlessPath(
        string Path, string? Method = null, bool Prefix = false)
    {
        /// <summary>Admit every method on a path, which is the case for most of the list.</summary>
        public static implicit operator TenantlessPath(string path) => new(path);

        public bool Matches(string path, string? method) =>
            (Prefix
                ? path.StartsWith(Path, StringComparison.OrdinalIgnoreCase)
                : Path.Equals(path, StringComparison.OrdinalIgnoreCase)) &&
            (Method is null || method is null ||
             Method.Equals(method, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Paths that operate across all tenants and don't require a resolved tenant context.
    /// These are allowed through even when no matching tenant is found.
    /// </summary>
    private static readonly TenantlessPath[] TenantlessAllowedPaths =
    [
        // Aspire ServiceDefaults health endpoints — must never be tenant-gated;
        // they are used by Kubernetes liveness/readiness probes and external
        // monitoring. Returning 503 on these when no tenant exists causes
        // liveness probes to kill the pod, preventing first-time setup.
        "/health",
        "/alive",
        "/ready",
        "/api/v4/status",
        "/api/v4/me/tenants/validate-slug",
        // Cross-tenant caregiver overview: aggregates across the subject's tenants,
        // so it must be reachable from the apex in multi-tenant deployments. The
        // service pins each tenant itself and never uses the request-scoped context.
        "/api/v4/me/tenants/overview",
        // The subject's tenant list, which drives the tenantless dashboard's navigation
        // and the tenant switcher. Keyed on SubjectId alone, like the overview above.
        // GET only: the same path takes a POST that creates a tenant, and self-service
        // provisioning is a tenant-affecting write with no place on a host that resolves
        // no tenant — in the hosted deployment it goes through billing instead.
        new TenantlessPath("/api/v4/me/tenants", HttpMethods.Get),
        // The caller's own global subject-role scopes. Tenantless, MemberScopeMiddleware
        // returns before applying tenant-derived scopes, so this reports whatever the JWT
        // carries — empty for an ordinary subject, non-empty for one holding global roles.
        // Caller-scoped either way, so nothing about a tenant is exposed.
        "/api/v4/me/permissions",
        // Units, time format, region, colour theme, chart style, language — stored on the subject
        // (subjects.preferences / subjects.preferred_language) and read by SubjectId alone. Not
        // only presentation: the dashboard tiles render glucose in the units held here, so a 404
        // shows an mmol/L user their children's readings in mg/dL.
        "/api/v4/user/preferences",
        "/api/v4/admin/tenants/validate-slug",
        "/api/metadata",
        "/api/v4/chat-identity/directory/resolve",
        "/api/v4/chat-identity/directory/pending-links",
        // OIDC login can be initiated from the apex (no subdomain) — e.g. the
        // platform-access grant bounces an unauthenticated operator here. OIDC is
        // centralized at the apex (the registered redirect_uri is the apex callback),
        // so login must not be tenant-gated. On a subdomain the tenant still resolves
        // normally; this only allows the apex (tenantless) case through.
        "/api/auth/oidc/login",
        // The provider list the login page renders its buttons from. On a tenantless host the
        // identity-provider path is the only one that can complete a sign-in, so a 404 here
        // leaves the page with no sign-in control at all. The data is already served
        // tenantlessly by the allow-listed login above, which reads the same enabled-provider
        // set; the providers themselves are not tenant-scoped. GET is the only verb served.
        new TenantlessPath("/api/auth/oidc/providers", HttpMethods.Get),
        // The OIDC callback is the registered redirect_uri (apex). For apex-initiated
        // logins the state carries no TenantSlug, so OidcCallbackRedirectMiddleware
        // can't bounce it to a subdomain and it must process here. The session it
        // issues is subject-scoped (no tenant needed). Subdomain-originated callbacks
        // are already redirected to their subdomain before reaching this point.
        "/api/auth/oidc/callback",
        // Session introspection, called on every page load — including on the tenantless
        // dashboard host, which would otherwise 404 before rendering anything. The session
        // it reports is subject-scoped; the only tenant-dependent field (member roles) is
        // already skipped when no tenant is resolved.
        "/api/auth/oidc/session",
        // Sign-out from the tenantless dashboard. Revokes the refresh token and clears the
        // session cookies, neither of which is tenant-scoped.
        "/api/auth/oidc/logout",
        // Session refresh, driven by the client's expiry timer on every host — including the
        // tenantless dashboard, where a 404 would flip the shell to a signed-out UI while the
        // server-side session is still valid. Subject-scoped like the two above: it validates
        // the refresh token, reads global subject roles, and mints an access token with no
        // tenant pin, so it can confer no tenant-scoped authority. POST only, matching the
        // only verb the endpoint serves.
        new TenantlessPath("/api/auth/oidc/refresh", HttpMethods.Post),
        // The subject's own sign-in factors and linked identities, as /settings/account manages
        // them. Each is keyed on the caller's SubjectId, and the tables behind them
        // (passkey_credentials, recovery_codes, totp_credentials, subject_oidc_identities,
        // subject_avatars) carry no tenant column — one person's credential is the same credential
        // in every tenant they belong to, so managing it from a tenant subdomain was arbitrary.
        //
        // The sign-in ceremonies are deliberately absent. TotpController.Login gates on membership
        // of the resolved tenant and the passkey login/* paths likewise, so authenticating on a
        // tenantless host remains identity-provider-only; these enrol and revoke factors for a
        // caller who is already authenticated.
        new TenantlessPath("/api/auth/passkey/register/options", HttpMethods.Post),
        new TenantlessPath("/api/auth/passkey/register/complete", HttpMethods.Post),
        new TenantlessPath("/api/auth/passkey/recovery/status", HttpMethods.Get),
        new TenantlessPath("/api/auth/passkey/recovery/regenerate", HttpMethods.Post),
        new TenantlessPath("/api/auth/totp/setup", HttpMethods.Post),
        new TenantlessPath("/api/auth/totp/verify-setup", HttpMethods.Post),
        new TenantlessPath("/api/auth/totp", HttpMethods.Get),
        // Revoking one authenticator, whose route carries its id. Narrowed to DELETE so the sibling
        // /login, which gates on membership of the resolved tenant, is not admitted under the POST
        // it is served on. Asked without a method this prefix answers for its own DELETE, so a
        // coverage sweep sees /login as reachable; routing has no DELETE there to reach.
        new TenantlessPath("/api/auth/totp/", HttpMethods.Delete, Prefix: true),
        // GET the list and DELETE one by id; nothing else is routed beneath either.
        new TenantlessPath("/api/auth/passkey/credentials", Prefix: true),
        new TenantlessPath("/api/auth/oidc/link/identities", Prefix: true),
        // Linking an identity is a full-page navigation, so a 404 loses the page rather than
        // failing one control. It reads its tenant slug as a nullable off HttpContext.Items purely
        // to route the callback home, which on a tenantless host is where the caller already is —
        // the same shape as the login/callback pair above.
        new TenantlessPath("/api/auth/oidc/link", HttpMethods.Get),
        new TenantlessPath("/api/auth/oidc/link/callback", HttpMethods.Get),
        // GET is [AllowAnonymous] and serves any subject's picture by id on every tenant host
        // already; it is admitted here because the page renders through it.
        "/api/v4/me/avatar",

        // Cross-tenant by design: these operate on arbitrary tenants by id and so cannot rely on
        // subdomain resolution at all.
        //
        // Platform-admin tenant-access grant: minted at the apex (the operator is not on any
        // tenant subdomain yet); the target tenant is resolved from the query string.
        new TenantlessPath("/api/auth/platform-access", Prefix: true),
        new TenantlessPath("/api/v4/admin/demo/", Prefix: true),
        new TenantlessPath("/api/v4/admin/platform-settings", Prefix: true),
        new TenantlessPath("/api/v4/admin/tenants", Prefix: true),
        new TenantlessPath("/api/v4/dev-only/", Prefix: true),
        new TenantlessPath("/api/v4/platform/", Prefix: true),
        new TenantlessPath("/api/v4/setup/", Prefix: true),
        // Cross-tenant overview hub: authorizes a subject in-band and joins per-tenant groups
        // itself, so the connection is negotiated from the apex with no tenant. Prefix, not
        // exact path: SignalR appends /negotiate to the hub path.
        new TenantlessPath("/hubs/overview", Prefix: true),
    ];

    /// <summary>
    /// Whether a request is served without a resolved tenant. Public so the authorization
    /// guard tests can enumerate the same surface rather than restating these lists.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="method">
    /// The request method. Omit it to ask whether the path is reachable tenantlessly under any
    /// method, which is what a coverage sweep over the whole surface wants.
    /// </param>
    public static bool IsTenantlessAllowed(string path, string? method = null) =>
        TenantlessAllowedPaths.Any(p => p.Matches(path, method));

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantAccessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
        // Check X-Forwarded-Host first (set by reverse proxies), then fall back to Host
        var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(':')[0]
                   ?? context.Request.Host.Host;
        var slug = SubdomainParser.Extract(host, _config.BaseDomain);

        // Public share link: {token}.share.{baseDomain}. Resolve the tenant by its share token
        // and mark the request read-only-public. An unknown token returns the same 404 as an
        // unknown slug, so the share host can't be used as a tenant-existence oracle.
        if (slug != null && SubdomainParser.TryExtractShareToken(slug, out var shareToken))
        {
            var shareCache = context.RequestServices.GetRequiredService<ShareTokenCacheService>();
            var shareTenant = await shareCache.ResolveByTokenAsync(shareToken);

            if (shareTenant == null)
            {
                _logger.LogDebug("Share token did not resolve to a tenant");
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!shareTenant.IsActive)
            {
                _logger.LogWarning("Share token resolved to inactive tenant '{Slug}'", shareTenant.Slug);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            tenantAccessor.SetTenant(shareTenant);
            context.Items["TenantContext"] = shareTenant;
            context.Items["ShareAccess"] = true;
            // Mark the share before pinning the scoped context so the carrier is in place
            // for both the scoped-direct and the factory DbContext paths.
            context.RequestServices.GetRequiredService<ICategoryReadContext>().MarkShare();
            PinTenantOnScopedDbContext(context, shareTenant.TenantId);
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var isTenantlessAllowedPath = IsTenantlessAllowed(path, context.Request.Method);

        // On the apex (no subdomain), GET /api/v4/status is tenant-scoped yet listed as
        // tenantless-allowed (so a fresh apex doesn't 404). On a single-tenant install,
        // resolve the sole tenant so status reflects it instead of reporting
        // "setup_required" — which would bounce a fully configured single-tenant install
        // to /setup. Falls through to the normal tenantless passthrough when zero or
        // multiple tenants exist, so multi-tenant apex behavior is unchanged.
        if (slug == null && path.Equals("/api/v4/status", StringComparison.OrdinalIgnoreCase))
        {
            var soleStatusTenant = await GetSoleTenantAsync(context.RequestServices);
            if (soleStatusTenant != null)
            {
                tenantAccessor.SetTenant(soleStatusTenant);
                context.Items["TenantContext"] = soleStatusTenant;
                PinTenantOnScopedDbContext(context, soleStatusTenant.TenantId);
                await _next(context);
                return;
            }
        }

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
            PinTenantOnScopedDbContext(context, soleTenant.TenantId);
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
        PinTenantOnScopedDbContext(context, tenantContext.TenantId);

        await _next(context);
    }

    /// <summary>
    /// Pins the resolved tenant onto the request-scoped <see cref="NocturneDbContext"/>.
    /// The scoped context's <c>TenantId</c> defaults to <c>Guid.Empty</c>, so without this a
    /// directly-injected context has no tenant set. The <c>TenantConnectionInterceptor</c>
    /// reads <c>TenantId</c> to scope Row-Level Security on connection open, so any
    /// directly-injected context (e.g. connector-configuration reads) would otherwise run under
    /// an empty tenant — most visibly on unauthenticated flows (setup/onboarding) that have no
    /// auth handler to set it.
    /// </summary>
    private static void PinTenantOnScopedDbContext(HttpContext context, Guid tenantId)
    {
        var db = context.RequestServices.GetService<NocturneDbContext>();
        if (db is null)
            return;
        db.TenantId = tenantId;
        // Set the share carrier unconditionally so a directly-injected context never carries
        // unintended share state. The scoped-direct context carries only the marker (known
        // pre-auth) and leaves the CSV null, so a share reading PHI on this path is denied.
        db.IsShareContext = context.RequestServices.GetService<ICategoryReadContext>()?.IsShare == true;
        db.VisibleCategories = null;
        db.ShareFullHistory = false;
    }


    /// <summary>Cache key holding the resolved <see cref="TenantContext"/> for a slug.</summary>
    public static string TenantCacheKey(string slug) => $"tenant:{slug}";

    /// <summary>Cache key holding the sole-tenant context used to resolve the apex.</summary>
    public const string SoleTenantCacheKey = "tenant:__sole__";

    /// <summary>
    /// Drops the cached <see cref="TenantContext"/> for a tenant, so the next request rebuilds it
    /// from the row.
    /// </summary>
    /// <remarks>
    /// Call after writing any column the context carries. <see cref="TenantContext.IsDemo"/> in
    /// particular gates <c>GET /api/v4/demo/session</c> and the <c>isDemo</c> field on
    /// <c>/api/v4/status</c>, so without this a tenant stays non-demo for
    /// <see cref="CacheDuration"/> after being flagged — a login page with no passkey and no
    /// working demo sign-in.
    /// <para>
    /// Both keys go: the apex resolves single-tenant installs through
    /// <see cref="SoleTenantCacheKey"/>, which holds a copy of the same context.
    /// </para>
    /// </remarks>
    public static void EvictTenant(IMemoryCache cache, string slug)
    {
        cache.Remove(TenantCacheKey(slug));
        cache.Remove(SoleTenantCacheKey);
    }

    /// <summary>
    /// Resolves a tenant by subdomain slug.
    /// </summary>
    private async Task<TenantContext?> ResolveTenantBySlugAsync(IServiceProvider services, string slug)
    {
        var cacheKey = TenantCacheKey(slug);

        if (_cache.TryGetValue(cacheKey, out TenantContext? cached))
            return cached;

        var factory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        var tenant = await context.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug);

        if (tenant == null)
            return null;

        var tenantContext = new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo);
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
        var cacheKey = SoleTenantCacheKey;

        if (_cache.TryGetValue(cacheKey, out TenantContext? cached))
            return cached;

        var factory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        var tenants = await context.Tenants.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Id)
            .Take(2)
            .ToListAsync();

        if (tenants.Count != 1)
            return null;

        var tenant = tenants[0];
        var tenantContext = new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo);
        _cache.Set(cacheKey, tenantContext, CacheDuration);
        return tenantContext;
    }
}
