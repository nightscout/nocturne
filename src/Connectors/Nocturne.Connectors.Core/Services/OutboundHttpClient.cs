using System.Net;
using Nocturne.Core.Models.Net;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
/// Builds the one kind of <see cref="HttpClient"/> a connector may construct for itself: a
/// short-lived client with its own cookie jar, on a transport pinned by
/// <see cref="PinnedConnector"/>.
/// </summary>
/// <remarks>
/// Why the vendor login flows are exempt from <see cref="IHttpClientFactory"/>: CareLink's Auth0 and
/// Tandem's OIDC sign-in carry session cookies across a multi-step redirect chain, and a factory
/// client shares its handler — and therefore its <see cref="CookieContainer"/> — between every
/// caller for the handler's lifetime, so two tenants signing in at once would trade session cookies.
/// They get an instance per attempt from here instead, so that "constructs its own client" still
/// means "is pinned".
/// <para>
/// No <see cref="LinkLocalGuardHandler"/>: these clients walk their own redirect chains, so the
/// policy is applied by the transport on each hop's connect rather than by a handler that only sees
/// the first URI.
/// </para>
/// </remarks>
public static class OutboundHttpClient
{
    /// <summary>
    /// A client with a private cookie jar, refusing link-local destinations at the socket. Pass
    /// <paramref name="transport"/> to substitute a fake; production leaves it null.
    /// </summary>
    public static HttpClient CreateIsolated(
        bool followRedirects,
        TimeSpan? timeout = null,
        HttpMessageHandler? transport = null)
    {
        var handler = transport ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ConnectCallback = new PinnedConnector(OutboundAddressPolicy.NotLinkLocal).ConnectAsync,
        };

        return new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(2),
        };
    }
}
