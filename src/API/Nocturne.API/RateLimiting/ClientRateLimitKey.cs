using System.Net;
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
/// This settles who shares a partition, not what a partition may spend: an unsigned request still
/// partitions on <c>Connection.RemoteIpAddress</c>, which <c>UseForwardedHeaders</c> takes from
/// <c>X-Forwarded-For</c> with no trusted-proxy list, so a caller rotating that header still gets
/// a fresh window. The ceilings that do bound abuse are named on the policies themselves in
/// <see cref="Extensions.ServiceRegistrationExtensions"/>.
/// </para>
/// </remarks>
public sealed class ClientRateLimitKey
{
    private const string Unknown = "unknown";

    private readonly byte[] _signingKey;

    public ClientRateLimitKey(IConfiguration configuration)
    {
        _signingKey = Encoding.UTF8.GetBytes(InstanceKeyDigest.ResolveKey(configuration));
    }

    /// <summary>
    /// The partition key for this request: the signed end-user address when one is presented,
    /// otherwise the address the connection came from.
    /// </summary>
    public string Resolve(HttpContext context) =>
        SignedClientAddress(context.Request)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? Unknown;

    private string? SignedClientAddress(HttpRequest request)
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
            ? address.ToString()
            : null;
    }
}
