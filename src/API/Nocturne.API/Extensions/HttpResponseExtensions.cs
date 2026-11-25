using System.Globalization;

namespace Nocturne.API.Extensions;

internal static class HttpResponseExtensions
{
    /// <summary>Sets <c>Retry-After</c> to <paramref name="wait"/> in whole seconds, rounded up so a client never retries early.</summary>
    public static void SetRetryAfter(this HttpResponse response, TimeSpan wait) =>
        response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
