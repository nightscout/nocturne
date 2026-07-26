using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Demo;
using Nocturne.Core.Contracts.Auth;
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

    /// <summary>
    /// Fixed client id for the Scalar docs client. Unlike an app registering through DCR,
    /// this one is a first-party browser page, so a stable, unguessable-by-nobody id is
    /// fine — it is a public client and holds no secret.
    /// </summary>
    public const string ScalarClientId = "nocturne-scalar-docs";

    private const string ClientName = "Scalar API Reference";

    // Matches the tenant-resolution and public-access caches: long enough to keep the
    // docs page off the database, short enough that a demo reset heals within a couple
    // of minutes.
    private static readonly TimeSpan ClientCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DemoTokenCacheTtl = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<NocturneDbContext> _factory;
    private readonly DemoTenantService _demoTenantService;
    private readonly ISessionService _sessionService;
    private readonly IMemoryCache _cache;
    private readonly BaseDomainOptions _baseDomain;
    private readonly ILogger<ScalarAuthProvider> _logger;

    public ScalarAuthProvider(
        IDbContextFactory<NocturneDbContext> factory,
        DemoTenantService demoTenantService,
        ISessionService sessionService,
        IMemoryCache cache,
        IOptions<BaseDomainOptions> baseDomain,
        ILogger<ScalarAuthProvider> logger)
    {
        _factory = factory;
        _demoTenantService = demoTenantService;
        _sessionService = sessionService;
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
            var tenant = await ResolveTenantAsync(context);
            if (tenant is null)
                return;

            var redirectUri = BuildRedirectUri(context);
            var clientId = await EnsureScalarClientAsync(tenant.Id, redirectUri, context.RequestAborted);

            var bearerToken = tenant.IsDemo
                ? await GetDemoBearerTokenAsync(tenant.Id, context)
                : null;

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
    /// Resolves the tenant from the request host. Returns <see langword="null"/> for the
    /// apex with more than one tenant, an unknown slug, an inactive tenant, or a public
    /// share host — a share grants read-only anonymous access and must not be handed a
    /// client or a token.
    /// </summary>
    private async Task<TenantEntity?> ResolveTenantAsync(HttpContext context)
    {
        var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(':')[0]
                   ?? context.Request.Host.Host;
        var slug = SubdomainParser.Extract(host, _baseDomain.BaseDomain);

        if (slug is not null && slug.EndsWith(".share", StringComparison.OrdinalIgnoreCase))
            return null;

        await using var db = await _factory.CreateDbContextAsync(context.RequestAborted);

        if (slug is null)
        {
            // Single-tenant installs serve everything from the apex.
            var soleTenants = await db.Set<TenantEntity>()
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Id)
                .Take(2)
                .ToListAsync(context.RequestAborted);

            return soleTenants.Count == 1 ? soleTenants[0] : null;
        }

        return await db.Set<TenantEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive, context.RequestAborted);
    }

    /// <summary>
    /// The URI Scalar's OAuth flow will be sent back to: the docs page itself, on the host
    /// the browser is already using.
    /// </summary>
    private static string BuildRedirectUri(HttpContext context)
    {
        var proto = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                    ?? context.Request.Scheme;
        var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                   ?? context.Request.Host.Value;

        return $"{proto}://{host}/scalar";
    }

    /// <summary>
    /// Registers the tenant's Scalar OAuth client if absent, and adds
    /// <paramref name="redirectUri"/> to it if not already registered.
    /// </summary>
    private async Task<string> EnsureScalarClientAsync(
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
            db.OAuthClients.Add(new OAuthClientEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ClientId = ScalarClientId,
                SoftwareId = ScalarSoftwareId,
                ClientName = ClientName,
                DisplayName = ClientName,
                IsKnown = true,
                RedirectUris = JsonSerializer.Serialize(new[] { redirectUri }),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            var registered = DeserializeRedirectUris(client.RedirectUris);
            if (!registered.Contains(redirectUri, StringComparer.Ordinal))
            {
                registered.Add(redirectUri);
                client.RedirectUris = JsonSerializer.Serialize(registered);
                client.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }

        _cache.Set(cacheKey, ScalarClientId, ClientCacheTtl);
        return ScalarClientId;
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
