using System.Net;
using System.Net.Sockets;

namespace Nocturne.API.Services.Alerts.Webhooks;

/// <summary>
/// Decides whether a webhook URL is a legitimate external destination.
/// </summary>
/// <remarks>
/// Webhook URLs are supplied by whoever is signed in, and the API sends the request from
/// inside the deployment's network, where the database, the other services and the cloud
/// metadata endpoint are all reachable by name. Without this check a member can use the
/// webhook sender to reach those, and the returned per-URL success or failure reports back
/// what it found. Only <c>http</c> and <c>https</c> to a non-loopback, non-private,
/// publicly routable address are allowed.
/// <para>
/// Hostnames are resolved here and every resolved address is checked, so a name that
/// points at a private address is rejected too. A name whose DNS answer changes between
/// this check and the send could still slip through; closing that needs the send to be
/// pinned to the address that was checked, which the shared
/// <see cref="System.Net.Http.HttpClient"/> does not currently support.
/// </para>
/// </remarks>
public static class WebhookDestination
{
    /// <summary>
    /// Returns true when <paramref name="url"/> is a well-formed absolute http(s) URL whose
    /// every resolved address is publicly routable.
    /// </summary>
    /// <remarks>
    /// Asynchronous because a hostname costs a DNS round trip, and an alert fans out to every
    /// URL on the rule: resolving synchronously would block a thread-pool thread per
    /// destination for as long as the resolver takes.
    /// </remarks>
    public static async Task<bool> IsAllowedAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var addresses = await ResolveAddressesAsync(uri, ct);

        // Fail closed on an empty result. A name this process cannot resolve is not
        // necessarily unreachable — the HTTP stack may resolve it by other means — so
        // "no addresses to check" must deny rather than allow.
        if (addresses.Count == 0)
            return false;

        return addresses.All(IsPubliclyRoutable);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAddressesAsync(Uri uri, CancellationToken ct)
    {
        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
            return [literal];

        try
        {
            return await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private static bool IsPubliclyRoutable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
            return IsPubliclyRoutable(address.MapToIPv4());

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return address.AddressFamily == AddressFamily.InterNetworkV6 && !IsIPv6UniqueLocal(address);

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 => false,                                     // 0.0.0.0/8 "this network"
            10 => false,                                    // 10.0.0.0/8 private
            127 => false,                                   // loopback
            169 when octets[1] == 254 => false,             // 169.254.0.0/16 link-local (cloud metadata)
            172 when octets[1] is >= 16 and <= 31 => false, // 172.16.0.0/12 private
            192 when octets[1] == 168 => false,             // 192.168.0.0/16 private
            100 when octets[1] is >= 64 and <= 127 => false, // 100.64.0.0/10 carrier NAT
            >= 224 => false,                                // multicast and reserved
            _ => true,
        };
    }

    /// <summary>fc00::/7 — the IPv6 equivalent of the private ranges.</summary>
    private static bool IsIPv6UniqueLocal(IPAddress address) =>
        (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
}
