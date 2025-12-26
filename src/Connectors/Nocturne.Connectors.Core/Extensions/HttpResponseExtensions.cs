using System;
using System.Net;
using System.Net.Http;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
/// Extension methods for HttpResponseMessage to standardize error handling across connectors.
/// </summary>
public static class HttpResponseExtensions
{
    /// <summary>
    /// Determines if the response indicates a retryable error (rate limiting, server errors, etc.)
    /// </summary>
    /// <param name="response">The HTTP response to check</param>
    /// <returns>True if the request should be retried</returns>
    public static bool IsRetryableError(this HttpResponseMessage response)
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
    /// Determines if the response indicates that re-authentication is required.
    /// </summary>
    /// <param name="response">The HTTP response to check</param>
    /// <returns>True if the token should be invalidated and refreshed</returns>
    public static bool RequiresReauthentication(this HttpResponseMessage response)
    {
        return response.StatusCode == HttpStatusCode.Unauthorized;
    }

    /// <summary>
    /// Determines if the response indicates a non-retryable client error (invalid credentials, forbidden, etc.)
    /// </summary>
    /// <param name="response">The HTTP response to check</param>
    /// <returns>True if the error is permanent and should not be retried</returns>
    public static bool IsNonRetryableClientError(this HttpResponseMessage response)
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
