namespace Nocturne.API.Multitenancy;

/// <summary>
/// Platform-wide base domain configuration.
/// Used for subdomain tenant resolution, WebAuthn RP ID derivation, and URL construction.
/// Bound from the flat "BASE_DOMAIN" configuration key (env var: BASE_DOMAIN).
/// </summary>
public class BaseDomainOptions
{
    public const string ConfigKey = "BASE_DOMAIN";

    /// <summary>
    /// Base domain for the platform, e.g. "nocturnecgm.com" or "localhost:1612".
    /// Requests to rhys.nocturnecgm.com resolve tenant "rhys".
    /// </summary>
    public string BaseDomain { get; set; } = "";

    /// <summary>
    /// Public origin of the deployment apex: "https://{BaseDomain}", or null when
    /// no base domain is configured. The platform serves HTTPS at the edge
    /// (WebAuthn already requires it), so the scheme is not configurable.
    /// Used for OIDC redirect URIs, invite links, Pushover callbacks, and other
    /// externally visible URLs.
    /// </summary>
    public string? PublicOrigin =>
        string.IsNullOrEmpty(BaseDomain) ? null : $"https://{BaseDomain}";
}
