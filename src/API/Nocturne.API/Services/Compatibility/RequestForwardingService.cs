using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Helpers;
using Nocturne.API.Models.Compatibility;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;

namespace Nocturne.API.Services.Compatibility;

/// <summary>
/// Forwards cloned requests to the upstream Nightscout instance as part of the compatibility proxy.
/// </summary>
/// <seealso cref="RequestForwardingService"/>
public interface IRequestForwardingService
{
    /// <summary>
    /// Forwards a cloned request to the Nightscout instance.
    /// Returns <see langword="null"/> when the circuit breaker is open, avoiding unnecessary
    /// network calls while Nightscout is unavailable.
    /// </summary>
    Task<TargetResponse?> ForwardToNightscoutAsync(
        ClonedRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IRequestForwardingService"/> implementation that mirrors requests to the
/// configured Nightscout URL for compatibility comparison.
/// </summary>
/// <remarks>
/// <para>
/// Nightscout API authentication is added via the SHA-1 hash of the <c>api-secret</c>
/// configuration value (matching the original Nightscout authentication scheme). The header
/// is only injected when not already present in the incoming request so callers can override it.
/// </para>
/// <para>
/// The <c>NightscoutCircuitBreaker</c> is consulted before each request; when open, the method
/// returns <see langword="null"/> immediately. Successful responses call
/// <c>RecordSuccess()</c> and exceptions call <c>RecordFailure()</c> to update breaker state.
/// </para>
/// <para>
/// Error messages are filtered through <see cref="Configuration.CompatibilityProxyConfiguration"/>
/// redaction rules before being stored or forwarded, preventing Nightscout credentials from
/// leaking into logs or discrepancy reports.
/// </para>
/// </remarks>
/// <seealso cref="IRequestForwardingService"/>
public class RequestForwardingService : IRequestForwardingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CompatibilityProxyConfiguration> _configuration;
    private readonly NightscoutConnectorConfiguration _nightscoutConfig;
    private readonly NightscoutCircuitBreaker _circuitBreaker;
    private readonly ICorrelationService _correlationService;
    private readonly ILogger<RequestForwardingService> _logger;

    /// <summary>
    /// Initializes a new instance of the RequestForwardingService class
    /// </summary>
    public RequestForwardingService(
        IHttpClientFactory httpClientFactory,
        IOptions<CompatibilityProxyConfiguration> configuration,
        NightscoutConnectorConfiguration nightscoutConfig,
        NightscoutCircuitBreaker circuitBreaker,
        ICorrelationService correlationService,
        ILogger<RequestForwardingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _nightscoutConfig = nightscoutConfig;
        _circuitBreaker = circuitBreaker;
        _correlationService = correlationService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TargetResponse?> ForwardToNightscoutAsync(
        ClonedRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_circuitBreaker.IsOpen)
        {
            _logger.LogWarning(
                "Circuit breaker is open, skipping Nightscout forwarding for {Method} {Path}",
                request.Method.Replace("\r", "").Replace("\n", ""),
                request.Path.Replace("\r", "").Replace("\n", ""));
            return null;
        }

        var correlationId = _correlationService.GetCurrentCorrelationId();
        var stopwatch = Stopwatch.StartNew();
        var response = new TargetResponse { Target = "Nightscout" };

        try
        {
            var nightscoutUrl = _nightscoutConfig.Url;
            if (string.IsNullOrEmpty(nightscoutUrl))
            {
                _logger.LogWarning(
                    "Nightscout URL not configured [CorrelationId: {CorrelationId}]",
                    correlationId);
                response.ErrorMessage = "Nightscout URL not configured";
                return response;
            }

            using var httpClient = _httpClientFactory.CreateClient("NightscoutClient");
            httpClient.Timeout = TimeSpan.FromSeconds(_configuration.Value.TimeoutSeconds);

            var requestUri = NightscoutBaseUri.Resolve(nightscoutUrl, request.Path);
            using var httpRequest = new HttpRequestMessage(
                new HttpMethod(request.Method),
                requestUri);

            // Add correlation ID header for tracking
            if (!string.IsNullOrEmpty(correlationId))
            {
                httpRequest.Headers.Add("X-Correlation-ID", correlationId);
            }

            // Add Nightscout authentication
            AddNightscoutAuth(httpRequest);

            // Add headers (excluding content headers which are handled separately)
            foreach (var header in request.Headers)
            {
                if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    continue;
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Add content if present
            if (request.Body?.Length > 0)
            {
                httpRequest.Content = new ByteArrayContent(request.Body);
                if (!string.IsNullOrEmpty(request.ContentType))
                {
                    httpRequest.Content.Headers.TryAddWithoutValidation(
                        "Content-Type",
                        request.ContentType);
                }
            }

            _logger.LogDebug(
                "Sending request to Nightscout: {Uri} [CorrelationId: {CorrelationId}]",
                requestUri,
                correlationId);

            using var httpResponse = await httpClient.SendAsync(httpRequest, cancellationToken);

            response.StatusCode = (int)httpResponse.StatusCode;
            response.IsSuccess = httpResponse.IsSuccessStatusCode;
            response.ContentType = httpResponse.Content.Headers.ContentType?.ToString();
            response.Body = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);

            // Copy response headers
            foreach (var header in httpResponse.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in httpResponse.Content.Headers)
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }

            _circuitBreaker.RecordSuccess();

            _logger.LogDebug(
                "Received response from Nightscout: {StatusCode} in {ResponseTime}ms [CorrelationId: {CorrelationId}]",
                response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                correlationId);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(
                "Request to Nightscout timed out after {Timeout}s [CorrelationId: {CorrelationId}]",
                _configuration.Value.TimeoutSeconds,
                correlationId);
            response.ErrorMessage = "Request timed out";
            response.StatusCode = 408;
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogError(
                ex,
                "Error forwarding request to Nightscout [CorrelationId: {CorrelationId}]",
                correlationId);
            response.ErrorMessage = FilterSensitiveErrorMessage(ex.Message);
            response.StatusCode = 500;
        }
        finally
        {
            response.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
        }

        return response;
    }

    private void AddNightscoutAuth(HttpRequestMessage request)
    {
        var apiSecret = _nightscoutConfig.ApiSecret;
        if (string.IsNullOrEmpty(apiSecret))
            return;

        // Only add if not already present from the incoming request
        if (!request.Headers.Contains("api-secret"))
        {
            var hash = ComputeSha1Hash(apiSecret);
            request.Headers.Add("api-secret", hash);
            _logger.LogDebug("Added api-secret header to Nightscout request");
        }
    }

    private static string ComputeSha1Hash(string input)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string FilterSensitiveErrorMessage(string errorMessage)
    {
        var redactionSettings = _configuration.Value.Redaction;
        var filteredMessage = errorMessage;

        foreach (var sensitiveField in redactionSettings.GetAllSensitiveFields())
        {
            filteredMessage = filteredMessage.Replace(
                sensitiveField,
                redactionSettings.ReplacementText,
                StringComparison.OrdinalIgnoreCase);
        }

        return filteredMessage;
    }
}
