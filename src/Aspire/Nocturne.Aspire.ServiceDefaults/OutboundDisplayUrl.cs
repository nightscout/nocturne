using System.Diagnostics;
using Nocturne.Core.Constants;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Records a request's <see cref="OutboundTelemetry.DisplayUrl"/> in place of its URL on the span
/// the HTTP client instrumentation exports. A request without one keeps its URL.
/// </summary>
internal static class OutboundDisplayUrl
{
    private static readonly string[] UrlTags = ["url.full", "http.url"];

    private static readonly string[] PathTags = ["url.path", "http.target"];

    internal static void Apply(Activity activity, HttpRequestMessage request)
    {
        if (!request.Options.TryGetValue(OutboundTelemetry.DisplayUrl, out var displayUrl))
        {
            return;
        }

        foreach (var tag in UrlTags)
        {
            activity.SetTag(tag, displayUrl);
        }

        foreach (var tag in PathTags)
        {
            activity.SetTag(tag, null);
        }

        if (request.RequestUri is { IsAbsoluteUri: true } uri
            && uri.AbsolutePath.Length > 1
            && activity.DisplayName.Contains(uri.AbsolutePath, StringComparison.Ordinal))
        {
            activity.DisplayName = request.Method.Method;
        }
    }
}
