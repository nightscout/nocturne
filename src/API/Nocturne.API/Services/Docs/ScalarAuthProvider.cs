using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Demo;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Docs;

/// <summary>
/// Per-request authentication context for the Scalar reference UI, so its "Authorize"
/// and "Send request" actions work against the tenant the page was opened on.
/// </summary>
/// <param name="ClientId">Client id of that tenant's Scalar OAuth client.</param>
/// <param name="RedirectUri">
/// The exact redirect URI registered for <paramref name="ClientId"/>. Authorize-time
/// matching is byte-exact, so this is built from the request and reused verbatim.
/// </param>
/// <param name="BearerToken">
/// A ready-to-use access token, or <see langword="null"/>. Only ever populated for a demo
/// tenant, whose account is shared and synthetic by design.
/// </param>
public sealed record ScalarAuthContext(string ClientId, string RedirectUri, string? BearerToken)
{
    /// <summary>Key under which the context is stashed on <see cref="HttpContext.Items"/>.</summary>
    public const string HttpContextItemKey = "ScalarAuth";
}

/// <summary>
/// Prepares the Scalar reference UI's authentication for the tenant whose host the docs
/// were opened on: registers that tenant's Scalar OAuth client on demand and, for a demo
/// tenant, hands Scalar a bearer token so requests work without any sign-in step.
/// </summary>
/// <remarks>
/// The docs paths deliberately bypass tenant resolution and authentication (they must
/// render on a bare instance), so the tenant is resolved here from the request host
/// instead of <see cref="Core.Contracts.Multitenancy.ITenantAccessor"/>.
/// <para>
/// Registration is on demand rather than seeded at provision time because redirect-URI
/// matching is byte-exact: deriving the URI from the live request is the only way to be
/// sure it matches what the browser will present, across the apex, tenant subdomains, and
/// local development ports. It also means the client reappears by itself after a demo
/// reset, which wipes the tenant's OAuth clients along with the rest of its configuration.
/// </para>
/// </remarks>
public sealed class ScalarAuthProvider
{
    /// <summary>
    /// RFC 7591 software_id of the bundled Scalar docs client. Uniquely identifies the row
    /// per tenant, so registration is idempotent.
    /// </summary>
    public const string ScalarSoftwareId = "dev.nocturne.scalar";

    private const string ClientName = "Scalar API Reference";

    /// <summary>
    /// Ceiling on redirect URIs held by one tenant's Scalar client.
    /// </summary>
    /// <remarks>
    /// Unreachable by construction as things stand: the URI is assembled from the configured base
    /// domain and the tenant's stored slug, and the only part a caller influences is the scheme,
    /// which <see cref="RedirectUriValidator"/> narrows to https on a public host and http on
    /// loopback. So one tenant can accumulate at most one entry per deployment origin, and a
    /// deployment has one.
    /// <para>
    /// Kept rather than deleted because the row is written by an unauthenticated request, so the
    /// bound should not depend on the URI-construction argument staying true. An earlier commit
    /// message on this branch claimed a test covered this cap; none did. <see
    /// cref="Docs.ScalarAuthProviderTests"/> now exercises it directly.
    /// </para>
    /// </remarks>
    internal const int MaxRedirectUris = 5;

    // Matches the tenant-resolution and public-access caches: long enough to keep the
    // docs page off the database, short enough that a demo reset heals within a couple
    // of minutes.
    private static readonly TimeSpan ClientCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DemoTokenCacheTtl = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly DemoTenantService _demoTenantService;
    private readonly ISessionService _sessionService;
    private readonly RedirectUriValidator _redirectUriValidator;
    private readonly IMemoryCache _cache;
    private readonly BaseDomainOptions _baseDomain;
    private readonly ILogger<ScalarAuthProvider> _logger;

    public ScalarAuthProvider(
        IDbContextFactory<NocturneDbContext> factory,
        DemoTenantService demoTenantService,
        ISessionService sessionService,
        RedirectUriValidator redirectUriValidator,
        IMemoryCache cache,
        IOptions<BaseDomainOptions> baseDomain,
        ILogger<ScalarAuthProvider> logger)
    {
        _factory = factory;
        _demoTenantService = demoTenantService;
        _sessionService = sessionService;
        _redirectUriValidator = redirectUriValidator;
        _cache = cache;
        _baseDomain = baseDomain.Value;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the request's tenant and stashes a <see cref="ScalarAuthContext"/> on
    /// <see cref="HttpContext.Items"/> for the Scalar options delegate to read. Does
    /// nothing when the host resolves to no tenant, so the docs still render.
    /// </summary>
    public async Task PrepareAsync(HttpContext context)
    {
        try
        {
            var scheme = ParseScheme(context);
            if (scheme is null)
                return;

            var resolved = await ResolveAsync(context.Request.Host, scheme, context.RequestAborted);
            if (resolved is null)
                return;

            var redirectUri = resolved.RedirectUri;

            // Belt and braces: the URI is assembled from configuration and the tenant's
            // own slug, so this should never fail. It is the same gate a redirect URI
            // submitted through client registration passes, and it is what stops a
            // cleartext http URI being registered for a public host.
            if (!_redirectUriValidator.IsValidForRegistration(redirectUri))
            {
                _logger.LogWarning("Rejected Scalar redirect URI for tenant {TenantId}", resolved.TenantId);
                return;
            }

            var clientId = await EnsureScalarClientAsync(
                resolved.TenantId, redirectUri, context.RequestAborted);
            if (clientId is null)
                return;

            var bearerToken = resolved.IsDemo
                ? await GetDemoBearerTokenAsync(resolved.TenantId, context)
                : null;

            if (bearerToken is not null)
            {
                // The token is embedded in the page, so this response is per-credential
                // even though the URL is not. Keep it out of shared caches: a CDN with a
                // blanket "cache everything" rule would otherwise serve it on past the
                // reset that revokes it.
                context.Response.Headers.CacheControl = "no-store, private";
            }

            context.Items[ScalarAuthContext.HttpContextItemKey] =
                new ScalarAuthContext(clientId, redirectUri, bearerToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reference is worth serving even unauthenticated: fall through to the
            // static configuration rather than failing the page.
            _logger.LogWarning(ex, "Failed to prepare Scalar authentication context");
        }
    }

    /// <summary>
    /// Returns the request's scheme, or <see langword="null"/> when it is not http(s).
    /// </summary>
    /// <remarks>
    /// Read from <see cref="HttpRequest.Scheme"/>, which <c>UseForwardedHeaders</c> has
    /// already set from <c>X-Forwarded-Proto</c> — reading the header again here would
    /// pick a different entry from the one the rest of the pipeline used.
    /// </remarks>
    private static string? ParseScheme(HttpContext context)
    {
        var scheme = context.Request.Scheme?.ToLowerInvariant();
        return scheme is "http" or "https" ? scheme : null;
    }

    /// <summary>
    /// Resolves the tenant the docs were opened on and the redirect URI to register for
    /// it, or <see langword="null"/> when <paramref name="requestHost"/> is not an origin
    /// this deployment serves.
    /// </summary>
    /// <remarks>
    /// The request host is client-controllable: the gateway forwards
    /// <c>X-Forwarded-Host</c> untouched and <c>UseForwardedHeaders</c> runs with no
    /// trusted-proxy list, so it decides <see cref="HttpRequest.Host"/>. It is therefore
    /// used only to <em>select</em> a tenant, never to build the redirect URI — that is
    /// assembled from the configured base domain and the tenant's own slug as stored, so
    /// the only byte of it a caller influences is the scheme, which
    /// <see cref="RedirectUriValidator"/> then gates. Without this, a host belonging to
    /// nobody (<c>attacker.example</c>) fell through to the sole-tenant branch below and
    /// registered an attacker-controlled OAuth redirect URI on that tenant.
    /// <para>
    /// Returns <see langword="null"/> for the apex with more than one tenant, an unknown
    /// slug, an inactive tenant, and for a public share host — a share grants read-only
    /// anonymous access and must not be handed a client or a token.
    /// </para>
    /// </remarks>
    private async Task<ResolvedDocsTenant?> ResolveAsync(
        HostString requestHost, string scheme, CancellationToken ct)
    {
        var (baseHost, basePort) = _baseDomain.SplitHostPort();
        if (baseHost is null || string.IsNullOrEmpty(requestHost.Host))
            return null;

        // The port has to be the one the deployment is served on, so a caller cannot
        // register extra origins that differ only by port.
        if (requestHost.Port != basePort)
            return null;

        var slug = SubdomainParser.Extract(requestHost.Host, baseHost);

        // A share grants anonymous read-only access, so it must not be handed an OAuth client
        // or a token. Shared with TenantResolutionMiddleware so both agree what a share host is.
        if (slug is not null && SubdomainParser.TryExtractShareToken(slug, out _))
            return null;

        // Only resolutions that found a tenant are cached. The docs paths run before the rate
        // limiter, so the host on an unauthenticated request decides this key — caching misses
        // would let arbitrary hostnames each pin an entry. A miss costs one indexed lookup on
        // tenants.slug; a hit is what repeated views of a real tenant's docs page would otherwise
        // pay for on every request.
        var cacheKey = $"scalar-tenant:{scheme}:{requestHost.Value}";
        if (_cache.TryGetValue(cacheKey, out ResolvedDocsTenant? cached) && cached is not null)
            return cached;

        await using var db = await _factory.CreateDbContextAsync(ct);

        TenantEntity? tenant;
        if (slug is null)
        {
            // Apex. Single-tenant installs serve everything from it; anything that is not
            // the configured apex is not ours to register a redirect URI for.
            if (!requestHost.Host.Equals(baseHost, StringComparison.OrdinalIgnoreCase))
                return null;

            var soleTenants = await db.Set<TenantEntity>()
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Id)
                .Take(2)
                .ToListAsync(ct);

            if (soleTenants.Count != 1)
                return null;

            tenant = soleTenants[0];
        }
        else
        {
            tenant = await db.Set<TenantEntity>()
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive, ct);

            if (tenant is null)
                return null;
        }

        var canonicalHost = slug is null ? baseHost : $"{tenant.Slug}.{baseHost}";
        var authority = basePort is null ? canonicalHost : $"{canonicalHost}:{basePort}";

        var resolved = new ResolvedDocsTenant(
            tenant.Id, tenant.IsDemo, $"{scheme}://{authority}/scalar");

        // Same TTL as the client cache, so a demo reset — which clears the tenant's OAuth clients —
        // heals both within the same couple of minutes.
        _cache.Set(cacheKey, resolved, ClientCacheTtl);

        return resolved;
    }

    /// <summary>
    /// The parts of the resolved tenant the docs page needs. A record rather than the entity so
    /// nothing tracked by a disposed context is held in the cache.
    /// </summary>
    private sealed record ResolvedDocsTenant(Guid TenantId, bool IsDemo, string RedirectUri);

    /// <summary>
    /// Registers the tenant's Scalar OAuth client if absent, and adds
    /// <paramref name="redirectUri"/> to it if not already registered. Returns the row's
    /// client id, or <see langword="null"/> when the redirect URI cap is reached.
    /// </summary>
    /// <remarks>
    /// <paramref name="redirectUri"/> must already be validated: it is persisted as an
    /// allowed OAuth redirect target, and authorize-time matching is byte-exact.
    /// <para>
    /// <c>IsKnown</c> is taken from the bundled directory rather than asserted. The
    /// consent screen keys its "app not recognized" warning off that flag, and this row
    /// is created by an unauthenticated request — it must not be able to badge itself as
    /// vetted.
    /// </para>
    /// </remarks>
    private async Task<string?> EnsureScalarClientAsync(
        Guid tenantId, string redirectUri, CancellationToken ct)
    {
        var cacheKey = $"scalar-client:{tenantId}:{redirectUri}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return cached;

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        var client = await db.OAuthClients
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.SoftwareId == ScalarSoftwareId, ct);

        if (client is null)
        {
            client = new OAuthClientEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ClientId = Guid.CreateVersion7().ToString(),
                SoftwareId = ScalarSoftwareId,
                ClientName = ClientName,
                DisplayName = ClientName,
                IsKnown = KnownOAuthClients.MatchBySoftwareId(ScalarSoftwareId) is not null,
                RedirectUris = JsonSerializer.Serialize(new[] { redirectUri }),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.OAuthClients.Add(client);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var registered = DeserializeRedirectUris(client.RedirectUris);
            if (!registered.Contains(redirectUri, StringComparer.Ordinal))
            {
                // Each distinct origin this instance is served on adds one entry. A real
                // deployment has one or two; a cap keeps an unauthenticated caller from
                // growing the row without bound.
                if (registered.Count >= MaxRedirectUris)
                {
                    _logger.LogWarning(
                        "Scalar client for tenant {TenantId} already holds {Count} redirect URIs — not adding another",
                        tenantId, registered.Count);
                    return null;
                }

                registered.Add(redirectUri);
                client.RedirectUris = JsonSerializer.Serialize(registered);
                client.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }

        _cache.Set(cacheKey, client.ClientId, ClientCacheTtl);
        return client.ClientId;
    }

    private static List<string> DeserializeRedirectUris(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns an access token for the demo tenant's shared member, cached so that
    /// reloading the docs does not mint a session per page view. Keyed on the member's
    /// subject id, which changes on every reset, so a stale token is never reused.
    /// </summary>
    private async Task<string?> GetDemoBearerTokenAsync(Guid tenantId, HttpContext context)
    {
        var subjectId = await _demoTenantService.FindDemoMemberSubjectIdAsync(tenantId, context.RequestAborted);
        if (subjectId is null)
            return null;

        var cacheKey = $"scalar-demo-token:{subjectId}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return cached;

        // Bounded like the sign-in endpoint: /scalar is anonymous too, and the token cache is
        // keyed on the subject, so a reset (which changes it) lets the docs mint again.
        await _demoTenantService.TrimSessionsAsync(subjectId.Value, context.RequestAborted);

        // No IP or user-agent — see DemoSessionController: the demo subject is shared, and its
        // session list is readable by anyone holding a demo session.
        var session = await _sessionService.IssueSessionAsync(
            subjectId.Value,
            new SessionContext(DeviceDescription: "demo-scalar"),
            context.RequestAborted);

        // Expire ahead of the access token itself so a cached value is never handed out
        // already expired.
        var ttl = TimeSpan.FromSeconds(Math.Max(session.ExpiresInSeconds - 60, 60));
        _cache.Set(cacheKey, session.AccessToken, ttl < DemoTokenCacheTtl ? ttl : DemoTokenCacheTtl);

        return session.AccessToken;
    }
}
