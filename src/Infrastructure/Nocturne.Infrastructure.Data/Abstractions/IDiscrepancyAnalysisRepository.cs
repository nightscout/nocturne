using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.Infrastructure.Data.Abstractions;

/// <summary>
/// Repository port for discrepancy analysis operations
/// </summary>
public interface IDiscrepancyAnalysisRepository
{
    /// <summary>
    /// Stores the results of a discrepancy analysis between Nightscout and Nocturne responses
    /// </summary>
    Task<Guid> StoreAnalysisAsync(
        string traceId,
        DateTimeOffset analysisTimestamp,
        string requestMethod,
        string requestPath,
        int overallMatch,
        bool statusCodeMatch,
        bool bodyMatch,
        int? nightscoutStatusCode,
        int? nocturneStatusCode,
        long? nightscoutResponseTimeMs,
        long? nocturneResponseTimeMs,
        long totalProcessingTimeMs,
        string summary,
        string? selectedResponseTarget,
        string? selectionReason,
        List<DiscrepancyDetailData> discrepancies,
        bool nightscoutMissing = false,
        bool nocturneMissing = false,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a collection of discrepancy analyses based on filtering criteria
    /// </summary>
    Task<IEnumerable<DiscrepancyAnalysisEntity>> GetAnalysesAsync(
        string? requestPath = null,
        int? overallMatch = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets compatibility metrics for a specified date range
    /// </summary>
    Task<CompatibilityMetrics> GetCompatibilityMetricsAsync(
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metrics per endpoint for a specified date range
    /// </summary>
    Task<IEnumerable<EndpointMetrics>> GetEndpointMetricsAsync(
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes discrepancy analyses older than the specified cutoff date
    /// </summary>
    Task<int> DeleteOldAnalysesAsync(
        DateTimeOffset cutoffDate,
        CancellationToken cancellationToken = default);
}
