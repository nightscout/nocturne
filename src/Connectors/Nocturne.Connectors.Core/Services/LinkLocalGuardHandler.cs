using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.Net;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
/// Refuses a connector request whose target resolves to a link-local address.
/// </summary>
/// <remarks>
/// Connector base URLs come from tenant configuration — a value someone signed in supplied
/// through <c>PUT /api/v4/connectors/config/{connectorName}</c> — and the request is sent from
/// inside the deployment's network, with the outcome reported back through connector status. That
/// makes the connector fetch a request-forgery primitive.
/// <para>
/// Only link-local is refused, not every private range. A self-hosted deployment legitimately
/// points the Nightscout, remote-Nocturne or MyLife connector at a private address — a Nightscout
/// on the same Docker network or LAN is the ordinary migration setup — so requiring public
/// routability would break real installs. <c>169.254.169.254</c> and its neighbours have no
/// legitimate connector use and are where cloud instance credentials live, so that range is
/// refused for every tenant regardless of what was configured.
/// </para>
/// <para>
/// Applied at the sink rather than at the write, so it holds for a row that was already stored,
/// or stored by some path other than the configuration endpoint. Registered by
/// <c>HttpClientExtensions.ConfigureConnectorClient</c>, which every connector client goes
/// through, and the primary handler it sits above does not follow redirects by default for
/// connectors that opt out — see the note on <see cref="OutboundDestination"/> about the residual
/// gap between checking a name and connecting to it.
/// </para>
/// </remarks>
public sealed class LinkLocalGuardHandler : DelegatingHandler
{
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
        var uri = request.RequestUri;
        if (uri is null)
            return await base.SendAsync(request, cancellationToken);

        var allowed = await OutboundDestination.IsNotLinkLocalAsync(
            uri.AbsoluteUri, cancellationToken, _resolver);

        if (!allowed)
        {
            _logger.LogWarning(
                "Refusing connector request to {Host}: resolves to a link-local address",
                uri.Host);

            throw new HttpRequestException(
                $"Refusing to reach '{uri.Host}': the address is link-local, which no connector " +
                "target should be.");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
