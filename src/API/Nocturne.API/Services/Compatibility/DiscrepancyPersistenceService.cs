using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.API.Models.Compatibility;

namespace Nocturne.API.Services.Compatibility;

/// <summary>
/// Persists compatibility-proxy discrepancy analyses to the database and surfaces
/// aggregate metrics for the compatibility dashboard.
/// </summary>
/// <seealso cref="DiscrepancyPersistenceService"/>
public interface IDiscrepancyPersistenceService
{
    /// <summary>
    /// Persists a <see cref="ResponseComparisonResult"/> to the database and fires a fire-and-forget
    /// task to forward it via <see cref="IDiscrepancyForwardingService"/> so the calling request
    /// path is not blocked by the forwarding HTTP call.
    /// </summary>
    Task<Guid> StoreAnalysisAsync(
        ResponseComparisonResult comparisonResult,
        CompatibilityProxyResponse compatibilityProxyResponse,
        string requestMethod,
        string requestPath,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns aggregate compatibility metrics (match rates, discrepancy counts by severity) for the specified UTC date range.</summary>
    Task<CompatibilityMetrics> GetCompatibilityMetricsAsync(
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns per-endpoint discrepancy metrics for the specified UTC date range, useful for identifying which API paths have the most compatibility gaps.</summary>
    Task<IEnumerable<EndpointMetrics>> GetEndpointMetricsAsync(
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Deletes discrepancy analyses older than <paramref name="retentionDays"/> days.
    /// Intended to be called from a scheduled background sweep.
    /// </summary>
    Task<int> CleanupOldDataAsync(
        int retentionDays = 30,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// <see cref="IDiscrepancyPersistenceService"/> implementation backed by
/// <see cref="Infrastructure.Data.Abstractions.IDiscrepancyAnalysisRepository"/>.
/// </summary>
/// <remarks>
/// After each successful database write, forwarding is kicked off as a fire-and-forget
/// <c>Task.Run</c> so that remote forwarding latency does not affect the response time of
/// the mirrored request. Forwarding failures are logged as warnings and do not surface to
/// callers.
/// </remarks>
/// <seealso cref="IDiscrepancyPersistenceService"/>
public class DiscrepancyPersistenceService : IDiscrepancyPersistenceService
{
    private readonly IDiscrepancyAnalysisRepository _repository;
    private readonly IDiscrepancyForwardingService _forwardingService;
    private readonly ILogger<DiscrepancyPersistenceService> _logger;

    /// <summary>
    /// Initializes a new instance of the DiscrepancyPersistenceService class
    /// </summary>
    /// <param name="repository">Repository for discrepancy analysis operations</param>
    /// <param name="forwardingService">Service for forwarding discrepancies to remote endpoints</param>
    /// <param name="logger">Logger instance for this service</param>
    public DiscrepancyPersistenceService(
        IDiscrepancyAnalysisRepository repository,
        IDiscrepancyForwardingService forwardingService,
        ILogger<DiscrepancyPersistenceService> logger
    )
    {
        _repository = repository;
        _forwardingService = forwardingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Guid> StoreAnalysisAsync(
        ResponseComparisonResult comparisonResult,
        CompatibilityProxyResponse compatibilityProxyResponse,
        string requestMethod,
        string requestPath,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Storing discrepancy analysis for correlation {CorrelationId}",
                comparisonResult.TraceId
            );

            // Convert the compatibility proxy models to simple data structures
            var discrepancies = comparisonResult
                .Discrepancies.Select(d => new DiscrepancyDetailData
                {
                    Type = d.Type,
                    Severity = d.Severity,
                    Field = d.Field,
                    NightscoutValue = d.NightscoutValue,
                    NocturneValue = d.NocturneValue,
                    Description = d.Description,
                })
                .ToList();

            var analysisId = await _repository.StoreAnalysisAsync(
                comparisonResult.TraceId,
                comparisonResult.ComparisonTimestamp,
                requestMethod,
                requestPath,
                (int)comparisonResult.OverallMatch,
                comparisonResult.StatusCodeMatch,
                comparisonResult.BodyMatch,
                GetStatusCodeFromResponse(compatibilityProxyResponse.NightscoutResponse),
                GetStatusCodeFromResponse(compatibilityProxyResponse.NocturneResponse),
                comparisonResult.PerformanceComparison?.NightscoutResponseTime,
                comparisonResult.PerformanceComparison?.NocturneResponseTime,
                compatibilityProxyResponse.TotalProcessingTimeMs,
                comparisonResult.Summary,
                compatibilityProxyResponse.SelectedResponse?.Target,
                compatibilityProxyResponse.SelectionReason,
                discrepancies,
                comparisonResult.OverallMatch
                    == Nocturne.Core.Models.ResponseMatchType.NightscoutMissing
                    || comparisonResult.OverallMatch
                        == Nocturne.Core.Models.ResponseMatchType.BothMissing,
                comparisonResult.OverallMatch
                    == Nocturne.Core.Models.ResponseMatchType.NocturneMissing
                    || comparisonResult.OverallMatch
                        == Nocturne.Core.Models.ResponseMatchType.BothMissing,
                comparisonResult.OverallMatch
                == Nocturne.Core.Models.ResponseMatchType.ComparisonError
                    ? comparisonResult.Summary
                    : null,
                cancellationToken
            );

            _logger.LogDebug(
                "Stored discrepancy analysis {AnalysisId} for correlation {CorrelationId}",
                analysisId,
                comparisonResult.TraceId
            );

            // Forward to remote endpoint if configured (fire-and-forget, don't block)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _forwardingService.ForwardDiscrepancyAsync(
                        comparisonResult,
                        requestMethod,
                        requestPath,
                        CancellationToken.None
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Error forwarding discrepancy {CorrelationId} to remote endpoint",
                        comparisonResult.TraceId
                    );
                }
            });

            return analysisId;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error storing discrepancy analysis for correlation {CorrelationId}",
                comparisonResult.TraceId
            );
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CompatibilityMetrics> GetCompatibilityMetricsAsync(
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await _repository.GetCompatibilityMetricsAsync(
                fromDate,
                toDate,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving compatibility metrics");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<EndpointMetrics>> GetEndpointMetricsAsync(
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await _repository.GetEndpointMetricsAsync(fromDate, toDate, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving endpoint metrics");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> CleanupOldDataAsync(
        int retentionDays = 30,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            var deletedCount = await _repository.DeleteOldAnalysesAsync(
                cutoffDate,
                cancellationToken
            );

            _logger.LogInformation(
                "Cleaned up {DeletedCount} old discrepancy analyses older than {CutoffDate}",
                deletedCount,
                cutoffDate
            );

            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old discrepancy data");
            throw;
        }
    }

    private static int? GetStatusCodeFromResponse(TargetResponse? response)
    {
        return response?.StatusCode;
    }
}
