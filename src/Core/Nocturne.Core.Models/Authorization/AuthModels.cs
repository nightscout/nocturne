namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Authentication result from auth handlers
/// </summary>
public class AuthResult
{
    /// <summary>
    /// Whether authentication succeeded
    /// </summary>
    public bool Succeeded { get; set; }

    /// <summary>
    /// Authentication context if succeeded
    /// </summary>
    public AuthContext? AuthContext { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Whether the handler should skip and let the next handler try
    /// </summary>
    public bool ShouldSkip { get; set; }

    /// <summary>
    /// Create a successful auth result
    /// </summary>
    public static AuthResult Success(AuthContext context) =>
        new() { Succeeded = true, AuthContext = context };

    /// <summary>
    /// Create a failed auth result
    /// </summary>
    public static AuthResult Failure(string error) =>
        new() { Succeeded = false, Error = error };

    /// <summary>
    /// Create a skip result (handler didn't find credentials it can handle)
    /// </summary>
    public static AuthResult Skip() =>
        new() { Succeeded = false, ShouldSkip = true };
}

/// <summary>
/// Authentication context containing user identity and permissions
/// </summary>
public class AuthContext
{
    /// <summary>
    /// Whether the user is authenticated
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Type of authentication used
    /// </summary>
    public AuthType AuthType { get; set; }

    /// <summary>
    /// Subject (user/device) identifier
    /// </summary>
    public Guid? SubjectId { get; set; }

    /// <summary>
    /// Subject name for display
    /// </summary>
    public string? SubjectName { get; set; }

    /// <summary>
    /// Email address (from OIDC or subject record)
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// External OIDC subject ID
    /// </summary>
    public string? OidcSubjectId { get; set; }

    /// <summary>
    /// OIDC issuer URL
    /// </summary>
    public string? OidcIssuer { get; set; }

    /// <summary>
    /// Resolved Shiro-style permissions
    /// </summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>
    /// Assigned role names
    /// </summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>
    /// OAuth2 scopes
    /// </summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Token ID (if token-based auth)
    /// </summary>
    public Guid? TokenId { get; set; }

    /// <summary>
    /// Raw token string (for legacy compatibility)
    /// </summary>
    public string? RawToken { get; set; }

    /// <summary>
    /// When the authentication expires
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Create an unauthenticated context
    /// </summary>
    public static AuthContext Unauthenticated() =>
        new() { IsAuthenticated = false, AuthType = AuthType.None };

    /// <summary>
    /// Check if this context has a specific permission
    /// </summary>
    public bool HasPermission(string permission)
    {
        // Check for admin permission
        if (Permissions.Contains("*"))
            return true;

        // Check exact match
        if (Permissions.Contains(permission))
            return true;

        // Check hierarchical wildcards
        var parts = permission.Split(':');
        for (int i = 1; i <= parts.Length; i++)
        {
            var wildcardPermission = string.Join(":", parts.Take(i)) + ":*";
            if (Permissions.Contains(wildcardPermission))
                return true;
        }

        // Check *:*:action pattern
        if (parts.Length >= 3)
        {
            var actionWildcard = $"*:*:{parts[^1]}";
            if (Permissions.Contains(actionWildcard))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if this context has a specific role
    /// </summary>
    public bool HasRole(string role) =>
        Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Types of authentication
/// </summary>
public enum AuthType
{
    /// <summary>
    /// No authentication
    /// </summary>
    None,

    /// <summary>
    /// OIDC opaque token
    /// </summary>
    OidcToken,

    /// <summary>
    /// Legacy JWT token (self-issued)
    /// </summary>
    LegacyJwt,

    /// <summary>
    /// Legacy access token
    /// </summary>
    LegacyAccessToken,

    /// <summary>
    /// API secret (SHA1 hash)
    /// </summary>
    ApiSecret,

    /// <summary>
    /// Session cookie
    /// </summary>
    SessionCookie,

    /// <summary>
    /// OAuth 2.0 access token (JWT with scope claims)
    /// </summary>
    OAuthAccessToken
}
