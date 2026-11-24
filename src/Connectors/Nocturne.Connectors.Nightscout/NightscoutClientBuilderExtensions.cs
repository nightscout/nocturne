using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Nightscout;

public static class NightscoutClientBuilderExtensions
{
    /// <summary>
    ///     Keeps the path of each request out of the client's telemetry, since a site's path can be its
    ///     credential (see <see cref="NightscoutAddress"/>). The factory's request logging writes the
    ///     request URI, so it is removed. Each span records the request's
    ///     <see cref="NightscoutAddress.Masked"/> URL as its <see cref="OutboundTelemetry.DisplayUrl"/>.
    /// </summary>
    /// <remarks>
    ///     Added after the connector client's handlers, so it marks every hop the link-local guard
    ///     follows as well as the first request.
    /// </remarks>
    public static IHttpClientBuilder KeepNightscoutPathOutOfTelemetry(this IHttpClientBuilder builder) =>
        builder.RemoveAllLoggers().AddHttpMessageHandler(() => new MaskedDisplayUrlHandler());

    private sealed class MaskedDisplayUrlHandler : DelegatingHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Mark(request);
            return base.Send(request, cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Mark(request);
            return base.SendAsync(request, cancellationToken);
        }

        private static void Mark(HttpRequestMessage request)
        {
            if (request.RequestUri is { IsAbsoluteUri: true } uri)
                request.Options.Set(OutboundTelemetry.DisplayUrl, NightscoutAddress.Masked(uri.AbsoluteUri));
        }
    }
}
