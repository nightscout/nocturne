namespace Nocturne.Core.Models.Configuration;

/// <summary>
/// JWT authentication configuration options.
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Secret key for signing JWT tokens. Derived automatically from instance key
    /// via PostConfigure if not explicitly set.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// JWT token issuer.
    /// </summary>
    public string Issuer { get; set; } = "nocturne";

    /// <summary>
    /// JWT token audience.
    /// </summary>
    public string Audience { get; set; } = "nocturne-api";

    /// <summary>
    /// Access token lifetime in minutes.
    /// </summary>
    public int AccessTokenLifetimeMinutes { get; set; } = 15;

    /// <summary>
    /// Refresh token lifetime in days.
    /// </summary>
    public int RefreshTokenLifetimeDays { get; set; } = 7;

    /// <summary>
    /// Refresh token length in bytes (will be base64 encoded).
    /// </summary>
    public int RefreshTokenLengthBytes { get; set; } = 64;
}
