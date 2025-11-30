using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Configuration for the demo service health monitor.
/// </summary>
public class DemoServiceConfiguration
{
    public const string SectionName = "DemoService";

    /// <summary>
    /// Whether demo service integration is enabled.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// URL of the demo service health endpoint.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Interval between health checks in seconds.
    /// </summary>
    public int HealthCheckIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Number of consecutive failures before triggering cleanup.
    /// </summary>
    public int FailureThreshold { get; set; } = 3;
}

/// <summary>
/// Background service that monitors the demo service health.
/// When the demo service becomes unhealthy or stops, this service
/// automatically cleans up all demo data from the database.
/// </summary>
public class DemoServiceHealthMonitor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DemoServiceHealthMonitor> _logger;
    private readonly DemoServiceConfiguration _config;
    private readonly HttpClient _httpClient;
    private int _consecutiveFailures = 0;
    private bool _wasHealthy = false;
    private bool _cleanupPerformed = false;

    public DemoServiceHealthMonitor(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<DemoServiceHealthMonitor> logger
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config =
            configuration
                .GetSection(DemoServiceConfiguration.SectionName)
                .Get<DemoServiceConfiguration>() ?? new DemoServiceConfiguration();
        _httpClient = httpClientFactory.CreateClient("DemoServiceHealth");
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Demo service integration is disabled, health monitoring will not run");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.Url))
        {
            _logger.LogWarning("Demo service URL not configured, health monitoring disabled");
            return;
        }

        var healthUrl = _config.Url.TrimEnd('/') + "/health";
        _logger.LogInformation(
            "Starting demo service health monitor, checking {Url} every {Interval}s",
            healthUrl,
            _config.HealthCheckIntervalSeconds
        );

        var interval = TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckHealthAsync(healthUrl, stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Demo service health monitor is stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in demo service health monitor");
                await Task.Delay(interval, stoppingToken);
            }
        }
    }

    private async Task CheckHealthAsync(string healthUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(healthUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _consecutiveFailures = 0;
                _cleanupPerformed = false;

                if (!_wasHealthy)
                {
                    _logger.LogInformation("Demo service is now healthy");
                    _wasHealthy = true;
                }
            }
            else
            {
                await HandleHealthCheckFailureAsync(
                    $"Health check returned status {response.StatusCode}",
                    cancellationToken
                );
            }
        }
        catch (HttpRequestException ex)
        {
            await HandleHealthCheckFailureAsync(
                $"Health check failed: {ex.Message}",
                cancellationToken
            );
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await HandleHealthCheckFailureAsync("Health check timed out", cancellationToken);
        }
    }

    private async Task HandleHealthCheckFailureAsync(
        string reason,
        CancellationToken cancellationToken
    )
    {
        _consecutiveFailures++;
        _wasHealthy = false;

        _logger.LogWarning(
            "Demo service health check failed ({Failures}/{Threshold}): {Reason}",
            _consecutiveFailures,
            _config.FailureThreshold,
            reason
        );

        if (_consecutiveFailures >= _config.FailureThreshold && !_cleanupPerformed)
        {
            _logger.LogWarning("Demo service appears to be down, cleaning up demo data...");
            await CleanupDemoDataAsync(cancellationToken);
            _cleanupPerformed = true;
        }
    }

    private async Task CleanupDemoDataAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var postgreSqlService = scope.ServiceProvider.GetRequiredService<IPostgreSqlService>();

            _logger.LogInformation("Cleaning up demo data...");

            var entriesDeleted = await postgreSqlService.DeleteDemoEntriesAsync(cancellationToken);
            var treatmentsDeleted = await postgreSqlService.DeleteDemoTreatmentsAsync(
                cancellationToken
            );

            _logger.LogInformation(
                "Demo data cleanup complete: {Entries} entries and {Treatments} treatments deleted",
                entriesDeleted,
                treatmentsDeleted
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup demo data");
        }
    }
}
