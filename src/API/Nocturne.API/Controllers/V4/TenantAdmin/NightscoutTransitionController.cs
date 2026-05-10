using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Compatibility;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.TenantAdmin;

/// <summary>
/// Aggregates Nightscout transition status for the migration dashboard.
/// Reports migration progress, write-back health, and disconnect readiness.
/// </summary>
/// <seealso cref="IConnectorConfigurationService"/>
/// <seealso cref="IDiscrepancyPersistenceService"/>
[ApiController]
[Tags("TenantAdmin")]
[Route("api/v4/nightscout-transition")]
[Produces("application/json")]
public class NightscoutTransitionController : ControllerBase
{
    private readonly NightscoutConnectorConfiguration? _nightscoutConfig;
    private readonly NightscoutCircuitBreaker? _circuitBreaker;
    private readonly IConnectorConfigurationService _connectorConfigService;
    private readonly CompatibilityProxyConfiguration _proxyConfiguration;
    private readonly IDiscrepancyPersistenceService? _persistenceService;
    private readonly ILogger<NightscoutTransitionController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="NightscoutTransitionController"/>.
    /// </summary>
    /// <param name="connectorConfigService">Service for querying connector health state.</param>
    /// <param name="proxyConfiguration">Compatibility proxy configuration options.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="nightscoutConfig">Optional Nightscout connector configuration (null when connector is not registered).</param>
    /// <param name="circuitBreaker">Optional circuit breaker for Nightscout write-back health (null when not registered).</param>
    /// <param name="persistenceService">Optional service for compatibility metrics (null when proxy is disabled).</param>
    public NightscoutTransitionController(
        IConnectorConfigurationService connectorConfigService,
        IOptions<CompatibilityProxyConfiguration> proxyConfiguration,
        ILogger<NightscoutTransitionController> logger,
        NightscoutConnectorConfiguration? nightscoutConfig = null,
        NightscoutCircuitBreaker? circuitBreaker = null,
        IDiscrepancyPersistenceService? persistenceService = null
    )
    {
        _connectorConfigService = connectorConfigService;
        _proxyConfiguration = proxyConfiguration.Value;
        _logger = logger;
        _nightscoutConfig = nightscoutConfig;
        _circuitBreaker = circuitBreaker;
        _persistenceService = persistenceService;
    }

    /// <summary>
    /// Get the current Nightscout transition status including migration progress,
    /// write-back health, and disconnect readiness recommendation.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(NightscoutTransitionStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<NightscoutTransitionStatus>> GetTransitionStatus(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var compatibility = await BuildCompatibilityInfoAsync(cancellationToken);

            var status = new NightscoutTransitionStatus
            {
                Migration = await BuildMigrationStatusAsync(cancellationToken),
                WriteBack = BuildWriteBackHealth(),
                Compatibility = compatibility,
                Recommendation = await BuildRecommendationAsync(compatibility, cancellationToken)
            };

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Nightscout transition status");
            return Problem(
                detail: "Internal server error",
                statusCode: 500,
                title: "Internal Server Error"
            );
        }
    }

    /// <summary>
    /// Builds migration status information from the Nightscout connector health state.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MigrationStatusInfo"/> describing the last successful sync.</returns>
    private async Task<MigrationStatusInfo> BuildMigrationStatusAsync(CancellationToken ct)
    {
        var info = new MigrationStatusInfo();

        if (_nightscoutConfig is not { Enabled: true })
            return info;

        var healthState = await _connectorConfigService.GetHealthStateAsync("Nightscout", ct);
        if (healthState is null)
            return info;

        if (healthState.LastSuccessfulSync.HasValue)
        {
            info.LastSyncTime = new DateTimeOffset(
                DateTime.SpecifyKind(healthState.LastSuccessfulSync.Value, DateTimeKind.Utc)
            );
            info.IsComplete = true;
        }

        return info;
    }

    /// <summary>
    /// Builds write-back health information from the circuit breaker state.
    /// </summary>
    /// <returns>A <see cref="WriteBackHealthInfo"/> describing circuit breaker state and request counts.</returns>
    private WriteBackHealthInfo BuildWriteBackHealth()
    {
        var info = new WriteBackHealthInfo();

        if (_circuitBreaker is null)
            return info;

        info.CircuitBreakerOpen = _circuitBreaker.IsOpen;

        // Request counts from structured logs are not yet available;
        // placeholders until Aspire structured log queries are wired up.
        info.RequestsLast24h = 0;
        info.SuccessesLast24h = 0;
        info.FailuresLast24h = 0;

        return info;
    }

    /// <summary>
    /// Builds compatibility information from the discrepancy persistence service when the proxy is enabled.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CompatibilityInfo"/> with compatibility score and discrepancy counts, or <see langword="null"/> when the proxy is disabled.</returns>
    private async Task<CompatibilityInfo?> BuildCompatibilityInfoAsync(CancellationToken ct)
    {
        if (!_proxyConfiguration.Enabled || _persistenceService is null)
            return null;

        try
        {
            var metrics = await _persistenceService.GetCompatibilityMetricsAsync(
                cancellationToken: ct);

            return new CompatibilityInfo
            {
                ProxyEnabled = true,
                CompatibilityScore = metrics.TotalRequests > 0 ? metrics.CompatibilityScore : null,
                TotalComparisons = metrics.TotalRequests,
                Discrepancies = metrics.MajorDifferences + metrics.CriticalDifferences,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve compatibility metrics for transition status");
            return new CompatibilityInfo { ProxyEnabled = true };
        }
    }

    /// <summary>
    /// Evaluates readiness to disconnect from Nightscout and returns a recommendation.
    /// </summary>
    /// <param name="compatibility">Current compatibility info, or <see langword="null"/> if proxy is disabled.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DisconnectRecommendation"/> with status and any blocking issues.</returns>
    private async Task<DisconnectRecommendation> BuildRecommendationAsync(
        CompatibilityInfo? compatibility, CancellationToken ct)
    {
        var recommendation = new DisconnectRecommendation();

        // If the Nightscout connector is not registered or not enabled
        if (_nightscoutConfig is not { Enabled: true })
        {
            recommendation.Status = "not-ready";
            recommendation.Blockers.Add("Nightscout connector not configured");
            return recommendation;
        }

        // Check whether a historical sync has completed
        var healthState = await _connectorConfigService.GetHealthStateAsync("Nightscout", ct);
        if (healthState?.LastSuccessfulSync is null)
        {
            recommendation.Status = "not-ready";
            recommendation.Blockers.Add("Historical data migration not started");
            return recommendation;
        }

        // Check circuit breaker health
        if (_circuitBreaker is { IsOpen: true })
        {
            recommendation.Status = "not-ready";
            recommendation.Blockers.Add("Write-back connection unhealthy");
            return recommendation;
        }

        // Check if write-back is enabled
        if (!_nightscoutConfig.WriteBackEnabled)
        {
            recommendation.Status = "almost-ready";
            recommendation.Blockers.Add("Write-back not enabled");
            return recommendation;
        }

        // Check compatibility score if proxy is enabled
        if (compatibility is { ProxyEnabled: true, CompatibilityScore: < 95.0 })
        {
            recommendation.Status = recommendation.Status == "not-ready" ? "not-ready" : "almost-ready";
            recommendation.Blockers.Add(
                $"API response compatibility is {compatibility.CompatibilityScore:F1}% — investigate discrepancies");
        }

        if (recommendation.Blockers.Count == 0)
            recommendation.Status = "safe";

        return recommendation;
    }
}
