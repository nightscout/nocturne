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

        /// <summary>
        ///     Determines if the response indicates that re-authentication is required.
        /// </summary>
        /// <returns>True if the token should be invalidated and refreshed</returns>
        public bool RequiresReauthentication()
        {
            return response.StatusCode == HttpStatusCode.Unauthorized;
        }

        /// <summary>
        ///     Determines if the response indicates a non-retryable client error (invalid credentials, forbidden, etc.)
        /// </summary>
        /// <returns>True if the error is permanent and should not be retried</returns>
        public bool IsNonRetryableClientError()
        {
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest => true,
                HttpStatusCode.Forbidden => true,
                HttpStatusCode.NotFound => true,
                HttpStatusCode.MethodNotAllowed => true,
                HttpStatusCode.Gone => true,
                HttpStatusCode.UnprocessableEntity => true,
                _ => false
            };
        }
    }
}