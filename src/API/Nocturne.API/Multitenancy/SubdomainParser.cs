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
}
