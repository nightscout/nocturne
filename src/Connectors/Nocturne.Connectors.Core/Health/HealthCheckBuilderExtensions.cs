using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Health
{
    public static class HealthCheckBuilderExtensions
    {
        public static IHealthChecksBuilder AddConnectorHealthCheck(this IHealthChecksBuilder builder, string connectorName)
        {
            // Use HealthCheckRegistration to properly inject the connector name with IServiceProvider
            return builder.Add(new HealthCheckRegistration(
                connectorName,
                sp => new ConnectorHealthCheck(
                    sp.GetRequiredService<IConnectorMetricsTracker>(),
                    sp.GetRequiredService<IConnectorStateService>(),
                    connectorName),
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "connector", "metrics" }
            ));
        }
    }
}
