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
    /// <remarks>
    /// The base domain is normalized (scheme, path, port, and stray dots/wildcards
    /// stripped) and must resolve to a real multi-label host. A bare public suffix
    /// or single-label value (<c>com</c>, <c>localhost</c>, empty) is not usable as
    /// a credentialed-CORS base and disables base-domain matching entirely, so it
    /// can never widen the allow-list.
    /// </remarks>
    public static bool IsAllowed(string? origin, string baseDomain, bool allowLocalhost)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        // Only real browser CORS origins are meaningful here.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // A browser Origin is only ever scheme://host[:port] — never userinfo, a
        // path, a query, or a fragment. Reject anything richer; those are spoofing
        // attempts, not real Origin headers (e.g. "http://nocturne.run@evil.com",
        // "http://nocturne.run\@evil.com", "https://evil.com#.nocturne.run").
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (uri.AbsolutePath is not ("" or "/"))
            return false;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            return false;

        if (allowLocalhost && IsLoopback(host))
            return true;

        var baseHost = NormalizeBaseHost(baseDomain);

        // A credentialed CORS base must be a real multi-label domain. A bare public
        // suffix or single-label host ("com", "localhost", "") is not usable —
        // otherwise every "*.com" origin would be trusted with credentials. When the
        // base is unusable, base-domain matching is disabled (fail closed); in
        // development loopback origins are still admitted by the branch above.
        if (baseHost.Length == 0 || !baseHost.Contains('.'))
            return false;

        // Apex (exact match) or a subdomain at any depth. The leading dot enforces a
        // real label boundary, so "evilbasedomain.com" and "basedomain.com.evil.com"
        // do not match "basedomain.com".
        return host.Equals(baseHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{baseHost}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a configured base-domain value to a bare host suitable for CORS
    /// origin matching. Strips a leading <c>http(s)://</c> scheme, any path or
    /// trailing slash, a leading <c>*.</c> wildcard, a port, and leading/trailing
    /// dots. Returns an empty string when the input is null/blank or normalizes to
    /// nothing. The result is not guaranteed to be a valid base — callers must still
    /// require a dot (multi-label) before trusting it (see <see cref="IsAllowed"/>).
    /// </summary>
    /// <remarks>
    /// Tolerates misformatted configuration (<c>https://nocturne.run</c>,
    /// <c>nocturne.run/</c>, <c>.nocturne.run</c>, <c>nocturne.run.</c>,
    /// <c>*.nocturne.run</c>) so an operator typo doesn't silently disable
    /// cross-origin CORS.
    /// </remarks>
    public static string NormalizeBaseHost(string? baseDomain)
    {
        if (string.IsNullOrWhiteSpace(baseDomain))
            return "";

        var value = baseDomain.Trim();

        // Strip a leading scheme, e.g. "https://nocturne.run" -> "nocturne.run".
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
            value = value[(schemeIndex + 3)..];

        // Drop any path/trailing slash, e.g. "nocturne.run/" -> "nocturne.run".
        var slashIndex = value.IndexOf('/');
        if (slashIndex >= 0)
            value = value[..slashIndex];

        // Drop a leading wildcard label, e.g. "*.nocturne.run" -> ".nocturne.run".
        if (value.StartsWith('*'))
            value = value[1..];

        // Drop a port, e.g. "nocturne.run:1612" -> "nocturne.run". Hostnames never
        // contain a colon (IPv6 literals aren't valid base domains here).
        var colonIndex = value.IndexOf(':');
        if (colonIndex >= 0)
            value = value[..colonIndex];

        // Drop leading/trailing dots, e.g. ".nocturne.run" / "nocturne.run." ->
        // "nocturne.run".
        return value.Trim('.');
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "[::1]"
        || host == "::1";
}
