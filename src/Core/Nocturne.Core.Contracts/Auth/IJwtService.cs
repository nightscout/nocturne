using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for JWT token generation and validation
/// Access tokens are short-lived JWTs validated statelessly
/// </summary>
public interface IJwtService
{
    /// <param name="permissions">Resolved permissions to include</param>
    /// <param name="roles">Role names to include</param>
    /// <param name="lifetime">Optional custom lifetime (defaults to configuration)</param>
    string GenerateAccessToken(
        SubjectInfo subject,
        IEnumerable<string> permissions,
        IEnumerable<string> roles,
        TimeSpan? lifetime = null
    );

    /// <summary>
    /// Generate an access token JWT for a subject with OAuth scopes.
    /// Used by the OAuth token endpoint to issue scoped access tokens.
    /// </summary>
    /// <param name="permissions">Resolved permissions to include</param>
    /// <param name="roles">Role names to include</param>
    /// <param name="scopes">OAuth scopes to include in the token</param>
    /// <param name="clientId">OAuth client ID that requested this token</param>
    /// <param name="limitTo24Hours">When true, data requests should only return data from the last 24 hours</param>
    /// <param name="tenantId">Tenant this token is pinned to. When present, the token is
    /// only valid on the tenant subdomain that issued it.</param>
    /// <param name="lifetime">Optional custom lifetime (defaults to configuration)</param>
    /// <param name="platformAccess">When true, marks this token as a platform-admin tenant-access
    /// grant. Combined with <paramref name="tenantId"/>, this is what authorizes out-of-tenant
    /// superuser access — a marker that no ordinary tenant-pinned token carries.</param>
    /// <param name="grantId">The OAuth grant this token was minted from. Access tokens are
    /// stateless, so this is what lets a grant revocation invalidate them: the authenticating
    /// handler rejects a token whose grant is revoked. Omit only for tokens that have no grant
    /// behind them (platform access, recovery, connector link tokens).</param>
    string GenerateAccessToken(
        SubjectInfo subject,
        IEnumerable<string> permissions,
        IEnumerable<string> roles,
        IEnumerable<string> scopes,
        string? clientId = null,
        bool limitTo24Hours = false,
        Guid? tenantId = null,
        TimeSpan? lifetime = null,
        bool platformAccess = false,
        Guid? grantId = null
    );

    /// <returns>Validation result with claims if valid</returns>
    JwtValidationResult ValidateAccessToken(string token);

    /// <summary>
    /// Generate a refresh token (random string, stored in DB)
    /// </summary>
    string GenerateRefreshToken();

    /// <summary>
    /// Compute SHA256 hash of a refresh token for storage
    /// </summary>
    /// <param name="refreshToken">Plain refresh token</param>
    /// <returns>Hex-encoded SHA256 hash</returns>
    string HashRefreshToken(string refreshToken);

    TimeSpan GetAccessTokenLifetime();

    TimeSpan GetRefreshTokenLifetime();
}

public class SubjectInfo
{
    /// <summary>
    /// Subject ID (UUID)
    /// </summary>
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

}

public class JwtValidationResult
{
    public bool IsValid { get; set; }

    /// <summary>
    /// Extracted claims if valid
    /// </summary>
    public JwtClaims? Claims { get; set; }

    /// <summary>
    /// Error message if invalid
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Error code for programmatic handling
    /// </summary>
    public JwtValidationError? ErrorCode { get; set; }

    public static JwtValidationResult Success(JwtClaims claims) =>
        new() { IsValid = true, Claims = claims };

    public static JwtValidationResult Failure(string error, JwtValidationError errorCode) =>
        new()
        {
            IsValid = false,
            Error = error,
            ErrorCode = errorCode,
        };
}

public class JwtClaims
{
    public Guid SubjectId { get; set; }

    public string? Name { get; set; }

    public string? Email { get; set; }

    public List<string> Roles { get; set; } = new();

    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// OAuth scopes (space-delimited in JWT, parsed to list)
    /// </summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// OAuth client ID that requested this token
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Tenant this token is pinned to. Null for non-tenant-pinned tokens
    /// (legacy session JWTs). When set, the token must only be accepted on
    /// the matching tenant subdomain.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Whether this token is a platform-admin tenant-access grant. Only such tokens
    /// (combined with a matching <see cref="TenantId"/> pin) confer out-of-tenant
    /// superuser access; no ordinary tenant-pinned token carries this marker.
    /// </summary>
    public bool PlatformAccess { get; set; }

    /// <summary>
    /// When true, data requests using this token should only return data from the
    /// last 24 hours (rolling window from current request time).
    /// </summary>
    public bool LimitTo24Hours { get; set; }

    /// <summary>
    /// The OAuth grant this token was minted from, if any. Null for tokens with no grant behind
    /// them. When set, the grant's revocation state decides whether the token is still usable.
    /// </summary>
    public Guid? GrantId { get; set; }

    /// <summary>
    /// JWT ID (jti claim)
    /// </summary>
    public string? JwtId { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

public enum JwtValidationError
{
    InvalidFormat,

    InvalidSignature,

    Expired,

    InvalidIssuer,

    InvalidAudience,

    /// <summary>
    /// Token is not yet valid (nbf claim)
    /// </summary>
    NotYetValid,

    MissingClaims,

    Unknown,
}
