using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Extensions;

namespace Nocturne.API.Multitenancy;

/// <summary>
/// Middleware that resolves the current tenant from the request.
/// Tenants are resolved by subdomain: <c>{slug}.{BaseDomain}</c>.
/// Requests on the apex domain (no subdomain) are either tenantless-allowed
/// cross-tenant paths or 404/503 depending on whether any tenants exist.
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
    /// <param name="SoleTenantAtApex">
    /// Whether the apex of a single-tenant install serves this path as that tenant, exactly as
    /// <c>{slug}.{BaseDomain}</c> would. Set on paths scoped to the caller, which already run on
    /// every tenant host; leave it off for cross-tenant and platform paths, and for any that
    /// carries the resolved slug into an OIDC <c>state</c>, since
    /// <see cref="Middleware.ApexCallbackRelayMiddleware"/> would then bounce the callback to a
    /// subdomain the install may not serve.
    /// </param>
    public readonly record struct TenantlessPath(
        string Path, string? Method = null, bool Prefix = false, bool SoleTenantAtApex = false)
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
    /// The operator's own support and billing links, read from configuration alone. Named once
    /// because it is on both lists below and a typo would silently split them.
    /// </summary>
    private static readonly TenantlessPath SupportConfigPath =
        new("/api/v4/support/config", HttpMethods.Get);

    /// <summary>
    /// Paths that operate across all tenants and don't require a resolved tenant context.
    /// These are allowed through even when no matching tenant is found.
    /// </summary>
    private static readonly TenantlessPath[] TenantlessAllowedPaths =
    [
        // The two paths MapDefaultEndpoints maps, and the only two the deployment probes.
        // Returning 503 on these when no tenant exists causes liveness probes to kill the pod,
        // preventing first-time setup.
        "/health",
        "/alive",
        // Answers for the tenant: tenantless only so a fresh apex reports setup_required instead
        // of a 404, and served as the sole tenant so a configured install is not sent to /setup.
        new TenantlessPath("/api/v4/status", SoleTenantAtApex: true),
        "/api/v4/me/tenants/validate-slug",
        // Cross-tenant caregiver overview: aggregates across the subject's tenants,
        // so it must be reachable from the apex in multi-tenant deployments. The
        // service pins each tenant itself and never reads as the request's tenant.
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
        new TenantlessPath("/api/v4/me/permissions", SoleTenantAtApex: true),
        // Units, time format, region, colour theme, chart style, language — stored on the subject
        // (subjects.preferences / subjects.preferred_language) and read by SubjectId alone. Not
        // only presentation: the dashboard tiles render glucose in the units held here, so a 404
        // shows an mmol/L user their children's readings in mg/dL.
        new TenantlessPath("/api/v4/user/preferences", SoleTenantAtApex: true),
        // A host that resolves no tenant still needs somewhere to send the visitor.
        SupportConfigPath,
        // Another instance's server relaying a support issue to the apex it was configured with
        // (GitHubIssueOptions.RelayUrl). The issue goes to GitHub, not to any tenant here.
        new TenantlessPath("/api/v4/support/relay", HttpMethods.Post),
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
        // logins the state carries no TenantSlug, so ApexCallbackRelayMiddleware
        // can't bounce it to a subdomain and it must process here. The session it
        // issues is subject-scoped (no tenant needed). Subdomain-originated callbacks
        // are already redirected to their subdomain before reaching this point.
        "/api/auth/oidc/callback",
        // Session introspection, called on every page load — including on the tenantless
        // dashboard host, which would otherwise 404 before rendering anything. A member session
        // is subject-scoped apart from its member roles, which need a tenant; a guest session
        // belongs to one tenant outright, and GuestSessionHandler refuses it on a host with none.
        new TenantlessPath("/api/auth/oidc/session", SoleTenantAtApex: true),
        // Sign-out from the tenantless dashboard. Revokes the refresh token and clears the
        // session cookies, neither of which is tenant-scoped.
        new TenantlessPath("/api/auth/oidc/logout", SoleTenantAtApex: true),
        // Session refresh, driven by the client's expiry timer on every host — including the
        // tenantless dashboard, where a 404 would flip the shell to a signed-out UI while the
        // server-side session is still valid. Subject-scoped like the two above: it validates
        // the refresh token, reads global subject roles, and mints an access token with no
        // tenant pin, so it can confer no tenant-scoped authority. POST only, matching the
        // only verb the endpoint serves.
        new TenantlessPath("/api/auth/oidc/refresh", HttpMethods.Post, SoleTenantAtApex: true),
        // The subject's own sign-in factors and linked identities, as /settings/account manages
        // them. Each is keyed on the caller's SubjectId, and the tables behind them
        // (passkey_credentials, recovery_codes, totp_credentials, subject_oidc_identities,
        // subject_avatars) carry no tenant column — one person's credential is the same credential
        // in every tenant they belong to, so managing it from a tenant subdomain was arbitrary.
        //
        // The passkey sign-in ceremonies are deliberately absent: they gate on membership of the
        // resolved tenant, so a primary factor on a tenantless host is identity-provider-only.
        // These enrol and revoke factors for a caller who is already authenticated.
        new TenantlessPath("/api/auth/passkey/register/options", HttpMethods.Post, SoleTenantAtApex: true),
        new TenantlessPath("/api/auth/passkey/register/complete", HttpMethods.Post, SoleTenantAtApex: true),
        new TenantlessPath("/api/auth/passkey/recovery/status", HttpMethods.Get, SoleTenantAtApex: true),
        new TenantlessPath("/api/auth/passkey/recovery/regenerate", HttpMethods.Post, SoleTenantAtApex: true),
        new TenantlessPath("/api/auth/totp/setup", HttpMethods.Post, SoleTenantAtApex: true),
        new TenantlessPath("/api/auth/totp/verify-setup", HttpMethods.Post, SoleTenantAtApex: true),
        new TenantlessPath("/api/auth/totp", HttpMethods.Get, SoleTenantAtApex: true),
        // The authenticator step after an identity-provider sign-in on a tenantless host. Served
        // as the sole tenant like the passkey ceremonies it also follows; TotpController.Login
        // matches a token the tenantless OIDC callback minted at that same apex.
        new TenantlessPath("/api/auth/totp/login", HttpMethods.Post, SoleTenantAtApex: true),
        // Revoking one authenticator, whose route carries its id. Narrowed to DELETE so the sibling
        // /login is admitted only by its own entry above. Routing has no DELETE beneath /login.
        new TenantlessPath("/api/auth/totp/", HttpMethods.Delete, Prefix: true, SoleTenantAtApex: true),
        // GET the list and DELETE one by id; nothing else is routed beneath either.
        new TenantlessPath("/api/auth/passkey/credentials", Prefix: true, SoleTenantAtApex: true),
        new TenantlessPath("/api/auth/oidc/link/identities", Prefix: true, SoleTenantAtApex: true),
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
    /// The slice of <see cref="TenantlessAllowedPaths"/> still served when the resolved tenant is
    /// inactive. None of these reads tenant data.
    /// </summary>
    /// <remarks>
    /// <c>/api/v4/status</c> is deliberately not here: it answers for the tenant, and a 200 there
    /// would have the web shell render the app over an API refusing every read.
    /// </remarks>
    private static readonly TenantlessPath[] InactiveTenantAllowedPaths =
    [
        "/health",
        "/alive",
        SupportConfigPath,
    ];

    /// <summary>
    /// The machine-readable code an inactive tenant's refusal carries, alongside
    /// <c>setup_required</c> on the fresh-install 503.
    /// </summary>
    public const string TenantInactiveCode = "tenant_inactive";

    /// <summary>
    /// The entries themselves, for the guard that asserts each one still names a routed endpoint.
    /// </summary>
    public static IReadOnlyList<TenantlessPath> TenantlessPaths => TenantlessAllowedPaths;

    /// <summary>The inactive-tenant slice, for the guard that asserts it is one.</summary>
    public static IReadOnlyList<TenantlessPath> InactiveTenantPaths => InactiveTenantAllowedPaths;

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

    /// <summary>
    /// Whether the apex of a single-tenant install serves a tenantless request as that tenant.
    /// See <see cref="TenantlessPath.SoleTenantAtApex"/>.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="method">The request method, or null to ask about any method.</param>
    public static bool IsSoleTenantAtApex(string path, string? method = null) =>
        TenantlessAllowedPaths.Any(p => p.SoleTenantAtApex && p.Matches(path, method));

    /// <summary>
    /// Whether a request is served even though the resolved tenant is inactive.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="method">The request method, or null to ask about any method.</param>
    public static bool IsInactiveTenantAllowed(string path, string? method = null) =>
        InactiveTenantAllowedPaths.Any(p => p.Matches(path, method));

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantAccessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
        var slug = SubdomainParser.Extract(context.Request.Host.Host, _config.BaseDomain);

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
            context.SetTenantContext(shareTenant);
            context.SetShareAccess();
            context.RequestServices.GetRequiredService<ICategoryReadContext>().MarkShare();
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var isTenantlessAllowedPath = IsTenantlessAllowed(path, context.Request.Method);

        // Falls through to the tenantless passthrough when zero or several tenants exist.
        if (slug == null && IsSoleTenantAtApex(path, context.Request.Method))
        {
            var soleApexTenant = (await GetApexTenantsAsync(context.RequestServices)).SoleTenant;
            if (soleApexTenant != null)
            {
                tenantAccessor.SetTenant(soleApexTenant);
                context.SetTenantContext(soleApexTenant);
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
            var apex = await GetApexTenantsAsync(context.RequestServices);
            var soleTenant = apex.SoleTenant;
            if (soleTenant == null)
            {
                if (!apex.AnyTenantExists)
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
            context.SetTenantContext(soleTenant);
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
            if (IsInactiveTenantAllowed(path, context.Request.Method))
            {
                await _next(context);
                return;
            }

            _logger.LogWarning("Tenant '{Slug}' is inactive", slug);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = TenantInactiveCode });
            return;
        }

        tenantAccessor.SetTenant(tenantContext);
        context.SetTenantContext(tenantContext);

        await _next(context);
    }

    /// <summary>Cache key holding the resolved <see cref="TenantContext"/> for a slug.</summary>
    public static string TenantCacheKey(string slug) => $"tenant:{slug}";

    /// <summary>Cache key holding the <see cref="ApexTenants"/> answer the apex resolves through.</summary>
    public const string SoleTenantCacheKey = "tenant:__sole__";

    /// <summary>
    /// What the apex needs to know about the tenant table, as one cache entry.
    /// </summary>
    /// <param name="SoleTenant">
    /// The sole servable tenant (<see cref="ITenantRepository.FindSoleTenantAsync"/>), or null
    /// when there is none or several.
    /// </param>
    /// <param name="AnyTenantExists">
    /// Whether any tenant a caller could be served exists at all, which separates a fresh install
    /// (503 setup_required) from one whose tenants are ambiguous or inactive (404).
    /// </param>
    public sealed record ApexTenants(TenantContext? SoleTenant, bool AnyTenantExists);

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
    /// <see cref="SoleTenantCacheKey"/>, which holds a copy of the same context. Call this on
    /// every write that adds, removes, activates or deactivates a tenant, not only on writes to
    /// the columns <see cref="TenantContext"/> carries — an install that gains a second tenant
    /// otherwise keeps serving the first one from its apex.
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

        var tenant = await services.GetRequiredService<ITenantRepository>().FindBySlugAsync(slug, CancellationToken.None);

        if (tenant == null)
            return null;

        var tenantContext = tenant.ToContext();
        _cache.Set(cacheKey, tenantContext, CacheDuration);
        return tenantContext;
    }

    /// <summary>
    /// Reads the <see cref="ApexTenants"/> answer, from the rows on a miss.
    /// </summary>
    /// <remarks>
    /// A demo tenant is an ordinary active tenant, so it would otherwise be counted here; the
    /// repository's directory reads leave it out.
    /// <para>
    /// The fresh-install answer — no tenant exists at all — is deliberately not cached, and is the
    /// one answer worth a query per request. It is true only until an install's first tenant is
    /// created, and a cached copy can outlive that: the setup page's own <c>/api/v4/status</c> poll
    /// can read zero tenants, lose the race to the create, and then write the answer it read,
    /// leaving an instance that answers <c>setup_required</c> at its apex while its database has a
    /// tenant. Every other answer changes only when a tenant is created, removed or toggled, all of
    /// which call <see cref="EvictTenant"/>.
    /// </para>
    /// </remarks>
    private async Task<ApexTenants> GetApexTenantsAsync(IServiceProvider services)
    {
        if (_cache.TryGetValue(SoleTenantCacheKey, out ApexTenants? cached) && cached is not null)
            return cached;

        var tenants = services.GetRequiredService<ITenantRepository>();
        var tenant = await tenants.FindSoleTenantAsync(CancellationToken.None);

        var answer = tenant is null
            ? new ApexTenants(null, await tenants.AnyNonDemoAsync(CancellationToken.None))
            : new ApexTenants(tenant.ToContext(), true);

        if (answer.AnyTenantExists)
            _cache.Set(SoleTenantCacheKey, answer, CacheDuration);

        return answer;
    }
}
