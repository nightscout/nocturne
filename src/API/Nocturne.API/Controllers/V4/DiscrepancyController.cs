using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services.Compatibility;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for discrepancy analysis and compatibility dashboard
/// Provides endpoints for monitoring Nightscout/Nocturne compatibility
/// </summary>
[ApiController]
[Route("api/v4/[controller]")]
[Produces("application/json")]
[Tags("V4 Discrepancy")]
public class DiscrepancyController : ControllerBase
{
    private readonly DiscrepancyAnalysisRepository _discrepancyRepository;

    private readonly ILogger<DiscrepancyController> _logger;

    public DiscrepancyController(
        DiscrepancyAnalysisRepository discrepancyRepository,
        ILogger<DiscrepancyController> logger
    )
    {
        _discrepancyRepository = discrepancyRepository;

        _logger = logger;
    }

    /// <summary>
    /// Get overall compatibility metrics for dashboard overview
    /// </summary>
    /// <param name="fromDate">Start date for metrics (optional)</param>
    /// <param name="toDate">End date for metrics (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Compatibility metrics including success rate and response times</returns>
    [HttpGet("metrics")]
    public async Task<ActionResult<CompatibilityMetrics>> GetCompatibilityMetrics(
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Retrieving compatibility metrics from {FromDate} to {ToDate}",
                fromDate,
                toDate
            );

            var metrics = await _discrepancyRepository.GetCompatibilityMetricsAsync(
                fromDate,
                toDate,
                cancellationToken
            );

            return Ok(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving compatibility metrics");
            return StatusCode(500, "Error retrieving compatibility metrics");
        }
    }

    /// <summary>
    /// Get per-endpoint compatibility metrics
    /// </summary>
    /// <param name="fromDate">Start date for metrics (optional)</param>
    /// <param name="toDate">End date for metrics (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of endpoint-specific compatibility metrics</returns>
    [HttpGet("endpoints")]
    public async Task<ActionResult<IEnumerable<EndpointMetrics>>> GetEndpointMetrics(
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogDebug(
                "Retrieving endpoint metrics from {FromDate} to {ToDate}",
                fromDate,
                toDate
            );

            var metrics = await _discrepancyRepository.GetEndpointMetricsAsync(
                fromDate,
                toDate,
                cancellationToken
            );

            return Ok(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving endpoint metrics");
            return StatusCode(500, "Error retrieving endpoint metrics");
        }
    }

    /// <summary>
    /// Get detailed discrepancy analyses with filtering and pagination
    /// </summary>
    /// <param name="requestPath">Filter by request path (optional)</param>
    /// <param name="overallMatch">Filter by overall match type (optional)</param>
    /// <param name="fromDate">Start date for filter (optional)</param>
    /// <param name="toDate">End date for filter (optional)</param>
    /// <param name="count">Number of results to return (default: 100, max: 1000)</param>
    /// <param name="skip">Number of results to skip for pagination (default: 0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of detailed discrepancy analyses</returns>
    [HttpGet("analyses")]
    public async Task<ActionResult<IEnumerable<DiscrepancyAnalysisDto>>> GetDiscrepancyAnalyses(
        [FromQuery] string? requestPath = null,
        [FromQuery] int? overallMatch = null,
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        [FromQuery] int count = 100,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            // Validate parameters
            if (count <= 0)
            {
                return BadRequest("Count must be positive");
            }

            if (skip < 0)
            {
                return BadRequest("Skip must be non-negative");
            }

            _logger.LogDebug(
                "Retrieving discrepancy analyses: path={RequestPath}, match={OverallMatch}, count={Count}, skip={Skip}",
                requestPath,
                overallMatch,
                count,
                skip
            );

            var analyses = await _discrepancyRepository.GetAnalysesAsync(
                requestPath,
                overallMatch,
                fromDate,
                toDate,
                count,
                skip,
                cancellationToken
            );

            // Convert to DTOs to avoid exposing internal entities
            var analysisResults = analyses.Select(a => new DiscrepancyAnalysisDto
            {
                Id = a.Id,
                CorrelationId = a.CorrelationId,
                AnalysisTimestamp = a.AnalysisTimestamp,
                RequestMethod = a.RequestMethod,
                RequestPath = a.RequestPath,
                OverallMatch = (int)a.OverallMatch,
                StatusCodeMatch = a.StatusCodeMatch,
                BodyMatch = a.BodyMatch,
                NightscoutStatusCode = a.NightscoutStatusCode,
                NocturneStatusCode = a.NocturneStatusCode,
                NightscoutResponseTimeMs = a.NightscoutResponseTimeMs,
                NocturneResponseTimeMs = a.NocturneResponseTimeMs,
                TotalProcessingTimeMs = a.TotalProcessingTimeMs,
                Summary = a.Summary,
                SelectedResponseTarget = a.SelectedResponseTarget,
                SelectionReason = a.SelectionReason,
                CriticalDiscrepancyCount = a.CriticalDiscrepancyCount,
                MajorDiscrepancyCount = a.MajorDiscrepancyCount,
                MinorDiscrepancyCount = a.MinorDiscrepancyCount,
                NightscoutMissing = a.NightscoutMissing,
                NocturneMissing = a.NocturneMissing,
                ErrorMessage = a.ErrorMessage,
                Discrepancies = a
                    .Discrepancies.Select(d => new DiscrepancyDetailDto
                    {
                        Id = d.Id,
                        DiscrepancyType = d.DiscrepancyType,
                        Severity = d.Severity,
                        Field = d.Field,
                        NightscoutValue = d.NightscoutValue,
                        NocturneValue = d.NocturneValue,
                        Description = d.Description,
                        RecordedAt = d.RecordedAt,
                    })
                    .ToList(),
            });

            return Ok(analysisResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving discrepancy analyses");
            return StatusCode(500, "Error retrieving discrepancy analyses");
        }
    }

    /// <summary>
    /// Get a specific discrepancy analysis by ID
    /// </summary>
    /// <param name="id">Analysis ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed discrepancy analysis</returns>
    [HttpGet("analyses/{id:guid}")]
    public async Task<ActionResult<DiscrepancyAnalysisDto>> GetDiscrepancyAnalysis(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var analyses = await _discrepancyRepository.GetAnalysesAsync(
                count: 1,
                skip: 0,
                cancellationToken: cancellationToken
            );

            var analysis = analyses.FirstOrDefault(a => a.Id == id);
            if (analysis == null)
            {
                return NotFound($"Discrepancy analysis with ID {id} not found");
            }

            var result = new DiscrepancyAnalysisDto
            {
                Id = analysis.Id,
                CorrelationId = analysis.CorrelationId,
                AnalysisTimestamp = analysis.AnalysisTimestamp,
                RequestMethod = analysis.RequestMethod,
                RequestPath = analysis.RequestPath,
                OverallMatch = (int)analysis.OverallMatch,
                StatusCodeMatch = analysis.StatusCodeMatch,
                BodyMatch = analysis.BodyMatch,
                NightscoutStatusCode = analysis.NightscoutStatusCode,
                NocturneStatusCode = analysis.NocturneStatusCode,
                NightscoutResponseTimeMs = analysis.NightscoutResponseTimeMs,
                NocturneResponseTimeMs = analysis.NocturneResponseTimeMs,
                TotalProcessingTimeMs = analysis.TotalProcessingTimeMs,
                Summary = analysis.Summary,
                SelectedResponseTarget = analysis.SelectedResponseTarget,
                SelectionReason = analysis.SelectionReason,
                CriticalDiscrepancyCount = analysis.CriticalDiscrepancyCount,
                MajorDiscrepancyCount = analysis.MajorDiscrepancyCount,
                MinorDiscrepancyCount = analysis.MinorDiscrepancyCount,
                NightscoutMissing = analysis.NightscoutMissing,
                NocturneMissing = analysis.NocturneMissing,
                ErrorMessage = analysis.ErrorMessage,
                Discrepancies = analysis
                    .Discrepancies.Select(d => new DiscrepancyDetailDto
                    {
                        Id = d.Id,
                        DiscrepancyType = d.DiscrepancyType,
                        Severity = d.Severity,
                        Field = d.Field,
                        NightscoutValue = d.NightscoutValue,
                        NocturneValue = d.NocturneValue,
                        Description = d.Description,
                        RecordedAt = d.RecordedAt,
                    })
                    .ToList(),
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving discrepancy analysis {Id}", id);
            return StatusCode(500, "Error retrieving discrepancy analysis");
        }
    }

    /// <summary>
    /// Get real-time compatibility status summary
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current compatibility status</returns>
    [HttpGet("status")]
    public async Task<ActionResult<CompatibilityStatus>> GetCompatibilityStatus(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            // Get metrics for the last 24 hours
            var last24Hours = DateTimeOffset.UtcNow.AddHours(-24);
            var metrics = await _discrepancyRepository.GetCompatibilityMetricsAsync(
                last24Hours,
                null,
                cancellationToken
            );

            var status = new CompatibilityStatus
            {
                OverallScore = metrics.CompatibilityScore,
                TotalRequests = metrics.TotalRequests,
                HealthStatus = DetermineHealthStatus(metrics),
                LastUpdated = DateTimeOffset.UtcNow,
                CriticalIssues = metrics.CriticalDifferences,
                MajorIssues = metrics.MajorDifferences,
                MinorIssues = metrics.MinorDifferences,
            };

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving compatibility status");
            return StatusCode(500, "Error retrieving compatibility status");
        }
    }

    private static string DetermineHealthStatus(CompatibilityMetrics metrics)
    {
        if (metrics.TotalRequests == 0)
        {
            return "No Data";
        }

        return metrics.CompatibilityScore switch
        {
            >= 95 => "Excellent",
            >= 85 => "Good",
            >= 70 => "Fair",
            >= 50 => "Poor",
            _ => "Critical",
        };
    }





    /// <summary>
    /// Receive forwarded discrepancies from remote Nocturne instances
    /// </summary>
    /// <param name="request">The forwarded discrepancy data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Acknowledgement of receipt</returns>
    [HttpPost("ingest")]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(typeof(object), 400)]
    [ProducesResponseType(typeof(object), 401)]
    public async Task<ActionResult> IngestDiscrepancy(
        [FromBody] ForwardedDiscrepancyDto request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (request == null || request.Analysis == null)
            {
                return BadRequest(new { status = 400, message = "Invalid request body" });
            }

            var sourceId = request.SourceId;
            var correlationId = request.Analysis.CorrelationId;

            _logger.LogInformation(
                "Received forwarded discrepancy from {SourceId}: {CorrelationId}",
                sourceId,
                correlationId
            );

            // Store the forwarded discrepancy in the database
            // Mark the source to distinguish from local discrepancies
            var discrepancies = request.Analysis.Discrepancies
                .Select(d => new Nocturne.Infrastructure.Data.Repositories.DiscrepancyDetailData
                {
                    Type = d.DiscrepancyType,
                    Severity = d.Severity,
                    Field = d.Field,
                    NightscoutValue = d.NightscoutValue,
                    NocturneValue = d.NocturneValue,
                    Description = d.Description,
                })
                .ToList();

            await _discrepancyRepository.StoreAnalysisAsync(
                correlationId,
                request.Analysis.AnalysisTimestamp,
                request.Analysis.RequestMethod,
                request.Analysis.RequestPath,
                request.Analysis.OverallMatch,
                request.Analysis.StatusCodeMatch,
                request.Analysis.BodyMatch,
                request.Analysis.NightscoutStatusCode,
                request.Analysis.NocturneStatusCode,
                request.Analysis.NightscoutResponseTimeMs,
                request.Analysis.NocturneResponseTimeMs,
                request.Analysis.TotalProcessingTimeMs,
                $"[{sourceId}] {request.Analysis.Summary}",
                request.Analysis.SelectedResponseTarget,
                request.Analysis.SelectionReason,
                discrepancies,
                request.Analysis.NightscoutMissing,
                request.Analysis.NocturneMissing,
                request.Analysis.ErrorMessage,
                cancellationToken
            );

            _logger.LogDebug(
                "Stored forwarded discrepancy from {SourceId}: {CorrelationId}",
                sourceId,
                correlationId
            );

            return Ok(new
            {
                status = 200,
                message = "Discrepancy received and stored",
                correlationId,
                sourceId,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error ingesting forwarded discrepancy from {SourceId}",
                request?.SourceId ?? "unknown"
            );
            return StatusCode(500, new { status = 500, message = "Error processing forwarded discrepancy" });
        }
    }
}
