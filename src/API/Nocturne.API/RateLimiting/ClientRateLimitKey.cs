using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Nocturne.API.Authorization;
using Nocturne.Core.Constants;

namespace Nocturne.API.RateLimiting;

/// <summary>
/// Resolves the calling client that the address-keyed rate-limit policies partition on.
/// </summary>
/// <remarks>
/// The browser's address does not always reach the API: a page rendered by the SvelteKit server
/// calls the API from its own container, so an endpoint reached through a remote function (guest
/// code activation, TOTP sign-in) sees one address for every user of the deployment, and one
/// person's five mistyped attempts spend everyone else's window. The address is therefore carried
/// in <see cref="ServiceNames.Headers.ClientIp"/> and honoured only when
/// <see cref="ServiceNames.Headers.ClientIpSignature"/> shows the sender holds the instance key:
/// the header alone is writable by anyone the gateway admits, and the peer address is no evidence
/// either, since browser traffic and SSR traffic arrive over the same internal network. Signing the
/// address rather than presenting the key's digest keeps a reusable credential off user-originated
/// requests — a captured signature buckets its own address and nothing else.
/// <para>
/// An unsigned request partitions on <c>Connection.RemoteIpAddress</c>, which is only as good as
/// the edge that wrote <c>X-Forwarded-For</c> — see
/// <see cref="Extensions.NocturneForwardedHeadersExtensions"/> for what the API consumes and what the edge
/// must present for it to name a caller. The ceilings that bound abuse are on the policies
/// themselves in <see cref="Extensions.ServiceRegistrationExtensions"/>.
/// </para>
/// <para>
/// The partition is the client, not the address: an IPv6 address counts as its network (see
/// <see cref="IPv6ClientPrefixLength"/>), and an IPv4-mapped address as the IPv4 address it carries,
/// since a dual-stack socket reports an IPv4 peer in that form. The signature covers the string as
/// presented, so the address is reduced only after it verifies. Only the partition is reduced; what
/// audit rows and session lists record is the full address, read from the connection.
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

    private readonly byte[] _signingKey;

    public ClientRateLimitKey(IConfiguration configuration)
    {
        _signingKey = Encoding.UTF8.GetBytes(InstanceKeyDigest.ResolveKey(configuration));
    }

    /// <summary>
    /// The partition key for this request: the client of the signed end-user address when one is
    /// presented, otherwise of the address the connection came from.
    /// </summary>
    public string Resolve(HttpContext context) =>
        (SignedClientAddress(context.Request) ?? context.Connection.RemoteIpAddress) is { } address
            ? Client(address)
            : Unknown;

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

    private IPAddress? SignedClientAddress(HttpRequest request)
    {
        if (_signingKey.Length == 0)
            return null;

        var presented = request.Headers[ServiceNames.Headers.ClientIp].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(presented))
            return null;

        var signature = request.Headers[ServiceNames.Headers.ClientIpSignature].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(signature))
            return null;

        if (!IPAddress.TryParse(presented, out var address))
            return null;

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(presented)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(expected))
            ? address
            : null;
    }
}
