using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services.Compatibility;

/// <summary>
/// Background service that manages discrepancy analysis data by periodically pruning records older
/// than the configured retention period (default: 30 days). Runs every 6 hours.
/// </summary>
/// <remarks>
/// Discrepancy records are produced by the compatibility layer when comparing Nocturne responses
/// against a legacy Nightscout instance. This service prevents unbounded growth of that data.
/// </remarks>
public class DiscrepancyMaintenanceService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscrepancyMaintenanceService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6); // Run every 6 hours
    private readonly int _retentionDays = 30; // Keep data for 30 days by default

    /// <summary>
    /// Initializes a new instance of the DiscrepancyMaintenanceService class
    /// </summary>
    /// <param name="serviceProvider">Service provider for creating scoped services</param>
    /// <param name="timeProvider">Clock the retention cutoff and metrics window are measured from</param>
    /// <param name="logger">Logger instance for this service</param>
    public DiscrepancyMaintenanceService(
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        ILogger<DiscrepancyMaintenanceService> logger
    )
    {
        _serviceProvider = serviceProvider;
        _time = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Execute the background maintenance tasks
    /// </summary>
    /// <param name="stoppingToken">Cancellation token to stop the service</param>
    /// <returns>Task representing the async execution</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Discrepancy maintenance service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformMaintenanceAsync(stoppingToken);
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Discrepancy maintenance service is stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during discrepancy maintenance");
                // Wait a shorter interval before retrying on error
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
    }

    private async Task PerformMaintenanceAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDiscrepancyAnalysisRepository>();

        _logger.LogInformation("Starting discrepancy data maintenance");

        try
        {
            // Clean up old data
            var cutoffDate = _time.GetUtcNow().AddDays(-_retentionDays);
            var deletedCount = await repository.DeleteOldAnalysesAsync(
                cutoffDate,
                cancellationToken
            );

            if (deletedCount > 0)
            {
                _logger.LogInformation(
                    "Cleaned up {DeletedCount} old discrepancy analyses older than {CutoffDate}",
                    deletedCount,
                    cutoffDate
                );
            }

            // Get current metrics for alerting
            var last24Hours = _time.GetUtcNow().AddHours(-24);
            var metrics = await repository.GetCompatibilityMetricsAsync(
                last24Hours,
                null,
                cancellationToken
            );

            // Check for alert conditions
            await CheckAlertConditionsAsync(metrics, cancellationToken);

            _logger.LogDebug("Discrepancy maintenance completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during discrepancy maintenance");
            throw;
        }
    }

    private async Task CheckAlertConditionsAsync(
        CompatibilityMetrics metrics,
        CancellationToken cancellationToken
    )
    {
        // Check for critical compatibility issues
        if (metrics.TotalRequests > 10) // Only alert if we have meaningful data
        {
            // Alert if compatibility score drops below 85%
            if (metrics.CompatibilityScore < 85.0)
            {
                _logger.LogWarning(
                    "Compatibility score is low: {Score}% ({CriticalDifferences} critical, {MajorDifferences} major issues in last 24h)",
                    metrics.CompatibilityScore,
                    metrics.CriticalDifferences,
                    metrics.MajorDifferences
                );
            }

            // Alert if we have many critical differences
            if (metrics.CriticalDifferences > 10)
            {
                _logger.LogError(
                    "High number of critical compatibility issues: {CriticalDifferences} in last 24h",
                    metrics.CriticalDifferences
                );
            }

            // Alert if response times are significantly different
            if (
                metrics.AverageNightscoutResponseTime > 0
                && metrics.AverageNocturneResponseTime > 0
            )
            {
                var timeDifference = Math.Abs(
                    metrics.AverageNightscoutResponseTime - metrics.AverageNocturneResponseTime
                );
                var percentageDifference =
                    (
                        timeDifference
                        / Math.Min(
                            metrics.AverageNightscoutResponseTime,
                            metrics.AverageNocturneResponseTime
                        )
                    ) * 100;

                if (percentageDifference > 50) // Alert if response times differ by more than 50%
                {
                    _logger.LogWarning(
                        "Significant response time difference: Nightscout {NightscoutTime}ms vs Nocturne {NocturneTime}ms ({Percentage}% difference)",
                        metrics.AverageNightscoutResponseTime,
                        metrics.AverageNocturneResponseTime,
                        percentageDifference
                    );
                }
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stop the maintenance service gracefully
    /// </summary>
    /// <param name="stoppingToken">Cancellation token to stop the service</param>
    /// <returns>Task representing the async stop operation</returns>
    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Discrepancy maintenance service is stopping");
        await base.StopAsync(stoppingToken);
    }
}
