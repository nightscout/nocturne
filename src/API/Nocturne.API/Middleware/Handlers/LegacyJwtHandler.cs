using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for legacy Nightscout JWT tokens.
/// These are self-issued JWTs exchanged from access tokens via
/// /api/v2/authorization/request/:accessToken. Runs after
/// <see cref="OAuthAccessTokenHandler"/>, so it only sees JWTs without OAuth
/// claims (scope/client_id).
/// </summary>
/// <remarks>
/// Validation and claim extraction are delegated to <see cref="IJwtService"/>.
/// Claim lookups must not use literal JWT claim names: JwtSecurityTokenHandler's
/// inbound claim-type map rewrites <c>sub</c> to <c>ClaimTypes.NameIdentifier</c>
/// and <c>role</c> to <c>ClaimTypes.Role</c> during validation, and permissions
/// are emitted as repeated singular <c>permission</c> claims. IJwtService owns
/// that mapping in one place.
/// </remarks>
public class LegacyJwtHandler : IAuthHandler
{
    /// <summary>
    /// Handler priority (200 - after OIDC and OAuth access tokens)
    /// </summary>
    public int Priority => 200;

    /// <summary>
    /// Handler name for logging
    /// </summary>
    public string Name => "LegacyJwtHandler";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LegacyJwtHandler> _logger;

    /// <summary>
    /// Creates a new instance of LegacyJwtHandler
    /// </summary>
    public LegacyJwtHandler(IServiceScopeFactory scopeFactory, ILogger<LegacyJwtHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        // Check for Bearer token in Authorization header
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        if (
            string.IsNullOrEmpty(authHeader)
            || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
        {
            // No Bearer token, skip to next handler
            return Task.FromResult(AuthResult.Skip());
        }

        var token = authHeader["Bearer ".Length..].Trim();

        // Check if it looks like a JWT (has 3 parts separated by dots)
        if (string.IsNullOrEmpty(token) || token.Count(c => c == '.') != 2)
        {
            // Not a JWT, skip to next handler (might be an opaque token)
            return Task.FromResult(AuthResult.Skip());
        }

        using var scope = _scopeFactory.CreateScope();
        var jwtService = scope.ServiceProvider.GetRequiredService<IJwtService>();

        var validationResult = jwtService.ValidateAccessToken(token);

        if (!validationResult.IsValid || validationResult.Claims is null)
        {
            _logger.LogDebug(
                "Legacy JWT validation failed: {Error}",
                validationResult.Error
            );
            return Task.FromResult(
                AuthResult.Failure(validationResult.Error ?? "Invalid token")
            );
        }

        var claims = validationResult.Claims;

        // Enforce tenant pin: reject tokens issued for a different tenant
        if (claims.TenantId.HasValue)
        {
            var tenantCtx = context.Items["TenantContext"] as TenantContext;
            if (tenantCtx is null || tenantCtx.TenantId != claims.TenantId.Value)
            {
                _logger.LogWarning(
                    "Legacy JWT tenant mismatch: token tenant {TokenTenant}, request tenant {RequestTenant}",
                    claims.TenantId, tenantCtx?.TenantId);
                return Task.FromResult(AuthResult.Failure("Token is not valid for this tenant"));
            }
        }

        var authContext = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.LegacyJwt,
            SubjectId = claims.SubjectId,
            SubjectName = claims.Name ?? claims.SubjectId.ToString(),
            Permissions = claims.Permissions,
            Roles = claims.Roles,
            Scopes = claims.Scopes,
            RawToken = token,
            ExpiresAt = claims.ExpiresAt,
        };

        _logger.LogDebug(
            "Legacy JWT authentication successful for subject {SubjectName}",
            authContext.SubjectName
        );
        return Task.FromResult(AuthResult.Success(authContext));
    }
}
