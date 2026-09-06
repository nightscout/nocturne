using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.API.Extensions;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for opaque direct grant tokens.
/// Validates tokens by SHA-256 hashing and looking up the grant in the database.
/// Accepts the token via the <c>Authorization: Bearer</c> header or the Nightscout-style
/// <c>?token=</c> query parameter (how xDrip4iOS and other Nightscout uploaders send it).
/// Skips JWT-formatted tokens (starting with "eyJ") to let other handlers process them.
/// </summary>
public class DirectGrantTokenHandler : IAuthHandler
{
    /// <summary>
    /// Prefix identifying opaque direct grant tokens (see <see cref="Controllers.Authentication.DirectGrantController"/>).
    /// </summary>
    internal const string TokenPrefix = "noc_";

    /// <summary>
    /// Handler priority (150 - after session cookies, before OIDC/legacy JWT)
    /// </summary>
    public int Priority => 150;

    /// <summary>
    /// Handler name for logging
    /// </summary>
    public string Name => "DirectGrantTokenHandler";

    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DirectGrantTokenHandler> _logger;

    /// <summary>
    /// Creates a new instance of DirectGrantTokenHandler
    /// </summary>
    public DirectGrantTokenHandler(
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        TimeProvider timeProvider,
        ILogger<DirectGrantTokenHandler> logger)
    {
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        var token = ExtractToken(context);
        if (string.IsNullOrEmpty(token))
        {
            return AuthResult.Skip();
        }

        // Deliberately looser than TokenFormat.IsJwt: a malformed JWT still belongs to the JWT
        // handlers, and no noc_ token can open with a Base64-URL JSON header.
        if (token.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return AuthResult.Skip();
        }

        // Direct grants are tenant-scoped — only match grants for the resolved tenant
        var tenantCtx = context.GetTenantContext();
        if (tenantCtx is null)
        {
            return AuthResult.Skip();
        }

        var grant = await FindActiveGrantAsync(
            _dbContextFactory, token, tenantCtx.TenantId, _timeProvider.GetUtcNow().UtcDateTime);

        if (grant == null)
        {
            _logger.LogDebug("No matching direct grant found for token");
            return AuthResult.Skip();
        }

        // Update last used metadata (fire and forget via separate context)
        var ipAddress = context.Connection.RemoteIpAddress?.ToString();
        var userAgent = context.Request.Headers.UserAgent.FirstOrDefault();
        _ = RecordLastUsedAsync(
            _dbContextFactory, _logger, grant.Id, tenantCtx.TenantId, ipAddress, userAgent);

        _logger.LogDebug("Direct grant authentication successful for grant {GrantId}, subject {SubjectId}",
            grant.Id, grant.SubjectId);

        return AuthResult.Success(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.DirectGrant,
            SubjectId = grant.SubjectId,
            Scopes = grant.Scopes,
            TokenId = grant.Id,
            LimitTo24Hours = false, // Direct grants defer to MemberScopeMiddleware for 24-hour limits
        });
    }

    /// <summary>
    /// Finds the active direct grant <paramref name="token"/> identifies on
    /// <paramref name="tenantId"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Runs on its own context pinned to <paramref name="tenantId"/>: callers hold a scope whose
    /// context may carry no tenant, and <c>oauth_grants</c> is tenant-scoped by both a global query
    /// filter and the <c>tenant_isolation</c> RLS policy, so an unpinned read matches no row. The
    /// grant's own <c>TenantId</c> is matched explicitly as well, so the tenant a grant authorizes is
    /// decided here rather than by whatever tenant state the connection carries.
    /// </remarks>
    /// <param name="dbContextFactory">Factory for the tenant-pinned context.</param>
    /// <param name="token">The presented token, <c>noc_</c>-prefixed.</param>
    /// <param name="tenantId">The tenant the grant must belong to.</param>
    /// <param name="now">The instant the grant must be active at.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<OAuthGrantEntity?> FindActiveGrantAsync(
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        string token,
        Guid tenantId,
        DateTime now,
        CancellationToken ct = default)
    {
        var tokenHash = HashUtils.Sha256Hex(token);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(ct);
        dbContext.TenantId = tenantId;

        return await ActiveDirectGrants(dbContext.OAuthGrants.AsNoTracking(), tenantId, now)
            .FirstOrDefaultAsync(g => g.TokenHash == tokenHash, ct);
    }

    /// <summary>
    /// Narrows <paramref name="grants"/> to the direct grants on <paramref name="tenantId"/> that
    /// authenticate at <paramref name="now"/>: not revoked, and either open-ended or short of their
    /// <c>ExpiresAt</c>. The expiry instant itself does not authenticate, matching
    /// <see cref="Services.Auth.GuestLinkService"/>.
    /// </summary>
    /// <remarks>
    /// Query filters are ignored here for the reason given on <see cref="FindActiveGrantAsync"/>.
    /// </remarks>
    internal static IQueryable<OAuthGrantEntity> ActiveDirectGrants(
        IQueryable<OAuthGrantEntity> grants, Guid tenantId, DateTime now)
    {
        return grants
            .IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId)
            .Where(IsLiveDirectGrant(now));
    }

    /// <summary>
    /// What makes a direct grant usable, independent of how the caller scopes it to a tenant.
    /// </summary>
    /// <remarks>
    /// Shared with the <c>/api/v2/authorization/request/{token}</c> exchange, which scopes by the
    /// global query filter rather than an explicit tenant id and so cannot reuse
    /// <see cref="ActiveDirectGrants"/> wholesale. It previously restated the predicate and omitted
    /// the expiry term, so a grant this handler and the hubs both refused could still be exchanged
    /// for a one-hour JWT.
    /// </remarks>
    /// <param name="now">The instant to judge expiry against.</param>
    internal static Expression<Func<OAuthGrantEntity, bool>> IsLiveDirectGrant(DateTime now) =>
        g => g.GrantType == OAuthGrantTypes.Direct
             && g.RevokedAt == null
             && (g.ExpiresAt == null || g.ExpiresAt > now);

    /// <summary>
    /// Extracts a direct grant token from the request. Accepts the <c>Authorization: Bearer</c>
    /// header (any opaque value) or the Nightscout-style <c>?token=</c> query parameter — how
    /// xDrip4iOS and other Nightscout uploaders send their credential.
    /// </summary>
    /// <remarks>
    /// On the query-parameter path the <c>noc_</c> prefix is normalized in: uploaders routinely
    /// drop the human-facing marker and send only the secret suffix, so both <c>noc_&lt;secret&gt;</c>
    /// and a bare <c>&lt;secret&gt;</c> resolve to the same grant. A value that isn't one of our
    /// tokens simply won't match a grant and falls through (Skip) to <see cref="AccessTokenHandler"/>,
    /// which owns the legacy <c>name-hash</c> <c>?token=</c> format.
    /// </remarks>
    private static string? ExtractToken(HttpContext context)
    {
        var bearer = context.Request.GetAuthorizationCredential();
        if (!string.IsNullOrEmpty(bearer))
        {
            return bearer;
        }

        var queryToken = context.Request.Query["token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(queryToken))
        {
            return queryToken.StartsWith(TokenPrefix, StringComparison.Ordinal)
                ? queryToken
                : TokenPrefix + queryToken;
        }

        return null;
    }

    /// <summary>
    /// Stamps when, from where and by what a grant was last presented.
    /// </summary>
    /// <remarks>
    /// Runs on its own context pinned to <paramref name="tenantId"/>, for the reason given on
    /// <see cref="FindActiveGrantAsync"/>. Every caller starts it fire-and-forget from a request that
    /// has already authenticated, so a failure is logged and swallowed rather than surfaced.
    /// </remarks>
    /// <param name="dbContextFactory">Factory for the tenant-pinned context.</param>
    /// <param name="logger">Where a failure is reported.</param>
    /// <param name="grantId">The grant that was presented.</param>
    /// <param name="tenantId">The tenant the grant belongs to.</param>
    /// <param name="ipAddress">The caller's address, when the connection has one.</param>
    /// <param name="userAgent">The caller's user-agent, when it sent one.</param>
    internal static async Task RecordLastUsedAsync(
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        ILogger logger,
        Guid grantId,
        Guid tenantId,
        string? ipAddress,
        string? userAgent)
    {
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync();
            dbContext.TenantId = tenantId;
            await dbContext.OAuthGrants
                .IgnoreQueryFilters()
                .Where(g => g.Id == grantId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.LastUsedAt, DateTime.UtcNow)
                    .SetProperty(g => g.LastUsedIp, ipAddress)
                    .SetProperty(g => g.LastUsedUserAgent, userAgent));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update last used metadata for grant {GrantId}", grantId);
        }
    }
}
