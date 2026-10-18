using System.Net;

namespace Nocturne.Connectors.Core.Extensions;

public static class HttpResponseExtensions
{
    /// <summary>
    ///     The statuses worth sending the same request again for: the source is rate-limiting us or
    ///     is transiently unwell, so the request itself is not at fault.
    /// </summary>
    public static bool IsRetryableStatusCode(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout;

    extension(HttpResponseMessage response)
    {
        /// <inheritdoc cref="IsRetryableStatusCode"/>
        public bool IsRetryableError() => IsRetryableStatusCode(response.StatusCode);

        /// <summary>
        ///     Throws for a non-success status. The exception carries the status code, which the
        ///     connector retry loop branches on, and the response body, which is usually the only
        ///     account of why the source refused.
        /// </summary>
        public async Task EnsureSuccessWithBodyAsync(CancellationToken cancellationToken = default)
        {
            if (response.IsSuccessStatusCode) return;

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode);
        }
    }
}