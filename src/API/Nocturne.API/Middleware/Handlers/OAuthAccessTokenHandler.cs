using System.IdentityModel.Tokens.Jwt;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Authorization;

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

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        // Check for Bearer token in Authorization header
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        if (
            string.IsNullOrEmpty(authHeader)
            || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
        {
            return AuthResult.Skip();
        }

        var token = authHeader["Bearer ".Length..].Trim();

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

        // Validate using IJwtService (uses the correct Jwt:SecretKey, issuer, audience)
        using var scope = _scopeFactory.CreateScope();
        var jwtService = scope.ServiceProvider.GetRequiredService<IJwtService>();

        var validationResult = jwtService.ValidateAccessToken(token);

        if (!validationResult.IsValid || validationResult.Claims is null)
        {
            _logger.LogDebug(
                "OAuth access token validation failed: {Error}",
                validationResult.Error
            );
            return AuthResult.Failure(validationResult.Error ?? "Invalid OAuth access token");
        }

        var claims = validationResult.Claims;

        // Check revocation cache
        if (!string.IsNullOrEmpty(claims.JwtId))
        {
            var revocationCache =
                scope.ServiceProvider.GetRequiredService<IOAuthTokenRevocationCache>();
            if (await revocationCache.IsRevokedAsync(claims.JwtId))
            {
                _logger.LogDebug("OAuth access token has been revoked (jti: {Jti})", claims.JwtId);
                return AuthResult.Failure("Token has been revoked");
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
}
