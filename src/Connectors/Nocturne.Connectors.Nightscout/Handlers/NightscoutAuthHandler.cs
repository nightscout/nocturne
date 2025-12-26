using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;

namespace Nocturne.Connectors.Nightscout.Handlers;

/// <summary>
/// DelegatingHandler that centralizes Nightscout authentication.
/// Handles JWT token acquisition/caching for v3 API and API-secret header injection for v1 API.
/// </summary>
public class NightscoutAuthHandler : DelegatingHandler
{
    private readonly NightscoutConnectorConfiguration _config;
    private readonly ILogger<NightscoutAuthHandler> _logger;

    // Thread-safe JWT token caching
    private string? _jwtToken;
    private DateTime _jwtTokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public NightscoutAuthHandler(
        IOptions<NightscoutConnectorConfiguration> config,
        ILogger<NightscoutAuthHandler> logger)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Check if this is an internal auth request (token acquisition) - skip auth processing
        if (request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("IsInternalAuthRequest"), out var isInternalAuth) && isInternalAuth)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var isV3Endpoint = request.RequestUri?.AbsolutePath.Contains("/api/v3") == true ||
                           request.RequestUri?.AbsolutePath.Contains("/api/v2/authorization") == true;

        // Skip auth for the JWT token request itself to avoid infinite loop
        var isTokenRequest = request.RequestUri?.AbsolutePath.Contains("/api/v2/authorization/request") == true;

        if (!isTokenRequest)
        {
            if (isV3Endpoint && !string.IsNullOrEmpty(_config.SubjectToken))
            {
                // Try JWT auth for v3 endpoints
                await AddJwtAuthHeaderAsync(request, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(_config.SourceApiSecret))
            {
                // Fall back to API-secret for v1 endpoints or when no subject token configured
                AddApiSecretHeader(request);
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        // Handle 401 by invalidating JWT and retrying once (only for v3 endpoints with JWT)
        if (response.StatusCode == HttpStatusCode.Unauthorized &&
            isV3Endpoint &&
            !isTokenRequest &&
            _jwtToken != null)
        {
            _logger.LogWarning("Received 401 Unauthorized, invalidating JWT token and retrying");

            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                _jwtToken = null;
                _jwtTokenExpiry = DateTime.MinValue;
            }
            finally
            {
                _tokenLock.Release();
            }

            // Clone the request for retry (original request stream may have been consumed)
            var retryRequest = await CloneRequestAsync(request);
            await AddJwtAuthHeaderAsync(retryRequest, cancellationToken);

            response.Dispose();
            response = await base.SendAsync(retryRequest, cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// Gets or refreshes the JWT token for v3 API authentication
    /// </summary>
    private async Task<string?> GetJwtTokenAsync(HttpRequestMessage originalRequest, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Return cached token if still valid
            if (!string.IsNullOrEmpty(_jwtToken) && DateTime.UtcNow < _jwtTokenExpiry)
            {
                _logger.LogDebug("Using cached JWT token (expires in {Minutes} minutes)",
                    (_jwtTokenExpiry - DateTime.UtcNow).TotalMinutes);
                return _jwtToken;
            }

            var subjectToken = _config.SubjectToken;
            if (string.IsNullOrEmpty(subjectToken))
            {
                _logger.LogDebug("No Subject Token configured, skipping JWT authentication");
                return null;
            }

            // Build absolute URL using the base address from the original request
            var baseUri = originalRequest.RequestUri?.GetLeftPart(UriPartial.Authority);
            if (string.IsNullOrEmpty(baseUri))
            {
                _logger.LogWarning("Cannot determine base URI for JWT token request");
                return null;
            }

            var tokenUrl = new Uri($"{baseUri}/api/v2/authorization/request/{subjectToken}");
            _logger.LogDebug("Requesting JWT token from {Url}", tokenUrl);

            // Create a new request for the token
            var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
            // Mark this as an internal auth request to skip auth processing
            tokenRequest.Options.Set(new HttpRequestOptionsKey<bool>("IsInternalAuthRequest"), true);

            var response = await base.SendAsync(tokenRequest, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var authResponse = JsonSerializer.Deserialize<JsonElement>(content);

                if (authResponse.TryGetProperty("token", out var tokenElement))
                {
                    _jwtToken = tokenElement.GetString();
                    // JWT tokens typically expire in 1 hour, refresh at 50 minutes
                    _jwtTokenExpiry = DateTime.UtcNow.AddMinutes(50);
                    _logger.LogInformation("Successfully obtained JWT token for v3 API (expires at {Expiry})",
                        _jwtTokenExpiry);
                    return _jwtToken;
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Failed to get JWT token: {StatusCode} - {Error}",
                    response.StatusCode,
                    errorContent);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obtaining JWT token for v3 API");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Adds JWT authentication header to request for v3 API calls
    /// </summary>
    private async Task<bool> AddJwtAuthHeaderAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await GetJwtTokenAsync(request, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            // Fall back to API-secret if JWT is unavailable
            if (!string.IsNullOrEmpty(_config.SourceApiSecret))
            {
                _logger.LogDebug("JWT token unavailable, falling back to API-secret header");
                AddApiSecretHeader(request);
            }
            return false;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _logger.LogDebug("Added JWT Bearer token to request");
        return true;
    }

    /// <summary>
    /// Adds API secret header to request for v1 API calls
    /// </summary>
    private void AddApiSecretHeader(HttpRequestMessage request)
    {
        var apiSecret = _config.SourceApiSecret;
        if (!string.IsNullOrEmpty(apiSecret))
        {
            var hashedSecret = HashApiSecret(apiSecret);
            // Remove any existing header to avoid duplicates
            request.Headers.Remove("api-secret");
            request.Headers.Remove("API-SECRET");
            request.Headers.Add("api-secret", hashedSecret);
            _logger.LogDebug("Added api-secret header with hashed secret");
        }
        else
        {
            _logger.LogWarning("No API secret configured for authentication");
        }
    }

    /// <summary>
    /// Hash API secret using SHA1 to match Nightscout's expected format
    /// </summary>
    private static string HashApiSecret(string apiSecret)
    {
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Clones an HTTP request message for retry purposes
    /// </summary>
    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        // Copy headers
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content if present
        if (request.Content != null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(contentBytes);

            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Copy properties
        foreach (var property in request.Options)
        {
            clone.Options.TryAdd(property.Key, property.Value);
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tokenLock.Dispose();
        }
        base.Dispose(disposing);
    }
}
