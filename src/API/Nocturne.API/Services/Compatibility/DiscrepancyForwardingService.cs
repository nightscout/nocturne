using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Models.Compatibility;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Compatibility;

/// <summary>
/// Forwards compatibility-proxy discrepancy analysis results to an external Nocturne
/// telemetry endpoint and/or writes them to the local filesystem for offline review.
/// </summary>
/// <seealso cref="DiscrepancyForwardingService"/>
public interface IDiscrepancyForwardingService
{
    /// <summary>
    /// Forwards a discrepancy analysis. The call is a no-op (returns <see langword="true"/>)
    /// when the analysis falls below <c>MinimumSeverity</c> or when both
    /// <c>Enabled</c> and <c>SaveRawData</c> are <see langword="false"/>.
    /// File saving and remote forwarding can be enabled independently.
    /// </summary>
    Task<bool> ForwardDiscrepancyAsync(
        ResponseComparisonResult analysis,
        string requestMethod,
        string requestPath,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// <see cref="IDiscrepancyForwardingService"/> implementation that supports both file-based
/// and HTTP forwarding of discrepancy payloads.
/// </summary>
/// <remarks>
/// <para>
/// File names embed a UTC timestamp and a sanitised trace identifier so that they sort
/// chronologically and can be correlated back to the originating request. Relative
/// <c>DataDirectory</c> paths are resolved relative to <c>AppContext.BaseDirectory</c>.
/// </para>
/// <para>
/// HTTP forwarding always targets the fixed path <c>/api/v4/discrepancy/ingest</c> on the
/// configured <c>EndpointUrl</c>. Retries use exponential back-off; 4xx responses are not
/// retried because they indicate a permanent client-side error. The HTTP client is named
/// <see cref="HttpClientName"/> and must be registered in the DI container.
/// </para>
/// <para>
/// Severity is determined by examining <see cref="ResponseMatchType"/> and the highest
/// <see cref="DiscrepancySeverity"/> among individual discrepancies. The check-order is:
/// match type (critical/major) → individual discrepancy severity (critical → major → minor).
/// </para>
/// </remarks>
/// <seealso cref="IDiscrepancyForwardingService"/>
public class DiscrepancyForwardingService : IDiscrepancyForwardingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CompatibilityProxyConfiguration> _configuration;
    private readonly ILogger<DiscrepancyForwardingService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// HTTP client name for discrepancy forwarding
    /// </summary>
    public const string HttpClientName = "DiscrepancyForwarding";

    /// <summary>
    /// Initializes a new instance of the DiscrepancyForwardingService class
    /// </summary>
    public DiscrepancyForwardingService(
        IHttpClientFactory httpClientFactory,
        IOptions<CompatibilityProxyConfiguration> configuration,
        ILogger<DiscrepancyForwardingService> logger
    )
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ForwardDiscrepancyAsync(
        ResponseComparisonResult analysis,
        string requestMethod,
        string requestPath,
        CancellationToken cancellationToken = default
    )
    {
        var settings = _configuration.Value.DiscrepancyForwarding;

        // Check if anything is enabled
        if (!settings.Enabled && !settings.SaveRawData)
        {
            _logger.LogDebug("Discrepancy forwarding and file saving are both disabled, skipping");
            return true;
        }

        // Check minimum severity threshold
        if (!ShouldForward(analysis, settings.MinimumSeverity))
        {
            _logger.LogDebug(
                "Discrepancy for {CorrelationId} below minimum severity threshold, skipping",
                analysis.TraceId
            );
            return true;
        }

        // Build the forwarding payload
        var payload = BuildForwardingPayload(analysis, requestMethod, requestPath, settings.SourceId);

        var saveSuccess = true;
        var forwardSuccess = true;

        // Save to file if enabled
        if (settings.SaveRawData)
        {
            saveSuccess = await SaveToFileAsync(payload, settings, cancellationToken);
        }

        // Forward to remote endpoint if enabled and configured
        if (settings.Enabled && !string.IsNullOrEmpty(settings.EndpointUrl))
        {
            forwardSuccess = await ForwardWithRetriesAsync(payload, settings, cancellationToken);
        }
        else if (settings.Enabled && string.IsNullOrEmpty(settings.EndpointUrl))
        {
            _logger.LogDebug("Discrepancy forwarding is enabled but EndpointUrl is not configured, skipping remote forward");
        }

        return saveSuccess && forwardSuccess;
    }

    private async Task<bool> SaveToFileAsync(
        ForwardedDiscrepancyDto payload,
        DiscrepancyForwardingSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            // Ensure directory exists
            var directory = settings.DataDirectory;
            if (!Path.IsPathRooted(directory))
            {
                directory = Path.Combine(AppContext.BaseDirectory, directory);
            }

            Directory.CreateDirectory(directory);

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            var traceId = payload.Analysis.TraceId.Replace(":", "-").Replace("/", "-");
            var filename = $"discrepancy_{timestamp}_{traceId}.json";
            var filePath = Path.Combine(directory, filename);

            // Serialize and save
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            _logger.LogDebug(
                "Saved discrepancy {CorrelationId} to file {FilePath}",
                payload.Analysis.TraceId,
                filePath
            );

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to save discrepancy {CorrelationId} to file",
                payload.Analysis.TraceId
            );
            return false;
        }
    }

    private bool ShouldForward(ResponseComparisonResult analysis, DiscrepancySeverityLevel minimumSeverity)
    {
        // Determine the highest severity in the analysis
        var highestSeverity = GetHighestSeverity(analysis);

        return highestSeverity >= minimumSeverity;
    }

    private DiscrepancySeverityLevel GetHighestSeverity(ResponseComparisonResult analysis)
    {
        // Check match type for critical issues
        if (analysis.OverallMatch == ResponseMatchType.CriticalDifferences ||
            analysis.OverallMatch == ResponseMatchType.ComparisonError ||
            analysis.OverallMatch == ResponseMatchType.BothMissing)
        {
            return DiscrepancySeverityLevel.Critical;
        }

        if (analysis.OverallMatch == ResponseMatchType.MajorDifferences ||
            analysis.OverallMatch == ResponseMatchType.NightscoutMissing ||
            analysis.OverallMatch == ResponseMatchType.NocturneMissing)
        {
            return DiscrepancySeverityLevel.Major;
        }

        // Check individual discrepancies
        if (analysis.Discrepancies.Any(d => d.Severity == DiscrepancySeverity.Critical))
        {
            return DiscrepancySeverityLevel.Critical;
        }

        if (analysis.Discrepancies.Any(d => d.Severity == DiscrepancySeverity.Major))
        {
            return DiscrepancySeverityLevel.Major;
        }

        return DiscrepancySeverityLevel.Minor;
    }

    private ForwardedDiscrepancyDto BuildForwardingPayload(
        ResponseComparisonResult analysis,
        string requestMethod,
        string requestPath,
        string sourceId)
    {
        return new ForwardedDiscrepancyDto
        {
            SourceId = string.IsNullOrEmpty(sourceId) ? Environment.MachineName : sourceId,
            ReceivedAt = DateTimeOffset.UtcNow,
            Analysis = new DiscrepancyAnalysisDto
            {
                Id = Guid.NewGuid(),
                TraceId = analysis.TraceId,
                AnalysisTimestamp = analysis.ComparisonTimestamp,
                RequestMethod = requestMethod,
                RequestPath = requestPath,
                OverallMatch = (int)analysis.OverallMatch,
                StatusCodeMatch = analysis.StatusCodeMatch,
                BodyMatch = analysis.BodyMatch,
                NightscoutResponseTimeMs = analysis.PerformanceComparison?.NightscoutResponseTime,
                NocturneResponseTimeMs = analysis.PerformanceComparison?.NocturneResponseTime,
                Summary = analysis.Summary,
                CriticalDiscrepancyCount = analysis.Discrepancies.Count(d => d.Severity == DiscrepancySeverity.Critical),
                MajorDiscrepancyCount = analysis.Discrepancies.Count(d => d.Severity == DiscrepancySeverity.Major),
                MinorDiscrepancyCount = analysis.Discrepancies.Count(d => d.Severity == DiscrepancySeverity.Minor),
                Discrepancies = analysis.Discrepancies.Select(d => new DiscrepancyDetailDto
                {
                    Id = Guid.NewGuid(),
                    DiscrepancyType = d.Type,
                    Severity = d.Severity,
                    Field = d.Field,
                    NightscoutValue = d.NightscoutValue,
                    NocturneValue = d.NocturneValue,
                    Description = d.Description,
                    RecordedAt = DateTimeOffset.UtcNow,
                }).ToList(),
            }
        };
    }

    private async Task<bool> ForwardWithRetriesAsync(
        ForwardedDiscrepancyDto payload,
        DiscrepancyForwardingSettings settings,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        var maxRetries = settings.RetryAttempts;

        while (retryCount <= maxRetries)
        {
            try
            {
                using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
                httpClient.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);

                var endpointUri = new Uri(new Uri(settings.EndpointUrl.TrimEnd('/')), "/api/v4/discrepancy/ingest");

                var json = JsonSerializer.Serialize(payload, JsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
                request.Content = content;

                // Add authentication if configured
                if (!string.IsNullOrEmpty(settings.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                }

                // Add source identification header
                request.Headers.Add("X-Nocturne-Source", payload.SourceId);
                request.Headers.Add("X-Correlation-ID", payload.Analysis.TraceId);

                var response = await httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Successfully forwarded discrepancy {CorrelationId} to {Endpoint}",
                        payload.Analysis.TraceId,
                        settings.EndpointUrl
                    );
                    return true;
                }

                _logger.LogWarning(
                    "Failed to forward discrepancy {CorrelationId} to {Endpoint}: {StatusCode}",
                    payload.Analysis.TraceId,
                    settings.EndpointUrl,
                    response.StatusCode
                );

                // Don't retry on client errors (4xx)
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    return false;
                }
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Discrepancy forwarding cancelled");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Error forwarding discrepancy {CorrelationId} to {Endpoint} (attempt {Attempt}/{MaxAttempts})",
                    payload.Analysis.TraceId,
                    settings.EndpointUrl,
                    retryCount + 1,
                    maxRetries + 1
                );
            }

            retryCount++;

            if (retryCount <= maxRetries)
            {
                // Exponential backoff
                var delay = settings.RetryDelayMs * (int)Math.Pow(2, retryCount - 1);
                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogError(
            "Failed to forward discrepancy {CorrelationId} after {MaxAttempts} attempts",
            payload.Analysis.TraceId,
            maxRetries + 1
        );

        return false;
    }
}
