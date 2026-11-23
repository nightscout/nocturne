using System.Net;
using System.Net.Sockets;
using Nocturne.API.Authorization;

namespace Nocturne.API.RateLimiting;

/// <summary>
/// Reduces the client address the address-keyed rate-limit policies count against to its partition.
/// </summary>
/// <remarks>
/// Which address that is, and why a header may name it, is settled by
/// <see cref="ClientAddressResolver"/>.
/// <para>
/// The partition is the client, not the address: an IPv6 address counts as its network (see
/// <see cref="IPv6ClientPrefixLength"/>), and an IPv4-mapped address as the IPv4 address it carries,
/// since a dual-stack socket reports an IPv4 peer in that form. The signature covers the string as
/// presented, so the address is reduced only after it verifies. The ceilings that bound abuse are on
/// the policies themselves in <see cref="Extensions.ServiceRegistrationExtensions"/>.
/// </para>
/// </remarks>
public sealed class ClientRateLimitKey
{
    private const string Unknown = "unknown";

    /// <summary>
    /// The IPv6 prefix length one client is counted as.
    /// </summary>
    /// <remarks>
    /// A subscriber is assigned at least a /64 (RFC 6177; SLAAC needs one per link), and any address
    /// in it is theirs to use, so a limit on the whole address would hand one client 2^64 windows.
    /// Counting the /64 puts the people on one IPv6 network in one bucket, as an IPv4 household
    /// behind one NAT already is.
    /// </remarks>
    internal const int IPv6ClientPrefixLength = 64;

    private readonly ClientAddressResolver _address;

    public ClientRateLimitKey(IConfiguration configuration)
    {
        _address = new ClientAddressResolver(configuration);
    }

    /// <summary>
    /// The partition key for this request: the client of the address
    /// <see cref="ClientAddressResolver.Resolve"/> names.
    /// </summary>
    public string Resolve(HttpContext context) =>
        _address.Resolve(context) is { } address ? Client(address) : Unknown;

    private static string Client(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        bytes.AsSpan(IPv6ClientPrefixLength / 8).Clear();
        return new IPNetwork(new IPAddress(bytes), IPv6ClientPrefixLength).ToString();
    }
}
