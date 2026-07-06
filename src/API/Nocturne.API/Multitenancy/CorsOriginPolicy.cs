namespace Nocturne.API.Multitenancy;

/// <summary>
/// Decides whether a browser <c>Origin</c> is allowed by the default CORS policy.
/// Because the platform serves tenants and public shares on open-ended wildcard
/// subdomains (<c>{slug}.{BaseDomain}</c> and <c>{token}.share.{BaseDomain}</c>),
/// a static allow-list can't work: the origin is validated against the configured
/// base domain instead. Matching is done on the parsed <see cref="Uri.Host"/> —
/// never a substring check — so look-alikes such as <c>basedomain.com.evil.com</c>
/// or <c>evilbasedomain.com</c> are rejected.
/// </summary>
public static class CorsOriginPolicy
{
    /// <summary>
    /// Returns true when <paramref name="origin"/> is the apex of
    /// <paramref name="baseDomain"/> or any subdomain of it (covering tenant and
    /// share hosts). When <paramref name="allowLocalhost"/> is set (development),
    /// loopback origins on any port are also allowed. Everything else — including
    /// loopback origins in production — is rejected.
    /// </summary>
    public static bool IsAllowed(string? origin, string baseDomain, bool allowLocalhost)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        // Only real browser CORS origins are meaningful here.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            return false;

        if (allowLocalhost && IsLoopback(host))
            return true;

        // BaseDomain may carry a port for local URL construction (e.g. localhost:1612);
        // the origin host never does. Compare hostnames only.
        var baseHost = baseDomain.Split(':')[0];
        if (string.IsNullOrEmpty(baseHost))
            return false;

        // Apex (exact match) or a subdomain at any depth. The leading dot enforces a
        // real label boundary, so "evilbasedomain.com" and "basedomain.com.evil.com"
        // do not match "basedomain.com".
        return host.Equals(baseHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{baseHost}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "[::1]"
        || host == "::1";
}
