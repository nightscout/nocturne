using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nocturne.Core.Constants;

namespace Nocturne.API.Authorization;

/// <summary>
/// Resolves the address of the client a request was made for: the one the SvelteKit server carries
/// in <see cref="ServiceNames.Headers.ClientIp"/> when its signature verifies, otherwise the
/// address the connection came from.
/// </summary>
/// <remarks>
/// The browser's address does not always reach the API: a page rendered by the SvelteKit server
/// calls the API from its own container, so every sign-in made through a remote function arrives
/// from that container, and one person's five mistyped attempts spend everyone else's window. The
/// header is honoured only when <see cref="ServiceNames.Headers.ClientIpSignature"/> shows the
/// sender holds the instance key: the header alone is writable by anyone the gateway admits, and
/// the peer address is no evidence either, since browser traffic and SSR traffic arrive over the
/// same internal network. Signing the address rather than presenting the key's digest keeps a
/// reusable credential off user-originated requests, so a captured signature names its own address
/// and nothing else.
/// <para>
/// An unsigned request resolves to <c>Connection.RemoteIpAddress</c>, which is only as good as the
/// edge that wrote <c>X-Forwarded-For</c>; see
/// <see cref="Extensions.NocturneForwardedHeadersExtensions"/> for what the API consumes and what
/// the edge must present for it to name a caller.
/// </para>
/// <para>
/// The address is returned whole. Rate limiting reduces it to a partition
/// (<see cref="RateLimiting.ClientRateLimitKey"/>); audit rows, sessions and the other records of
/// who did something keep it as presented. Recorders read it through
/// <see cref="Extensions.HttpContextExtensions.GetClientAddress"/>.
/// </para>
/// </remarks>
public sealed class ClientAddressResolver
{
    private readonly byte[] _signingKey;

    public ClientAddressResolver(IConfiguration configuration)
    {
        _signingKey = Encoding.UTF8.GetBytes(InstanceKeyDigest.ResolveKey(configuration));
    }

    /// <summary>
    /// The signed end-user address when one is presented and verifies, otherwise the connection's
    /// address, or null when the request has neither.
    /// </summary>
    public IPAddress? Resolve(HttpContext context) =>
        SignedAddress(context.Request) ?? context.Connection.RemoteIpAddress;

    private IPAddress? SignedAddress(HttpRequest request)
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
