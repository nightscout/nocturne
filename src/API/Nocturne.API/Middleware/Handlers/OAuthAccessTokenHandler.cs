using System.IdentityModel.Tokens.Jwt;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for OAuth 2.0 access tokens (JWTs with scope/client_id claims).
/// Validates tokens using IJwtService which uses the configured Jwt:SecretKey, matching
/// the key used by OAuthTokenService to generate them.
/// </summary>
public class OAuthAccessTokenHandler : IAuthHandler
{
    /// <summary>
    /// Handler priority (150 - after OIDC, before legacy JWT).
    /// Must run before LegacyJwtHandler because both recognize JWTs, but
    /// LegacyJwtHandler may use a different signing key (API_SECRET fallback)
    /// and returns Failure (not Skip) on validation errors, blocking the chain.
    /// </summary>
    public int Priority => 150;

    /// <summary>
    /// Handler name for logging
    /// </summary>
    public string Name => "OAuthAccessTokenHandler";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OAuthAccessTokenHandler> _logger;

    /// <summary>
    /// Creates a new instance of OAuthAccessTokenHandler
    /// </summary>
    public OAuthAccessTokenHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<OAuthAccessTokenHandler> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Path prefix of the SignalR hub endpoints, the only place the token is accepted on the query
    /// string.
    /// </summary>
    private static readonly PathString HubPathPrefix = new("/hubs");

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        var token = ExtractToken(context);

        // Must be a JWT (3 dot-separated parts)
        if (string.IsNullOrEmpty(token) || token.Count(c => c == '.') != 2)
        {
            return AuthResult.Skip();
        }

        // Peek at the token to check for OAuth-specific claims (scope or client_id)
        // without validating the signature yet
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);

            var hasScope = jwtToken.Claims.Any(c => c.Type == "scope");
            var hasClientId = jwtToken.Claims.Any(c => c.Type == "client_id");

            if (!hasScope && !hasClientId)
            {
                // Not an OAuth token, let the next handler try
                return AuthResult.Skip();
            }
        }
        catch
        {
            // Can't read as JWT, skip
            return AuthResult.Skip();
        }

        using var scope = _scopeFactory.CreateScope();
        var credentialValidator = scope.ServiceProvider.GetRequiredService<IJwtCredentialValidator>();
        var validationResult = await credentialValidator.ValidateAsync(token);

        if (!validationResult.IsValid)
        {
            _logger.LogDebug(
                "OAuth access token refused ({Rejection}): {Error}",
                validationResult.Rejection, validationResult.Error);
            return AuthResult.Failure(validationResult.Error ?? "Invalid OAuth access token");
        }

        var claims = validationResult.Claims!;

        // Enforce tenant pin: reject tokens issued for a different tenant
        if (claims.TenantId.HasValue)
        {
            var tenantCtx = context.GetTenantContext();
            if (tenantCtx is null || tenantCtx.TenantId != claims.TenantId.Value)
            {
                _logger.LogWarning(
                    "OAuth access token tenant mismatch: token tenant {TokenTenant}, request tenant {RequestTenant}",
                    claims.TenantId, tenantCtx?.TenantId);
                return AuthResult.Failure("Token is not valid for this tenant");
            }
        }

        var authContext = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.OAuthAccessToken,
            SubjectId = claims.SubjectId,
            SubjectName = claims.Name,
            Email = claims.Email,
            Roles = claims.Roles,
            Permissions = claims.Permissions,
            Scopes = claims.Scopes,
            RawToken = token,
            ExpiresAt = claims.ExpiresAt,
            LimitTo24Hours = claims.LimitTo24Hours,
        };

        _logger.LogDebug(
            "OAuth access token authentication successful for subject {SubjectId} (client: {ClientId})",
            claims.SubjectId,
            claims.ClientId ?? "none"
        );

        return AuthResult.Success(authContext);
    }

    /// <summary>
    /// Extracts the bearer token from the Authorization header, or — on a SignalR hub path only —
    /// from the <c>access_token</c> query parameter.
    /// </summary>
    /// <remarks>
    /// The SignalR clients cannot set headers on a WebSocket or SSE request, so they append the token
    /// to the query string under <c>access_token</c> (the JS <c>accessTokenFactory</c>, the .NET
    /// <c>AccessTokenProvider</c>, and the desktop companion's hand-built URL all use that key); the
    /// hub connection's <see cref="HttpContext"/> is that upgrade request, so without this a hub
    /// connection carries no authentication context. Restricted to <see cref="HubPathPrefix"/>
    /// because a query-string credential ends up in access logs and referrers.
    /// </remarks>
    private static string? ExtractToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        if (!string.IsNullOrEmpty(authHeader)
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader["Bearer ".Length..].Trim();
        }

        if (context.Request.Path.StartsWithSegments(HubPathPrefix))
        {
            return context.Request.Query["access_token"].FirstOrDefault();
        }

        return null;
    }
}
