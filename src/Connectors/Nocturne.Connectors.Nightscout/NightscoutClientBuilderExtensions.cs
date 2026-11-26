using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Nightscout;

public static class NightscoutClientBuilderExtensions
{
    /// <summary>
    ///     Keeps the path of each request out of the client's telemetry, since a site's path can be its
    ///     credential (see <see cref="NightscoutAddress"/>). The factory's request logging writes the
    ///     request URI, so it is removed. Each span records
    ///     <see cref="NightscoutAddress.ShownRequest"/> as the request's <see cref="OutboundTelemetry.DisplayUrl"/>.
    /// </summary>
    /// <remarks>
    ///     Added after the connector client's handlers, so it marks every hop the link-local guard
    ///     follows as well as the first request. A sender that sets its own display URL, because its
    ///     path holds a credential or is the base itself, keeps it on every hop.
    /// </remarks>
    public static IHttpClientBuilder KeepNightscoutPathOutOfTelemetry(this IHttpClientBuilder builder) =>
        builder.RemoveAllLoggers().AddHttpMessageHandler(() => new ShownRequestHandler());

    private sealed class ShownRequestHandler : DelegatingHandler
    {
        // A redirect hop copies its request's options, so a display URL derived here is derived again for the hop.
        private static readonly HttpRequestOptionsKey<bool> Derived = new("Nocturne.Nightscout.DisplayUrlDerived");

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
            var senderSet = request.Options.TryGetValue(OutboundTelemetry.DisplayUrl, out _)
                            && !request.Options.TryGetValue(Derived, out _);
            if (!senderSet && request.RequestUri is { IsAbsoluteUri: true } uri)
            {
                request.Options.Set(OutboundTelemetry.DisplayUrl, NightscoutAddress.ShownRequest(uri));
                request.Options.Set(Derived, true);
            }
        }
    }
}
