using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for legacy Nightscout JWT tokens.
/// These are self-issued JWTs exchanged from access tokens via
/// /api/v2/authorization/request/:accessToken
/// </summary>
public class LegacyJwtHandler : IAuthHandler
{
    /// <summary>
    /// Handler priority (200 - after OIDC, before access token)
    /// </summary>
    public int Priority => 200;

    /// <summary>
    /// Handler name for logging
    /// </summary>
    public string Name => "LegacyJwtHandler";

    private readonly IConfiguration _configuration;
    private readonly ILogger<LegacyJwtHandler> _logger;
    private readonly byte[]? _jwtKey;
    private readonly TokenValidationParameters? _validationParameters;

    /// <summary>
    /// Creates a new instance of LegacyJwtHandler
    /// </summary>
    public LegacyJwtHandler(IConfiguration configuration, ILogger<LegacyJwtHandler> logger)
    {
        _configuration = configuration;
        _logger = logger;

        // Use JWT_SECRET env var, fall back to API_SECRET, then Jwt:SecretKey from config
        var jwtSecret =
            _configuration[ServiceNames.ConfigKeys.JwtSecret]
            ?? _configuration[ServiceNames.ConfigKeys.ApiSecret]
            ?? _configuration["Jwt:SecretKey"];

        // Only configure JWT validation if a secret is available
        if (!string.IsNullOrEmpty(jwtSecret))
        {
            _jwtKey = Encoding.UTF8.GetBytes(jwtSecret);
            _validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(_jwtKey),
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
            };
        }
        else
        {
            _logger.LogWarning(
                "No JWT_SECRET, API_SECRET, or Jwt:SecretKey configured - legacy JWT authentication will be disabled"
            );
        }
    }

    /// <inheritdoc />
    public Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        // If no JWT secret is configured, skip this handler
        if (_validationParameters is null)
        {
            return Task.FromResult(AuthResult.Skip());
        }

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

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var principal = tokenHandler.ValidateToken(
                token,
                _validationParameters,
                out var validatedToken
            );

            if (validatedToken is not JwtSecurityToken jwtToken)
            {
                return Task.FromResult(AuthResult.Failure("Invalid JWT token format"));
            }

            // Extract claims
            var subjectId = principal.FindFirst("sub")?.Value;
            var subjectName = principal.FindFirst("name")?.Value ?? subjectId;
            var permissionsClaim = principal.FindFirst("permissions")?.Value;
            var rolesClaim = principal.FindFirst("roles")?.Value;

            if (string.IsNullOrEmpty(subjectId))
            {
                _logger.LogWarning("JWT token missing 'sub' claim");
                return Task.FromResult(AuthResult.Failure("JWT token missing subject claim"));
            }

            // Parse permissions (comma-separated or JSON array)
            var permissions = ParseListClaim(permissionsClaim);
            var roles = ParseListClaim(rolesClaim);

            // Extract OAuth scopes if present (space-delimited per RFC 6749)
            var scopeClaim = principal.FindFirst("scope")?.Value;
            var scopes = string.IsNullOrEmpty(scopeClaim)
                ? new List<string>()
                : scopeClaim
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

            // Create auth context
            var authContext = new AuthContext
            {
                IsAuthenticated = true,
                AuthType = AuthType.LegacyJwt,
                SubjectId = Guid.TryParse(subjectId, out var guid) ? guid : null,
                SubjectName = subjectName,
                Permissions = permissions,
                Roles = roles,
                Scopes = scopes,
                RawToken = token,
                ExpiresAt = jwtToken.ValidTo,
            };

            _logger.LogDebug(
                "Legacy JWT authentication successful for subject {SubjectName}",
                subjectName
            );
            return Task.FromResult(AuthResult.Success(authContext));
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogDebug("JWT token has expired");
            return Task.FromResult(AuthResult.Failure("Token has expired"));
        }
        catch (SecurityTokenValidationException ex)
        {
            _logger.LogWarning(ex, "JWT token validation failed");
            return Task.FromResult(AuthResult.Failure("Invalid token"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error validating JWT token");
            return Task.FromResult(AuthResult.Failure("Token validation error"));
        }
    }

    /// <summary>
    /// Parse a claim that may be comma-separated or JSON array format
    /// </summary>
    private static List<string> ParseListClaim(string? claimValue)
    {
        if (string.IsNullOrEmpty(claimValue))
            return [];

        // Try JSON array format first
        if (claimValue.StartsWith('['))
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(claimValue);
                return parsed ?? [];
            }
            catch
            {
                // Fall through to comma-separated parsing
            }
        }

        // Comma-separated format
        return claimValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
