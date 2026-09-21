using System.Net;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Extension methods for HttpResponseMessage to standardize error handling across connectors.
/// </summary>
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

    /// <param name="response">The HTTP response to check</param>
    extension(HttpResponseMessage response)
    {
        /// <inheritdoc cref="IsRetryableStatusCode"/>
        public bool IsRetryableError() => IsRetryableStatusCode(response.StatusCode);
    }
}