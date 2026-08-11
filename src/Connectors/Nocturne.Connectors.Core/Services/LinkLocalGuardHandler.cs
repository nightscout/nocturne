using System.Net;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.Net;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
/// Refuses a connector request whose target resolves to a link-local address, on every hop.
/// </summary>
/// <remarks>
/// Connector base URLs come from tenant configuration — a value someone signed in supplied
/// through <c>PUT /api/v4/connectors/config/{connectorName}</c> — and the request is sent from
/// inside the deployment's network, with the outcome reported back through connector status. That
/// makes the connector fetch a request-forgery primitive.
/// <para>
/// Only link-local is refused, not every private range. A self-hosted deployment legitimately
/// points a connector with a member-supplied base URL — Nightscout, remote Nocturne, MyLife — at a
/// private address, and a Nightscout on the same Docker network or LAN is the ordinary migration
/// setup, so requiring public routability would break real installs. <c>169.254.169.254</c> and
/// its neighbours have no legitimate connector use and are where cloud instance credentials live,
/// so that range is refused for every tenant regardless of what was configured.
/// </para>
/// <para>
/// <b>Redirects are followed here, not by the transport.</b> Checking only
/// <see cref="HttpRequestMessage.RequestUri"/> and letting the primary handler follow 3xx would
/// leave the guard trivially bypassable: an allowed host answering <c>307</c> with a
/// <c>Location</c> of <c>http://169.254.169.254/…</c> would be followed from inside the network
/// beneath this handler, and connector status would report the result. Simply setting
/// <c>AllowAutoRedirect = false</c> would close that but also break a legitimate config — a
/// Nightscout behind an <c>http</c>-to-<c>https</c> redirect is ordinary — so the transport has
/// redirects disabled and this handler follows them itself, re-checking each hop.
/// </para>
/// <para>
/// Request headers are reduced to <see cref="SafeCrossOriginHeaders"/> when a redirect crosses to
/// a different origin: a tenant-configured host that redirects elsewhere must not hand that host's
/// credentials to the redirect target. An allowlist rather than a list of known credential
/// headers, so a connector added later that authenticates with some header nobody thought of does
/// not leak it on the first cross-origin hop.
/// </para>
/// <para>
/// <b>Deliberately more permissive than <see cref="System.Net.Http.HttpClient"/> on one axis:</b>
/// it refuses an automatic https-to-http redirect outright, and this does not. Plain <c>http</c>
/// is an accepted connector configuration by design — a Nightscout on a LAN is normally served
/// over it — so the downgrade grants nothing that configuring <c>http</c> directly would not, and
/// refusing it would break a host that redirects between its own schemes. Every hop is still
/// address-checked, which is the property that matters here.
/// </para>
/// <para>
/// Registered by <c>HttpClientExtensions.ConfigureConnectorClient</c>. Not every connector client
/// goes through that extension automatically — an installer calling <c>AddHttpClient</c> without
/// it gets no guard and transport-level redirects, which is how the MyLife connector was left
/// outside this for a while. <c>ConnectorClientGuardCoverageTests</c> walks every installer and
/// fails if one registers a client without it. See the note on
/// <see cref="OutboundDestination"/> about the residual gap between resolving a name and
/// connecting to it.
/// </para>
/// </remarks>
public sealed class LinkLocalGuardHandler : DelegatingHandler
{
    /// <summary>
    /// Hop ceiling, matching <see cref="System.Net.Http.HttpClient"/>'s default
    /// <c>MaxAutomaticRedirections</c>.
    /// </summary>
    private const int MaxRedirects = 50;

    /// <summary>
    /// The only request headers carried across a cross-origin redirect. Compared
    /// case-insensitively, because header names are.
    /// </summary>
    /// <remarks>
    /// An allowlist, not a list of credential headers to strip. A denylist would have to name
    /// every scheme any connector might ever authenticate with — <c>Authorization</c>,
    /// <c>api-secret</c>, <c>Cookie</c> today, but a connector added later using
    /// <c>X-Api-Key</c> would leak it silently on the first cross-origin hop, and nothing would
    /// fail. These four are content negotiation and identification: none of them is a credential,
    /// and a connector needing anything else on a redirect target it did not configure is not a
    /// case worth opening the hole for.
    /// </remarks>
    private static readonly string[] SafeCrossOriginHeaders =
    [
        "Accept",
        "Accept-Encoding",
        "Accept-Language",
        "User-Agent",
    ];

    /// <summary>
    /// The only <see cref="HttpContent"/> headers carried across a cross-origin redirect.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SafeCrossOriginHeaders"/> because the two live on different
    /// objects: these travel on the reused <see cref="HttpContent"/> instance rather than on the
    /// request. Both describe the body rather than authorize it — a signature or a bearer placed
    /// on a content header is stripped like any other unlisted name.
    /// </remarks>
    private static readonly string[] SafeCrossOriginContentHeaders =
    [
        "Content-Type",
        "Content-Length",
        "Content-Encoding",
    ];

    private readonly ILogger<LinkLocalGuardHandler> _logger;
    private readonly OutboundDestination.AddressResolver? _resolver;

    public LinkLocalGuardHandler(ILogger<LinkLocalGuardHandler> logger)
        : this(logger, null)
    {
    }

    /// <summary>
    /// Supplies an address resolver instead of using the machine's DNS. Exists so tests do not
    /// depend on network conditions; DI uses the single-argument constructor.
    /// </summary>
    public LinkLocalGuardHandler(
        ILogger<LinkLocalGuardHandler> logger,
        OutboundDestination.AddressResolver? resolver)
    {
        _logger = logger;
        _resolver = resolver;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
            return await base.SendAsync(request, cancellationToken);

        var current = request;
        var hops = 0;

        while (true)
        {
            await EnsureAllowedAsync(current.RequestUri!, cancellationToken);

            var response = await base.SendAsync(current, cancellationToken);

            if (!TryGetRedirect(response, current.RequestUri!, out var target))
                return response;

            if (++hops > MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException(
                    $"Connector request exceeded {MaxRedirects} redirects.");
            }

            var next = CloneForRedirect(current, response.StatusCode, target!);
            response.Dispose();

            if (!ReferenceEquals(current, request))
            {
                // Detach the body first. A 307/308 clone shares the caller's HttpContent instance,
                // and HttpRequestMessage.Dispose disposes its Content — so disposing the previous
                // hop would dispose the content the next hop is about to send, and could dispose
                // the caller's own request.Content after this method returns.
                current.Content = null;
                current.Dispose();
            }

            current = next;
        }
    }

    private async Task EnsureAllowedAsync(Uri uri, CancellationToken ct)
    {
        if (await OutboundDestination.IsNotLinkLocalAsync(uri.AbsoluteUri, ct, _resolver))
            return;

        _logger.LogWarning(
            "Refusing connector request to {Host}: resolves to a link-local address", uri.Host);

        throw new HttpRequestException(
            $"Refusing to reach '{uri.Host}': the address is link-local, which no connector " +
            "target should be.");
    }

    /// <summary>
    /// True when <paramref name="response"/> is a redirect carrying a usable absolute target.
    /// A relative <c>Location</c> is resolved against the request URI.
    /// </summary>
    private static bool TryGetRedirect(HttpResponseMessage response, Uri requestUri, out Uri? target)
    {
        target = null;

        if (response.StatusCode is not (HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect))
        {
            return false;
        }

        var location = response.Headers.Location;
        if (location is null)
            return false;

        var absolute = location.IsAbsoluteUri ? location : new Uri(requestUri, location);

        // Only http(s) is followed. A redirect to any other scheme is not a connector target, and
        // OutboundDestination would refuse it anyway; stopping here keeps the response intact for
        // the caller to report rather than raising.
        if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
            return false;

        target = absolute;
        return true;
    }

    /// <summary>
    /// Builds the follow-up request. 301/302/303 become GET without a body, as
    /// <see cref="System.Net.Http.HttpClient"/> does; 307/308 preserve method and content.
    /// </summary>
    /// <remarks>
    /// A 307/308 re-sends the <em>same</em> <see cref="HttpContent"/> instance, which only works
    /// for buffered content. Every connector POST today uses
    /// <see cref="FormUrlEncodedContent"/> or a serialized string, both buffered and re-readable;
    /// a non-buffered body such as <see cref="StreamContent"/> would throw on the second send.
    /// Buffering here to remove that limit would mean holding an arbitrary request body in memory
    /// for a case no connector currently has, so it fails loudly instead.
    /// </remarks>
    private static HttpRequestMessage CloneForRedirect(
        HttpRequestMessage original, HttpStatusCode status, Uri target)
    {
        var demoteToGet = status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther;

        var clone = new HttpRequestMessage(
            demoteToGet ? HttpMethod.Get : original.Method, target)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };

        var crossOrigin = !IsSameOrigin(original.RequestUri!, target);

        if (!demoteToGet)
        {
            clone.Content = original.Content;

            // Content headers are a credential channel too — the webhook sender puts its
            // signature on them — and they ride along on the HttpContent instance, so the
            // request-header allowlist above never sees them. Reduce them to the same set.
            // A body-less method redirected with 307/308 has no content to reduce.
            if (crossOrigin && clone.Content is not null)
            {
                var carried = clone.Content.Headers
                    .Where(h => !SafeCrossOriginContentHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
                    .Select(h => h.Key)
                    .ToList();

                foreach (var name in carried)
                    clone.Content.Headers.Remove(name);
            }
        }

        foreach (var header in original.Headers)
        {
            if (crossOrigin
                && !SafeCrossOriginHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in original.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        return clone;
    }

    private static bool IsSameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
