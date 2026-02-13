using System.Net;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Extension methods for HttpResponseMessage to standardize error handling across connectors.
/// </summary>
public static class HttpResponseExtensions
{
    /// <param name="response">The HTTP response to check</param>
    extension(HttpResponseMessage response)
    {
        /// <summary>
        ///     Determines if the response indicates a retryable error (rate limiting, server errors, etc.)
        /// </summary>
        /// <returns>True if the request should be retried</returns>
        public bool IsRetryableError()
        {
            return response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => true,
                HttpStatusCode.ServiceUnavailable => true,
                HttpStatusCode.InternalServerError => true,
                HttpStatusCode.BadGateway => true,
                HttpStatusCode.GatewayTimeout => true,
                HttpStatusCode.RequestTimeout => true,
                _ => false
            };
        }

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