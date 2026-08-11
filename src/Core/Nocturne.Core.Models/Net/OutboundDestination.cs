using System.Net;
using System.Net.Sockets;

namespace Nocturne.Core.Models.Net;

/// <summary>
/// Classifies a URL the server itself is about to fetch, for the paths where the URL was
/// supplied by whoever was signed in.
/// </summary>
/// <remarks>
/// The API sends these requests from inside the deployment's network, where the database, the
/// sibling services and the cloud metadata endpoint are all reachable by name, and each caller
/// learns the outcome — a webhook test reports per-URL success, a connector reports its sync
/// status. That makes a user-supplied URL a request-forgery primitive.
/// <para>
/// Two different properties are needed, because the sinks have different legitimate ranges; which
/// range belongs to which sink is on <see cref="OutboundAddressPolicy"/>.
/// </para>
/// <para>
/// Every resolved address is checked, so a name pointing at a refused address is refused too; the
/// socket the request then opens is pinned to a checked address by <see cref="PinnedConnector"/>.
/// Resolution failure is refused rather than allowed: a name this process cannot resolve may still
/// resolve for the HTTP stack.
/// </para>
/// </remarks>
public static class OutboundDestination
{
    /// <summary>
    /// Resolves hostnames to addresses. Replaceable so tests do not depend on the machine's DNS
    /// — a resolver that wildcards NXDOMAIN would otherwise flip the unresolvable-host cases,
    /// and an uncached lookup is slow enough to matter in a unit suite.
    /// </summary>
    public delegate ValueTask<IReadOnlyList<IPAddress>> AddressResolver(
        string host, CancellationToken ct);

    /// <summary>The default resolver: the machine's DNS, with failure reported as no addresses.</summary>
    public static readonly AddressResolver SystemResolver = async (host, ct) =>
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    };

    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) URL whose every resolved address
    /// is publicly routable — not loopback, private, carrier-NAT, link-local or multicast.
    /// </summary>
    public static Task<bool> IsPubliclyRoutableAsync(
        string url, CancellationToken ct = default, AddressResolver? resolver = null) =>
        IsAllowedAsync(url, OutboundAddressPolicy.PubliclyRoutable, ct, resolver);

    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) URL and no resolved address is
    /// link-local. Private and loopback addresses are permitted — see
    /// <see cref="OutboundAddressPolicy.NotLinkLocal"/>.
    /// </summary>
    public static Task<bool> IsNotLinkLocalAsync(
        string url, CancellationToken ct = default, AddressResolver? resolver = null) =>
        IsAllowedAsync(url, OutboundAddressPolicy.NotLinkLocal, ct, resolver);

    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) URL whose every resolved address
    /// satisfies <paramref name="policy"/>.
    /// </summary>
    public static async Task<bool> IsAllowedAsync(
        string url,
        OutboundAddressPolicy policy,
        CancellationToken ct = default,
        AddressResolver? resolver = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var addresses = await ResolveAsync(uri.DnsSafeHost, ct, resolver);

        // Fail closed on an empty result: "nothing to check" must deny, not allow.
        if (addresses.Count == 0)
            return false;

        return addresses.All(address => IsAllowed(address, policy));
    }

    /// <summary>
    /// The single place an address is judged, shared by the URL checks above and by
    /// <see cref="PinnedConnector"/>.
    /// </summary>
    public static bool IsAllowed(IPAddress address, OutboundAddressPolicy policy) => policy switch
    {
        OutboundAddressPolicy.PubliclyRoutable => IsPubliclyRoutable(address),
        OutboundAddressPolicy.NotLinkLocal => !IsLinkLocal(address),
        _ => false,
    };

    /// <summary>
    /// Addresses for <paramref name="host"/>, which may be a name or an IP literal. Brackets around
    /// an IPv6 literal have to be gone already — <see cref="Uri.DnsSafeHost"/> and
    /// <see cref="DnsEndPoint.Host"/> both strip them.
    /// </summary>
    public static async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host, CancellationToken ct = default, AddressResolver? resolver = null)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];

        return await (resolver ?? SystemResolver)(host, ct);
    }

    /// <summary>
    /// AWS's IPv6 instance metadata address. Unique-local rather than link-local, so the
    /// prefix checks below do not reach it.
    /// </summary>
    private static readonly IPAddress Ec2IPv6Metadata = IPAddress.Parse("fd00:ec2::254");

    /// <summary>
    /// 169.254.0.0/16 and fe80::/10 — including the cloud instance metadata endpoint, which is
    /// the highest-value target reachable from inside a deployment.
    /// </summary>
    /// <remarks>
    /// Plus <see cref="Ec2IPv6Metadata"/>, which serves the same credentials over IPv6. Named
    /// individually rather than by refusing fc00::/7, because a connector pointed at a
    /// self-hosted Nightscout on an IPv6 ULA network is the same ordinary setup this check
    /// deliberately allows over the IPv4 private ranges — refusing the whole prefix would break
    /// it to reach one address.
    /// </remarks>
    private static bool IsLinkLocal(IPAddress address)
    {
        address = Unwrap(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.Equals(Ec2IPv6Metadata);

        var octets = address.GetAddressBytes();
        return octets[0] == 169 && octets[1] == 254;
    }

    private static bool IsPubliclyRoutable(IPAddress address)
    {
        address = Unwrap(address);

        if (IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return address.AddressFamily == AddressFamily.InterNetworkV6 && !IsIPv6UniqueLocal(address);

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 => false,                                       // 0.0.0.0/8 "this network"
            10 => false,                                      // 10.0.0.0/8 private
            127 => false,                                      // loopback
            169 when octets[1] == 254 => false,                // 169.254.0.0/16 link-local (cloud metadata)
            172 when octets[1] is >= 16 and <= 31 => false,    // 172.16.0.0/12 private
            192 when octets[1] == 168 => false,                // 192.168.0.0/16 private
            192 when octets[1] == 0 && octets[2] == 0 => false, // 192.0.0.0/24 IETF protocol assignments
            198 when octets[1] is 18 or 19 => false,           // 198.18.0.0/15 benchmarking
            100 when octets[1] is >= 64 and <= 127 => false,   // 100.64.0.0/10 carrier NAT
            >= 224 => false,                                   // multicast and reserved
            _ => true,
        };
    }

    /// <summary>
    /// Reduces an IPv6 address that carries an embedded IPv4 address to that IPv4 address, so the
    /// v4 range checks apply. Covers IPv4-mapped (::ffff:a.b.c.d), IPv4-compatible (::a.b.c.d),
    /// NAT64 (64:ff9b::/96), 6to4 (2002::/16, IPv4 in bytes 2-5) and Teredo (2001::/32, IPv4 in
    /// bytes 12-15, stored inverted).
    /// </summary>
    /// <remarks>
    /// Only the mapped form is routed to the embedded IPv4 address by a stock stack; the rest need
    /// a relay or a translator on the path. They are unwrapped anyway because whether one of them
    /// reaches <c>169.254.169.254</c> is a property of the network the deployment happens to sit
    /// on, and this classifier does not get to see that.
    /// </remarks>
    private static IPAddress Unwrap(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address;

        var bytes = address.GetAddressBytes();

        // 6to4: 2002:V4ADDR::/48
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return new IPAddress(bytes[2..6]);

        // Teredo: 2001:0000:SERVER:FLAGS:PORT:CLIENTV4, client v4 one's-complemented.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            return new IPAddress([.. bytes[12..16].Select(b => (byte)~b)]);

        // NAT64: 64:ff9b::V4ADDR (and the local-use 64:ff9b:1::/48 prefix's /96 suffix).
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B)
            return new IPAddress(bytes[12..16]);

        // IPv4-compatible: ::a.b.c.d, the deprecated ::/96 form, which is not IsIPv4MappedToIPv6.
        // :: and ::1 are the unspecified and loopback addresses rather than a wrapped IPv4 one, and
        // both are already judged in their own right.
        if (bytes[..12].All(b => b == 0) && (bytes[12] != 0 || bytes[13] != 0 || bytes[14] != 0))
            return new IPAddress(bytes[12..16]);

        return address;
    }

    /// <summary>fc00::/7 — the IPv6 equivalent of the private ranges.</summary>
    private static bool IsIPv6UniqueLocal(IPAddress address) =>
        (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
}
