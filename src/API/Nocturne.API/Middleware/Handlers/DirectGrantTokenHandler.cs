using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

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
    private const string TokenPrefix = "noc_";

    /// <summary>
    /// Handler priority (150 - after session cookies, before OIDC/legacy JWT)
    /// </summary>
    public int Priority => 150;

    /// <summary>
    /// Handler name for logging
    /// </summary>
    public string Name => "DirectGrantTokenHandler";

    private readonly IDbContextFactory<NocturneDbContext> _dbContextFactory;
    private readonly ILogger<DirectGrantTokenHandler> _logger;

    /// <summary>
    /// Creates a new instance of DirectGrantTokenHandler
    /// </summary>
    public DirectGrantTokenHandler(
        IDbContextFactory<NocturneDbContext> dbContextFactory,
        ILogger<DirectGrantTokenHandler> logger)
    {
        _dbContextFactory = dbContextFactory;
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

        // Skip JWT-formatted tokens (base64url-encoded JSON starts with eyJ)
        if (token.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return AuthResult.Skip();
        }

        // Direct grants are tenant-scoped — only match grants for the resolved tenant
        var tenantCtx = context.Items["TenantContext"] as TenantContext;
        if (tenantCtx is null)
        {
            return AuthResult.Skip();
        }

        var tokenHash = ComputeSha256Hex(token);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        dbContext.TenantId = tenantCtx.TenantId;

        var grant = await dbContext.OAuthGrants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(g => g.TokenHash == tokenHash
                     && g.TenantId == tenantCtx.TenantId
                     && g.GrantType == OAuthGrantTypes.Direct
                     && g.RevokedAt == null)
            .FirstOrDefaultAsync();

        if (grant == null)
        {
            _logger.LogDebug("No matching direct grant found for token");
            return AuthResult.Skip();
        }

        // Update last used metadata (fire and forget via separate context)
        var ipAddress = context.Connection.RemoteIpAddress?.ToString();
        var userAgent = context.Request.Headers.UserAgent.FirstOrDefault();
        _ = UpdateLastUsedAsync(grant.Id, tenantCtx.TenantId, ipAddress, userAgent);

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
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader)
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authHeader["Bearer ".Length..].Trim();
            if (!string.IsNullOrEmpty(bearer))
            {
                return bearer;
            }
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

    private async Task UpdateLastUsedAsync(Guid grantId, Guid tenantId, string? ipAddress, string? userAgent)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
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
            _logger.LogWarning(ex, "Failed to update last used metadata for grant {GrantId}", grantId);
        }
    }

    internal static string ComputeSha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
