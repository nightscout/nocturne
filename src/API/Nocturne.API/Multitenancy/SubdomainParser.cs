namespace Nocturne.API.Multitenancy;

/// <summary>
/// Extracts the tenant subdomain slug from a request hostname relative to the
/// configured base domain. Shared by <see cref="TenantResolutionMiddleware"/>
/// and the on-demand TLS authorization endpoint so both agree on what counts
/// as a tenant subdomain.
/// </summary>
public static class SubdomainParser
{
    /// <summary>
    /// Returns the subdomain slug for <paramref name="hostname"/> under
    /// <paramref name="baseDomain"/>, or null when the host is the apex domain,
    /// is empty, or does not belong to the base domain. Ports are ignored on
    /// both sides (BaseDomain may carry a port for local URL construction).
    /// </summary>
    public static string? Extract(string hostname, string baseDomain)
    {
        if (string.IsNullOrEmpty(hostname) || string.IsNullOrEmpty(baseDomain))
            return null;

        var host = hostname.Split(':')[0];
        var baseDomainHost = baseDomain.Split(':')[0];

        if (!host.EndsWith($".{baseDomainHost}", StringComparison.OrdinalIgnoreCase))
            return null;

        var subdomain = host[..^(baseDomainHost.Length + 1)];
        return string.IsNullOrEmpty(subdomain) ? null : subdomain;
    }

    /// <summary>The label that marks a public-share host: <c>{token}.share.{baseDomain}</c>.</summary>
    public const string ShareSubdomainLabel = "share";

    /// <summary>
    /// Detects the public-share host form <c>{token}.share</c> (the subdomain left of the base
    /// domain) and extracts the token. Returns false for ordinary tenant slugs, empty tokens,
    /// or nested forms — slugs and tokens never contain dots. The token is lower-cased because
    /// hostnames are case-insensitive and generated tokens are always lowercase.
    /// </summary>
    /// <remarks>
    /// Shared so that everything deciding "is this the share host" agrees. A share grants
    /// anonymous read-only access, so a caller that treats a share host as an ordinary tenant
    /// host hands out more than the link does: <see cref="TenantResolutionMiddleware"/> uses this
    /// to resolve the tenant by token, and <c>ScalarAuthProvider</c> uses it to refuse to register
    /// an OAuth client or mint a token there.
    /// </remarks>
    public static bool TryExtractShareToken(string subdomain, out string token)
    {
        const string suffix = "." + ShareSubdomainLabel;
        if (subdomain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            token = subdomain[..^suffix.Length].ToLowerInvariant();
            if (token.Length > 0 && !token.Contains('.'))
                return true;
        }

        token = string.Empty;
        return false;
    }
}
