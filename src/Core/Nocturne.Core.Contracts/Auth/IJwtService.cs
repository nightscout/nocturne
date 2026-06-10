using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for JWT token generation and validation
/// Access tokens are short-lived JWTs validated statelessly
/// </summary>
public interface IJwtService
{
    /// <summary>
    /// Generate an access token JWT for a subject
    /// </summary>
    /// <param name="subject">Subject to generate token for</param>
    /// <param name="permissions">Resolved permissions to include</param>
    /// <param name="roles">Role names to include</param>
    /// <param name="lifetime">Optional custom lifetime (defaults to configuration)</param>
    /// <returns>JWT access token string</returns>
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
    /// <param name="subject">Subject to generate token for</param>
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
    /// <returns>JWT access token string</returns>
    string GenerateAccessToken(
        SubjectInfo subject,
        IEnumerable<string> permissions,
        IEnumerable<string> roles,
        IEnumerable<string> scopes,
        string? clientId = null,
        bool limitTo24Hours = false,
        Guid? tenantId = null,
        TimeSpan? lifetime = null,
        bool platformAccess = false
    );

    /// <summary>
    /// Validate an access token JWT
    /// </summary>
    /// <param name="token">JWT token string</param>
    /// <returns>Validation result with claims if valid</returns>
    JwtValidationResult ValidateAccessToken(string token);

    /// <summary>
    /// Generate a refresh token (random string, stored in DB)
    /// </summary>
    /// <returns>Refresh token string</returns>
    string GenerateRefreshToken();

    /// <summary>
    /// Compute SHA256 hash of a refresh token for storage
    /// </summary>
    /// <param name="refreshToken">Plain refresh token</param>
    /// <returns>Hex-encoded SHA256 hash</returns>
    string HashRefreshToken(string refreshToken);

    /// <summary>
    /// Get the configured access token lifetime
    /// </summary>
    /// <returns>Access token lifetime as TimeSpan</returns>
    TimeSpan GetAccessTokenLifetime();

    /// <summary>
    /// Get the configured refresh token lifetime
    /// </summary>
    /// <returns>Refresh token lifetime as TimeSpan</returns>
    TimeSpan GetRefreshTokenLifetime();
}

/// <summary>
/// Subject information for JWT generation
/// </summary>
public class SubjectInfo
{
    /// <summary>
    /// Subject ID (UUID)
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Email address (optional)
    /// </summary>
    public string? Email { get; set; }

}

/// <summary>
/// Result of JWT validation
/// </summary>
public class JwtValidationResult
{
    /// <summary>
    /// Whether the token is valid
    /// </summary>
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

    /// <summary>
    /// Create a successful result
    /// </summary>
    public static JwtValidationResult Success(JwtClaims claims) =>
        new() { IsValid = true, Claims = claims };

    /// <summary>
    /// Create a failed result
    /// </summary>
    public static JwtValidationResult Failure(string error, JwtValidationError errorCode) =>
        new()
        {
            IsValid = false,
            Error = error,
            ErrorCode = errorCode,
        };
}

/// <summary>
/// Extracted JWT claims
/// </summary>
public class JwtClaims
{
    /// <summary>
    /// Subject ID
    /// </summary>
    public Guid SubjectId { get; set; }

    /// <summary>
    /// Subject name
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Email address
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Role names
    /// </summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>
    /// Permissions
    /// </summary>
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
    /// JWT ID (jti claim)
    /// </summary>
    public string? JwtId { get; set; }

    /// <summary>
    /// Issued at
    /// </summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// Expires at
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// JWT validation error codes
/// </summary>
public enum JwtValidationError
{
    /// <summary>
    /// Token format is invalid
    /// </summary>
    InvalidFormat,

    /// <summary>
    /// Token signature is invalid
    /// </summary>
    InvalidSignature,

    /// <summary>
    /// Token has expired
    /// </summary>
    Expired,

    /// <summary>
    /// Token issuer is invalid
    /// </summary>
    InvalidIssuer,

    /// <summary>
    /// Token audience is invalid
    /// </summary>
    InvalidAudience,

    /// <summary>
    /// Token is not yet valid (nbf claim)
    /// </summary>
    NotYetValid,

    /// <summary>
    /// Required claims are missing
    /// </summary>
    MissingClaims,

    /// <summary>
    /// Unknown error
    /// </summary>
    Unknown,
}
