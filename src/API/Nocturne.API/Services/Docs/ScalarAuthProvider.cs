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
    /// Ceiling on redirect URIs held by one tenant's Scalar client. One per origin the
    /// instance is served on; more than a handful means something is registering them
    /// that is not an operator opening the docs.
    /// </summary>
    private const int MaxRedirectUris = 5;

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
            var origin = ParseOrigin(context);
            if (origin is null)
                return;

            var tenant = await ResolveTenantAsync(origin, context.RequestAborted);
            if (tenant is null)
                return;

            // Built from the same validated origin the tenant was resolved from, then
            // held to the same rules as a redirect URI submitted through registration.
            var redirectUri = origin.ScalarUri;
            if (!_redirectUriValidator.IsValidForRegistration(redirectUri))
            {
                _logger.LogWarning("Rejected Scalar redirect URI for tenant {TenantId}", tenant.Id);
                return;
            }

            var clientId = await EnsureScalarClientAsync(tenant.Id, redirectUri, context.RequestAborted);
            if (clientId is null)
                return;

            var bearerToken = tenant.IsDemo
                ? await GetDemoBearerTokenAsync(tenant.Id, context)
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
    /// The request's public origin, parsed into validated parts.
    /// </summary>
    /// <remarks>
    /// Tenant resolution and redirect-URI construction must be driven by the same
    /// validated value. Reading the forwarded headers separately in each — one
    /// normalizing the host, the other interpolating it raw — let a single header
    /// satisfy the tenant check while carrying a different effective authority
    /// (<c>alice.example.com:443@attacker.example</c> parses as userinfo plus the
    /// attacker's host), which persisted an attacker-controlled OAuth redirect URI.
    /// </remarks>
    private sealed record RequestOrigin(string Scheme, string Host, int? Port)
    {
        public string Authority => Port is null ? Host : $"{Host}:{Port}";

        public string ScalarUri => $"{Scheme}://{Authority}/scalar";
    }

    /// <summary>
    /// Parses the request's public origin from the forwarded headers, or
    /// <see langword="null"/> when it is not a single well-formed http(s) origin.
    /// </summary>
    /// <remarks>
    /// The forwarded headers are client-controllable — the gateway passes them through
    /// untouched — so the host is rebuilt from a validated name and port rather than
    /// used as a string. Credentials, paths, and multi-value lists are rejected outright
    /// rather than normalized, because anything that needs normalizing here is not a
    /// host this deployment serves.
    /// </remarks>
    private static RequestOrigin? ParseOrigin(HttpContext context)
    {
        var rawScheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                        ?? context.Request.Scheme;
        var scheme = rawScheme.Trim().ToLowerInvariant();
        if (scheme is not (("http") or "https"))
            return null;

        var rawHost = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawHost))
            rawHost = context.Request.Host.Value;

        rawHost = rawHost.Trim();
        if (rawHost.AsSpan().IndexOfAny(ForbiddenHostChars) >= 0)
            return null;

        var (host, port) = SplitHostPort(rawHost);
        if (host is null)
            return null;

        // Rejects anything that is not a DNS name or IP literal, so the value can only
        // ever be interpolated back as the host it was validated as.
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return null;

        return new RequestOrigin(scheme, host, port);
    }

    /// <summary>
    /// Characters that never appear in a bare host and would change which authority a
    /// URI built from it addresses: credentials, list separators, and path/query starts.
    /// </summary>
    private static readonly char[] ForbiddenHostChars = ['@', ',', ' ', '\t', '/', '\\', '?', '#'];

    /// <summary>
    /// Splits <c>host[:port]</c>. Returns a null host when the value carries more than
    /// one colon (and is not a bracketed IPv6 literal) or an unparseable port.
    /// </summary>
    private static (string? Host, int? Port) SplitHostPort(string value)
    {
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
                return (null, null);

            var literal = value[..(close + 1)];
            var rest = value[(close + 1)..];
            if (rest.Length == 0)
                return (literal, null);
            if (rest[0] != ':' || !int.TryParse(rest[1..], out var literalPort) || literalPort is < 1 or > 65535)
                return (null, null);
            return (literal, literalPort);
        }

        var parts = value.Split(':');
        if (parts.Length == 1)
            return (parts[0], null);
        if (parts.Length != 2)
            return (null, null);
        if (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535)
            return (null, null);

        return (parts[0], port);
    }

    /// <summary>
    /// Resolves the tenant for the given origin. Returns <see langword="null"/> for the
    /// apex with more than one tenant, an unknown slug, an inactive tenant, or a public
    /// share host — a share grants read-only anonymous access and must not be handed a
    /// client or a token.
    /// </summary>
    private async Task<TenantEntity?> ResolveTenantAsync(RequestOrigin origin, CancellationToken ct)
    {
        var slug = SubdomainParser.Extract(origin.Host, _baseDomain.BaseDomain);

        if (slug is not null && slug.EndsWith(".share", StringComparison.OrdinalIgnoreCase))
            return null;

        await using var db = await _factory.CreateDbContextAsync(ct);

        if (slug is null)
        {
            // Single-tenant installs serve everything from the apex.
            var soleTenants = await db.Set<TenantEntity>()
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Id)
                .Take(2)
                .ToListAsync(ct);

            return soleTenants.Count == 1 ? soleTenants[0] : null;
        }

        return await db.Set<TenantEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive, ct);
    }

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

        var session = await _sessionService.IssueSessionAsync(
            subjectId.Value,
            new SessionContext(
                DeviceDescription: "demo-scalar",
                IpAddress: context.Connection.RemoteIpAddress?.ToString(),
                UserAgent: context.Request.Headers.UserAgent.FirstOrDefault()),
            context.RequestAborted);

        // Expire ahead of the access token itself so a cached value is never handed out
        // already expired.
        var ttl = TimeSpan.FromSeconds(Math.Max(session.ExpiresInSeconds - 60, 60));
        _cache.Set(cacheKey, session.AccessToken, ttl < DemoTokenCacheTtl ? ttl : DemoTokenCacheTtl);

        return session.AccessToken;
    }
}
