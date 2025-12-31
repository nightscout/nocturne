using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Health
{
    public class ConnectorHealthCheck : IHealthCheck
    {
        private readonly IConnectorMetricsTracker _metricsTracker;
        private readonly IConnectorStateService _stateService;
        private readonly string _connectorSource;

        public ConnectorHealthCheck(
            IConnectorMetricsTracker metricsTracker,
            IConnectorStateService stateService,
            string connectorSource
        )
        {
            _metricsTracker =
                metricsTracker ?? throw new ArgumentNullException(nameof(metricsTracker));
            _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
            _connectorSource = connectorSource ?? "unknown";
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default
        )
        {
            var data = new Dictionary<string, object>
            {
                { "connector_source", _connectorSource },
                { "TotalEntries", _metricsTracker.TotalEntries },
                { "EntriesLast24Hours", _metricsTracker.EntriesLast24Hours },
                { "State", _stateService.CurrentState.ToString() },
                { "StateMessage", _stateService.StateMessage ?? string.Empty },
            };

            // Add per-type breakdowns
            var totalBreakdown = _metricsTracker.GetTotalItemsBreakdown();
            var last24hBreakdown = _metricsTracker.GetItemsLast24HoursBreakdown();

            // Convert enum keys to strings for JSON serialization
            data.Add(
                "TotalItemsBreakdown",
                totalBreakdown.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value)
            );
            data.Add(
                "ItemsLast24HoursBreakdown",
                last24hBreakdown.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value)
            );

            if (_metricsTracker.LastEntryTime.HasValue)
            {
                data.Add("LastEntryTime", _metricsTracker.LastEntryTime.Value.ToString("O"));

                // Calculate time since last entry for cleaner consumption if needed
                var timeSince = DateTime.UtcNow - _metricsTracker.LastEntryTime.Value;
                data.Add("seconds_since_last_entry", (long)timeSince.TotalSeconds);
            }
            else
            {
                data.Add("LastEntryTime", "N/A");
            }

            // You might want to degrade health if no data received for a long time,
            // but for now we just report Healthy with data.
            // If LastEntryTime is very old (e.g. > 1 hour), we could return Degraded?
            // For now, let's keep it simple: Healthy, just reporting metrics.

            return Task.FromResult(HealthCheckResult.Healthy("Connector is running", data));
        }
    }
}
