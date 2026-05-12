namespace Nocturne.API.Multitenancy;

/// <summary>
/// Platform-wide base domain configuration.
/// Used for subdomain tenant resolution, WebAuthn RP ID derivation, and URL construction.
/// Bound from the flat "BaseDomain" configuration key (env var: BaseDomain).
/// </summary>
public class BaseDomainOptions
{
    public const string ConfigKey = "BASE_DOMAIN";

    /// <summary>
    /// Base domain for the platform, e.g. "nocturnecgm.com" or "localhost:1612".
    /// Requests to rhys.nocturnecgm.com resolve tenant "rhys".
    /// </summary>
    public string BaseDomain { get; set; } = "";
}
