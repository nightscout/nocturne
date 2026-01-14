using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeHealthCheck(
    MyLifeConnectorService connectorService,
    ILogger<MyLifeHealthCheck> logger)
    : IHealthCheck
{
    private readonly MyLifeConnectorService _connectorService = connectorService;
    private readonly ILogger<MyLifeHealthCheck> _logger = logger;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_connectorService.IsHealthy)
            {
                var failureCount = _connectorService.FailedRequestCount;
                _logger.LogWarning(
                    "MyLife connector has {FailureCount} consecutive failures",
                    failureCount
                );

                return Task.FromResult(
                    HealthCheckResult.Unhealthy(
                        $"MyLife connector has {failureCount} consecutive failures",
                        data: new Dictionary<string, object>
                        {
                            ["FailedRequestCount"] = failureCount,
                            ["ServiceName"] = _connectorService.ServiceName
                        }
                    )
                );
            }

            return Task.FromResult(
                HealthCheckResult.Healthy(
                    "MyLife connector is healthy",
                    new Dictionary<string, object>
                    {
                        ["ServiceName"] = _connectorService.ServiceName,
                        ["FailedRequestCount"] = _connectorService.FailedRequestCount
                    }
                )
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking MyLife connector health");

            return Task.FromResult(
                HealthCheckResult.Unhealthy(
                    "Error checking MyLife connector health",
                    ex,
                    new Dictionary<string, object>
                    {
                        ["ServiceName"] = _connectorService.ServiceName,
                        ["Error"] = ex.Message
                    }
                )
            );
        }
    }
}