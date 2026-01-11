using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Compatibility;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// API controller for compatibility dashboard data
/// </summary>
[ApiController]
[Route("api/v4/compatibility")]
public class CompatibilityController : ControllerBase
{
    private readonly IDiscrepancyPersistenceService _persistenceService;
    private readonly DiscrepancyAnalysisRepository _repository;

    private readonly CompatibilityProxyConfiguration _configuration;
    private readonly ILogger<CompatibilityController> _logger;

    /// <summary>
    /// Initializes a new instance of the CompatibilityController class
    /// </summary>
    public CompatibilityController(
        IDiscrepancyPersistenceService persistenceService,
        DiscrepancyAnalysisRepository repository,
        IOptions<CompatibilityProxyConfiguration> configuration,
        ILogger<CompatibilityController> logger
    )
    {
        _persistenceService = persistenceService;
        _repository = repository;

        _configuration = configuration.Value;
        _logger = logger;
    }

    /// <summary>
    /// Get current proxy configuration
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType(typeof(ProxyConfigurationDto), StatusCodes.Status200OK)]
    public ActionResult<ProxyConfigurationDto> GetConfiguration()
    {
        return Ok(
            new ProxyConfigurationDto
            {
                NightscoutUrl = _configuration.NightscoutUrl,
                DefaultStrategy = _configuration.DefaultStrategy.ToString(),
                EnableDetailedLogging = _configuration.EnableDetailedLogging,
            }
        );
    }

    /// <summary>
    /// Get overall compatibility metrics
    /// </summary>
    [HttpGet("metrics")]
    [ProducesResponseType(typeof(CompatibilityMetrics), StatusCodes.Status200OK)]
    public async Task<ActionResult<CompatibilityMetrics>> GetMetrics(
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var metrics = await _persistenceService.GetCompatibilityMetricsAsync(
                fromDate,
                toDate,
                cancellationToken
            );
            return Ok(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving compatibility metrics");
            return StatusCode(500, new { error = "Failed to retrieve metrics" });
        }
    }

    /// <summary>
    /// Get per-endpoint compatibility metrics
    /// </summary>
    [HttpGet("endpoints")]
    [ProducesResponseType(typeof(IEnumerable<EndpointMetrics>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<EndpointMetrics>>> GetEndpointMetrics(
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var metrics = await _persistenceService.GetEndpointMetricsAsync(
                fromDate,
                toDate,
                cancellationToken
            );
            return Ok(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving endpoint metrics");
            return StatusCode(500, new { error = "Failed to retrieve endpoint metrics" });
        }
    }

    /// <summary>
    /// Get list of analyses with filtering and pagination
    /// </summary>
    [HttpGet("analyses")]
    [ProducesResponseType(typeof(AnalysesListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalysesListResponse>> GetAnalyses(
        [FromQuery] string? requestPath = null,
        [FromQuery] ResponseMatchType? overallMatch = null,
        [FromQuery] string? requestMethod = null,
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        [FromQuery] int count = 100,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var analyses = await _repository.GetAnalysesAsync(
                requestPath,
                overallMatch.HasValue ? (int)overallMatch.Value : null,
                fromDate,
                toDate,
                count,
                skip,
                cancellationToken
            );

            var analysisItems = analyses
                .Select(a => new AnalysisListItemDto
                {
                    Id = a.Id,
                    CorrelationId = a.CorrelationId,
                    AnalysisTimestamp = a.AnalysisTimestamp,
                    RequestMethod = a.RequestMethod,
                    RequestPath = a.RequestPath,
                    OverallMatch = a.OverallMatch,
                    StatusCodeMatch = a.StatusCodeMatch,
                    BodyMatch = a.BodyMatch,
                    NightscoutStatusCode = a.NightscoutStatusCode,
                    NocturneStatusCode = a.NocturneStatusCode,
                    NightscoutResponseTimeMs = a.NightscoutResponseTimeMs,
                    NocturneResponseTimeMs = a.NocturneResponseTimeMs,
                    TotalProcessingTimeMs = a.TotalProcessingTimeMs,
                    Summary = a.Summary,
                    CriticalDiscrepancyCount = a.CriticalDiscrepancyCount,
                    MajorDiscrepancyCount = a.MajorDiscrepancyCount,
                    MinorDiscrepancyCount = a.MinorDiscrepancyCount,
                    NightscoutMissing = a.NightscoutMissing,
                    NocturneMissing = a.NocturneMissing,
                })
                .ToList();

            return Ok(new AnalysesListResponse { Analyses = analysisItems, Total = analysisItems.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving analyses");
            return StatusCode(500, new { error = "Failed to retrieve analyses" });
        }
    }

    /// <summary>
    /// Get detailed analysis by ID
    /// </summary>
    [HttpGet("analyses/{id}")]
    [ProducesResponseType(typeof(AnalysisDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalysisDetailDto>> GetAnalysisDetail(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var analyses = await _repository.GetAnalysesAsync(
                null,
                null,
                null,
                null,
                1,
                0,
                cancellationToken
            );

            var analysis = analyses.FirstOrDefault(a => a.Id == id);

            if (analysis == null)
            {
                return NotFound(new { error = "Analysis not found" });
            }

            var detail = new AnalysisDetailDto
            {
                Id = analysis.Id,
                CorrelationId = analysis.CorrelationId,
                AnalysisTimestamp = analysis.AnalysisTimestamp,
                RequestMethod = analysis.RequestMethod,
                RequestPath = analysis.RequestPath,
                OverallMatch = analysis.OverallMatch,
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

            return Ok(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving analysis detail for {Id}", id);
            return StatusCode(500, new { error = "Failed to retrieve analysis detail" });
        }
    }





    /// <summary>
    /// Test API compatibility by comparing responses from Nightscout and Nocturne
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(ManualTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ManualTestResult>> TestApiComparison(
        [FromBody] ManualTestRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(request.NightscoutUrl))
        {
            return BadRequest(new { error = "NightscoutUrl is required" });
        }

        if (string.IsNullOrWhiteSpace(request.QueryPath))
        {
            return BadRequest(new { error = "QueryPath is required" });
        }

        var result = new ManualTestResult
        {
            QueryPath = request.QueryPath,
            Method = request.Method ?? "GET",
            Timestamp = DateTimeOffset.UtcNow,
        };

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Build URLs
        var nightscoutBaseUrl = request.NightscoutUrl.TrimEnd('/');
        var queryPath = request.QueryPath.StartsWith("/") ? request.QueryPath : "/" + request.QueryPath;
        var nightscoutUrl = nightscoutBaseUrl + queryPath;

        // Get Nocturne base URL from current request
        var nocturneBaseUrl = $"{Request.Scheme}://{Request.Host}";
        var nocturneUrl = nocturneBaseUrl + queryPath;

        _logger.LogInformation(
            "Manual compatibility test: Nightscout={NightscoutUrl}, Nocturne={NocturneUrl}",
            nightscoutUrl,
            nocturneUrl
        );

        // Fetch from Nightscout
        var nsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var nsRequest = new HttpRequestMessage(
                request.Method?.ToUpperInvariant() == "POST" ? HttpMethod.Post : HttpMethod.Get,
                nightscoutUrl
            );

            if (!string.IsNullOrWhiteSpace(request.ApiSecret))
            {
                nsRequest.Headers.Add("api-secret", request.ApiSecret);
            }

            if (!string.IsNullOrEmpty(request.RequestBody) && nsRequest.Method == HttpMethod.Post)
            {
                nsRequest.Content = new StringContent(
                    request.RequestBody,
                    System.Text.Encoding.UTF8,
                    "application/json"
                );
            }

            var nsResponse = await httpClient.SendAsync(nsRequest, cancellationToken);
            nsStopwatch.Stop();

            result.NightscoutStatusCode = (int)nsResponse.StatusCode;
            result.NightscoutResponseTimeMs = nsStopwatch.ElapsedMilliseconds;
            result.NightscoutResponse = await nsResponse.Content.ReadAsStringAsync(cancellationToken);

            // Try to format JSON for better diffing
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(result.NightscoutResponse);
                result.NightscoutResponse = System.Text.Json.JsonSerializer.Serialize(
                    json,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                );
            }
            catch
            {
                // Not JSON, keep raw response
            }
        }
        catch (Exception ex)
        {
            nsStopwatch.Stop();
            result.NightscoutResponseTimeMs = nsStopwatch.ElapsedMilliseconds;
            result.NightscoutError = $"Failed to connect: {ex.Message}";
            _logger.LogWarning(ex, "Error fetching from Nightscout: {Url}", nightscoutUrl);
        }

        // Fetch from Nocturne
        var ncStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var ncRequest = new HttpRequestMessage(
                request.Method?.ToUpperInvariant() == "POST" ? HttpMethod.Post : HttpMethod.Get,
                nocturneUrl
            );

            if (!string.IsNullOrWhiteSpace(request.ApiSecret))
            {
                ncRequest.Headers.Add("api-secret", request.ApiSecret);
            }

            if (!string.IsNullOrEmpty(request.RequestBody) && ncRequest.Method == HttpMethod.Post)
            {
                ncRequest.Content = new StringContent(
                    request.RequestBody,
                    System.Text.Encoding.UTF8,
                    "application/json"
                );
            }

            var ncResponse = await httpClient.SendAsync(ncRequest, cancellationToken);
            ncStopwatch.Stop();

            result.NocturneStatusCode = (int)ncResponse.StatusCode;
            result.NocturneResponseTimeMs = ncStopwatch.ElapsedMilliseconds;
            result.NocturneResponse = await ncResponse.Content.ReadAsStringAsync(cancellationToken);

            // Try to format JSON for better diffing
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(result.NocturneResponse);
                result.NocturneResponse = System.Text.Json.JsonSerializer.Serialize(
                    json,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                );
            }
            catch
            {
                // Not JSON, keep raw response
            }
        }
        catch (Exception ex)
        {
            ncStopwatch.Stop();
            result.NocturneResponseTimeMs = ncStopwatch.ElapsedMilliseconds;
            result.NocturneError = $"Failed to connect: {ex.Message}";
            _logger.LogWarning(ex, "Error fetching from Nocturne: {Url}", nocturneUrl);
        }

        return Ok(result);
    }
}

/// <summary>
/// Proxy configuration DTO
/// </summary>
public class ProxyConfigurationDto
{
    public string NightscoutUrl { get; set; } = string.Empty;
    public string DefaultStrategy { get; set; } = string.Empty;
    public bool EnableDetailedLogging { get; set; }
}

/// <summary>
/// Analysis list item DTO
/// </summary>
public class AnalysisListItemDto
{
    public Guid Id { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset AnalysisTimestamp { get; set; }
    public string RequestMethod { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public ResponseMatchType OverallMatch { get; set; }
    public bool StatusCodeMatch { get; set; }
    public bool BodyMatch { get; set; }
    public int? NightscoutStatusCode { get; set; }
    public int? NocturneStatusCode { get; set; }
    public long? NightscoutResponseTimeMs { get; set; }
    public long? NocturneResponseTimeMs { get; set; }
    public long TotalProcessingTimeMs { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int CriticalDiscrepancyCount { get; set; }
    public int MajorDiscrepancyCount { get; set; }
    public int MinorDiscrepancyCount { get; set; }
    public bool NightscoutMissing { get; set; }
    public bool NocturneMissing { get; set; }
}

/// <summary>
/// Analyses list response
/// </summary>
public class AnalysesListResponse
{
    public List<AnalysisListItemDto> Analyses { get; set; } = new();
    public int Total { get; set; }
}

/// <summary>
/// Analysis detail DTO
/// </summary>
public class AnalysisDetailDto
{
    public Guid Id { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset AnalysisTimestamp { get; set; }
    public string RequestMethod { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public ResponseMatchType OverallMatch { get; set; }
    public bool StatusCodeMatch { get; set; }
    public bool BodyMatch { get; set; }
    public int? NightscoutStatusCode { get; set; }
    public int? NocturneStatusCode { get; set; }
    public long? NightscoutResponseTimeMs { get; set; }
    public long? NocturneResponseTimeMs { get; set; }
    public long TotalProcessingTimeMs { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? SelectedResponseTarget { get; set; }
    public string? SelectionReason { get; set; }
    public int CriticalDiscrepancyCount { get; set; }
    public int MajorDiscrepancyCount { get; set; }
    public int MinorDiscrepancyCount { get; set; }
    public bool NightscoutMissing { get; set; }
    public bool NocturneMissing { get; set; }
    public string? ErrorMessage { get; set; }
    public List<DiscrepancyDetailDto> Discrepancies { get; set; } = new();
}

/// <summary>
/// Request for manual API comparison test
/// </summary>
public class ManualTestRequest
{
    /// <summary>
    /// Base URL of the Nightscout server to compare against
    /// </summary>
    public string NightscoutUrl { get; set; } = string.Empty;

    /// <summary>
    /// API secret (SHA1 hash or plain text)
    /// </summary>
    public string? ApiSecret { get; set; }

    /// <summary>
    /// API path to test (e.g., /api/v1/entries?count=10)
    /// </summary>
    public string QueryPath { get; set; } = string.Empty;

    /// <summary>
    /// HTTP method (GET, POST, etc.) - defaults to GET
    /// </summary>
    public string? Method { get; set; }

    /// <summary>
    /// Optional request body for POST/PUT requests
    /// </summary>
    public string? RequestBody { get; set; }
}

/// <summary>
/// Result of manual API comparison test
/// </summary>
public class ManualTestResult
{
    /// <summary>
    /// The API path that was tested
    /// </summary>
    public string QueryPath { get; set; } = string.Empty;

    /// <summary>
    /// HTTP method used
    /// </summary>
    public string Method { get; set; } = "GET";

    /// <summary>
    /// When the test was performed
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Raw JSON response from Nightscout
    /// </summary>
    public string? NightscoutResponse { get; set; }

    /// <summary>
    /// Raw JSON response from Nocturne
    /// </summary>
    public string? NocturneResponse { get; set; }

    /// <summary>
    /// HTTP status code from Nightscout
    /// </summary>
    public int? NightscoutStatusCode { get; set; }

    /// <summary>
    /// HTTP status code from Nocturne
    /// </summary>
    public int? NocturneStatusCode { get; set; }

    /// <summary>
    /// Response time from Nightscout in milliseconds
    /// </summary>
    public long NightscoutResponseTimeMs { get; set; }

    /// <summary>
    /// Response time from Nocturne in milliseconds
    /// </summary>
    public long NocturneResponseTimeMs { get; set; }

    /// <summary>
    /// Error message if Nightscout request failed
    /// </summary>
    public string? NightscoutError { get; set; }

    /// <summary>
    /// Error message if Nocturne request failed
    /// </summary>
    public string? NocturneError { get; set; }
}
