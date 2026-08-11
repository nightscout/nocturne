using System.Net;
using Nocturne.Core.Models.Net;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
/// Builds the one kind of <see cref="HttpClient"/> a connector may construct for itself: a
/// short-lived client with its own cookie jar, on a transport pinned by
/// <see cref="PinnedConnector"/>.
/// </summary>
/// <remarks>
/// Nearly every connector client comes from <see cref="IHttpClientFactory"/> via
/// <c>ConfigureConnectorClient</c>, and <c>ConnectorClientGuardCoverageTests</c> holds that line.
/// The vendor login flows are the exception: CareLink's Auth0 and Tandem's OIDC sign-in both carry
/// session cookies across a multi-step redirect chain, and a factory client shares its handler —
/// and therefore its <see cref="CookieContainer"/> — between every caller for the handler's
/// lifetime, so two tenants signing in at once would trade session cookies. They get an instance
/// per attempt instead, from here, so that "constructs its own client" still means "is pinned".
/// <para>
/// No <see cref="LinkLocalGuardHandler"/>: these clients walk their own redirect chains, and the
/// address policy is applied by the transport on each hop's connect rather than by a handler that
/// only sees the first URI.
/// </para>
/// </remarks>
public static class OutboundHttpClient
{
    /// <summary>
    /// A client with a private cookie jar, refusing link-local destinations at the socket.
    /// </summary>
    /// <param name="followRedirects">Whether the transport follows 3xx itself.</param>
    /// <param name="timeout">Request timeout. Defaults to 2 minutes.</param>
    /// <param name="transport">
    /// Overrides the pinned transport. Tests pass a fake; production leaves it null.
    /// </param>
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
