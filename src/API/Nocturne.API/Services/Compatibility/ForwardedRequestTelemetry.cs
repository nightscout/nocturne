using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Constants;

namespace Nocturne.API.Services.Compatibility;

internal static class ForwardedRequestTelemetry
{
    /// <summary>
    ///     Sets the span URL of a request to <paramref name="address"/> from
    ///     <paramref name="pathAndQuery"/> with its credential route values redacted. A route such as
    ///     <c>/api/v2/authorization/request/{accessToken}</c> holds the caller's token, which the
    ///     handler of <c>KeepNightscoutPathOutOfTelemetry</c> cannot tell from any other segment. A
    ///     request with no path of its own is to the base, shown as <see cref="NightscoutAddress.ShownBase"/>.
    /// </summary>
    internal static void ShowRedacted(
        this HttpRequestMessage request,
        NightscoutAddress address,
        string pathAndQuery,
        IRequestPathRedactor redactor)
    {
        var route = pathAndQuery.Split('?', 2)[0];
        if (route.Trim('/').Length == 0)
            request.Options.Set(OutboundTelemetry.DisplayUrl, address.ShownBase);
        else if (address.TryFor(redactor.Redact(pathAndQuery), out var redacted))
            request.Options.Set(OutboundTelemetry.DisplayUrl, NightscoutAddress.ShownRequest(redacted));
    }
}
