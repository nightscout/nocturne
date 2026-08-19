using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// Repository for discrepancy analysis operations
/// </summary>
public class DiscrepancyAnalysisRepository : IDiscrepancyAnalysisRepository
{
    private readonly NocturneDbContext _context;

    /// <summary>
    /// Initializes a new instance of the DiscrepancyAnalysisRepository class
    /// </summary>
    /// <param name="context">The database context</param>
    public DiscrepancyAnalysisRepository(NocturneDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Store a discrepancy analysis result
    /// </summary>
    /// <param name="traceId">The request trace identifier.</param>
    /// <param name="analysisTimestamp">The timestamp when the analysis was performed.</param>
    /// <param name="requestMethod">The HTTP request method (e.g., GET, POST).</param>
    /// <param name="requestPath">The endpoint path.</param>
    /// <param name="overallMatch">The overall match level as an integer.</param>
    /// <param name="statusCodeMatch">Whether the HTTP status codes matched.</param>
    /// <param name="bodyMatch">Whether the response bodies matched.</param>
    /// <param name="nightscoutStatusCode">The status code returned by Nightscout.</param>
    /// <param name="nocturneStatusCode">The status code returned by Nocturne.</param>
    /// <param name="nightscoutResponseTimeMs">Nightscout response time in milliseconds.</param>
    /// <param name="nocturneResponseTimeMs">Nocturne response time in milliseconds.</param>
    /// <param name="totalProcessingTimeMs">Total time spent performing the analysis.</param>
    /// <param name="summary">A brief summary of the analysis results.</param>
    /// <param name="selectedResponseTarget">The response target that was eventually selected.</param>
    /// <param name="selectionReason">The reason for selecting that response target.</param>
    /// <param name="discrepancies">A list of detected discrepancies.</param>
    /// <param name="nightscoutMissing">Whether the Nightscout response was missing.</param>
    /// <param name="nocturneMissing">Whether the Nocturne response was missing.</param>
    /// <param name="errorMessage">Any error message encountered during analysis.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The unique identifier of the stored analysis record.</returns>
    public async Task<Guid> StoreAnalysisAsync(
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
        CancellationToken cancellationToken = default
    )
    {
        var entity = new DiscrepancyAnalysisEntity
        {
            Id = Guid.CreateVersion7(),
            TraceId = traceId,
            AnalysisTimestamp = analysisTimestamp,
            RequestMethod = requestMethod,
            RequestPath = requestPath,
            OverallMatch = (ResponseMatchType)overallMatch,
            StatusCodeMatch = statusCodeMatch,
            BodyMatch = bodyMatch,
            NightscoutStatusCode = nightscoutStatusCode,
            NocturneStatusCode = nocturneStatusCode,
            NightscoutResponseTimeMs = nightscoutResponseTimeMs,
            NocturneResponseTimeMs = nocturneResponseTimeMs,
            TotalProcessingTimeMs = totalProcessingTimeMs,
            Summary = summary,
            SelectedResponseTarget = selectedResponseTarget,
            SelectionReason = selectionReason,
            CriticalDiscrepancyCount = discrepancies.Count(d => d.Severity == DiscrepancySeverity.Critical),
            MajorDiscrepancyCount = discrepancies.Count(d => d.Severity == DiscrepancySeverity.Major),
            MinorDiscrepancyCount = discrepancies.Count(d => d.Severity == DiscrepancySeverity.Minor),
            NightscoutMissing = nightscoutMissing,
            NocturneMissing = nocturneMissing,
            ErrorMessage = errorMessage,
        };

        _context.DiscrepancyAnalyses.Add(entity);

        // Add detailed discrepancies
        foreach (var discrepancy in discrepancies)
        {
            var detailEntity = new DiscrepancyDetailEntity
            {
                Id = Guid.CreateVersion7(),
                AnalysisId = entity.Id,
                DiscrepancyType = discrepancy.Type,
                Severity = discrepancy.Severity,
                Field = discrepancy.Field,
                NightscoutValue = discrepancy.NightscoutValue,
                NocturneValue = discrepancy.NocturneValue,
                Description = discrepancy.Description,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            _context.DiscrepancyDetails.Add(detailEntity);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    /// <summary>
    /// Get analyses with filtering and pagination
    /// </summary>
    /// <param name="requestPath">Optional endpoint path filter.</param>
    /// <param name="overallMatch">Optional match level filter.</param>
    /// <param name="fromDate">Optional start date filter.</param>
    /// <param name="toDate">Optional end date filter.</param>
    /// <param name="count">The maximum number of records to return.</param>
    /// <param name="skip">The number of records to skip.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of matching discrepancy analysis entities.</returns>
    public async Task<IEnumerable<DiscrepancyAnalysisEntity>> GetAnalysesAsync(
        string? requestPath = null,
        int? overallMatch = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int count = 100,
        int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.DiscrepancyAnalyses.AsQueryable();

        if (!string.IsNullOrEmpty(requestPath))
        {
            query = query.Where(a => a.RequestPath.Contains(requestPath));
        }

        if (overallMatch.HasValue)
        {
            query = query.Where(a => a.OverallMatch == (ResponseMatchType)overallMatch.Value);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(a => a.AnalysisTimestamp >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            query = query.Where(a => a.AnalysisTimestamp <= toDate.Value);
        }

        return await query
            .OrderByDescending(a => a.AnalysisTimestamp)
            .Skip(skip)
            .Take(count)
            .Include(a => a.Discrepancies)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Get compatibility metrics for dashboard
    /// </summary>
    /// <param name="fromDate">Optional start date for metrics calculation.</param>
    /// <param name="toDate">Optional end date for metrics calculation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The calculated compatibility metrics.</returns>
    public async Task<CompatibilityMetrics> GetCompatibilityMetricsAsync(
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.DiscrepancyAnalyses.AsQueryable();

        if (fromDate.HasValue)
        {
            query = query.Where(a => a.AnalysisTimestamp >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            query = query.Where(a => a.AnalysisTimestamp <= toDate.Value);
        }

        var total = await query.CountAsync(cancellationToken);
        var perfect = await query.CountAsync(
            a => a.OverallMatch == ResponseMatchType.Perfect,
            cancellationToken
        );
        var minor = await query.CountAsync(
            a => a.OverallMatch == ResponseMatchType.MinorDifferences,
            cancellationToken
        );
        var major = await query.CountAsync(
            a => a.OverallMatch == ResponseMatchType.MajorDifferences,
            cancellationToken
        );
        var critical = await query.CountAsync(
            a => a.OverallMatch == ResponseMatchType.CriticalDifferences,
            cancellationToken
        );

        var avgNightscoutResponseTime =
            await query
                .Where(a => a.NightscoutResponseTimeMs.HasValue)
                .AverageAsync(a => (double?)a.NightscoutResponseTimeMs!.Value, cancellationToken)
            ?? 0;

        var avgNocturneResponseTime =
            await query
                .Where(a => a.NocturneResponseTimeMs.HasValue)
                .AverageAsync(a => (double?)a.NocturneResponseTimeMs!.Value, cancellationToken)
            ?? 0;

        return new CompatibilityMetrics
        {
            TotalRequests = total,
            PerfectMatches = perfect,
            MinorDifferences = minor,
            MajorDifferences = major,
            CriticalDifferences = critical,
            CompatibilityScore = total > 0 ? (double)(perfect + minor) / total * 100 : 100,
            AverageNightscoutResponseTime = avgNightscoutResponseTime,
            AverageNocturneResponseTime = avgNocturneResponseTime,
        };
    }

    /// <summary>
    /// Get endpoint-specific metrics
    /// </summary>
    /// <param name="fromDate">Optional start date for metrics calculation.</param>
    /// <param name="toDate">Optional end date for metrics calculation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A collection of metrics for each endpoint.</returns>
    public async Task<IEnumerable<EndpointMetrics>> GetEndpointMetricsAsync(
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = _context.DiscrepancyAnalyses.AsQueryable();

        if (fromDate.HasValue)
        {
            query = query.Where(a => a.AnalysisTimestamp >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            query = query.Where(a => a.AnalysisTimestamp <= toDate.Value);
        }

        var endpointStats = await query
            .GroupBy(a => a.RequestPath)
            .Select(g => new EndpointMetrics
            {
                Endpoint = g.Key,
                TotalRequests = g.Count(),
                PerfectMatches = g.Count(a => a.OverallMatch == ResponseMatchType.Perfect),
                MinorDifferences = g.Count(a =>
                    a.OverallMatch == ResponseMatchType.MinorDifferences
                ),
                MajorDifferences = g.Count(a =>
                    a.OverallMatch == ResponseMatchType.MajorDifferences
                ),
                CriticalDifferences = g.Count(a =>
                    a.OverallMatch == ResponseMatchType.CriticalDifferences
                ),
                AverageNightscoutResponseTime =
                    g.Where(a => a.NightscoutResponseTimeMs.HasValue)
                        .Average(a => (double?)a.NightscoutResponseTimeMs!.Value) ?? 0,
                AverageNocturneResponseTime =
                    g.Where(a => a.NocturneResponseTimeMs.HasValue)
                        .Average(a => (double?)a.NocturneResponseTimeMs!.Value) ?? 0,
            })
            .ToListAsync(cancellationToken);

        return endpointStats.Select(e => new EndpointMetrics
        {
            Endpoint = e.Endpoint,
            TotalRequests = e.TotalRequests,
            PerfectMatches = e.PerfectMatches,
            MinorDifferences = e.MinorDifferences,
            MajorDifferences = e.MajorDifferences,
            CriticalDifferences = e.CriticalDifferences,
            CompatibilityScore =
                e.TotalRequests > 0
                    ? (double)(e.PerfectMatches + e.MinorDifferences) / e.TotalRequests * 100
                    : 100,
            AverageNightscoutResponseTime = e.AverageNightscoutResponseTime,
            AverageNocturneResponseTime = e.AverageNocturneResponseTime,
        });
    }

    /// <summary>
    /// Delete old analyses based on retention policy
    /// </summary>
    /// <param name="cutoffDate">The date before which records should be deleted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    public async Task<int> DeleteOldAnalysesAsync(
        DateTimeOffset cutoffDate,
        CancellationToken cancellationToken = default
    )
    {
        var oldAnalyses = await _context
            .DiscrepancyAnalyses.Where(a => a.AnalysisTimestamp < cutoffDate)
            .ToListAsync(cancellationToken);

        if (oldAnalyses.Any())
        {
            _context.DiscrepancyAnalyses.RemoveRange(oldAnalyses);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return oldAnalyses.Count;
    }
}

/// <summary>
/// Simple data transfer object for discrepancy details
/// </summary>
public class DiscrepancyDetailData
{
    /// <summary>
    /// Gets or sets the type of discrepancy
    /// </summary>
    public DiscrepancyType Type { get; set; }

    /// <summary>
    /// Gets or sets the severity level of the discrepancy
    /// </summary>
    public DiscrepancySeverity Severity { get; set; }

    /// <summary>
    /// Gets or sets the field name where the discrepancy occurred
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the value from Nightscout response
    /// </summary>
    public string NightscoutValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the value from Nocturne response
    /// </summary>
    public string NocturneValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the description of the discrepancy
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
