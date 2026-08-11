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

    /// <summary>
    /// Splits <see cref="BaseDomain"/> into host and optional port. Returns a null host when the
    /// value is empty, carries more than one colon without being a bracketed IPv6 literal, or
    /// names an unparseable port.
    /// </summary>
    /// <remarks>
    /// Here rather than at each call site so that everything building an origin from the base
    /// domain agrees on how it parses — a caller that mishandles the port would build an origin
    /// the browser never presents, and for an OAuth redirect URI that means authorize-time
    /// matching (which is byte-exact) fails, or matches something it should not.
    /// </remarks>
    public (string? Host, int? Port) SplitHostPort() => SplitHostPort(BaseDomain);

    /// <inheritdoc cref="SplitHostPort()"/>
    public static (string? Host, int? Port) SplitHostPort(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);

        value = value.Trim();

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
                return (null, null);

            var literal = value[..(close + 1)];
            var rest = value[(close + 1)..];
            if (rest.Length == 0)
                return (literal, null);
            if (rest[0] != ':' || !int.TryParse(rest[1..], out var literalPort) || literalPort is < 1 or > 65535)
                return (null, null);
            return (literal, literalPort);
        }

        var parts = value.Split(':');
        if (parts.Length == 1)
            return (parts[0], null);
        if (parts.Length != 2)
            return (null, null);
        if (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535)
            return (null, null);

        return (parts[0], port);
    }
}
