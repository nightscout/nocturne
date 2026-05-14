using Microsoft.AspNetCore.SignalR;
using Nocturne.API.Hubs;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Providers;

/// <summary>
/// Delivers alert payloads to a specific Home Assistant instance via the
/// HomeAssistantHub's per-instance SignalR group.
/// </summary>
internal sealed class HomeAssistantProvider(
    IHubContext<HomeAssistantHub> hubContext,
    ILogger<HomeAssistantProvider> logger)
{
    /// <summary>
    /// Sends an alert payload to the HA instance identified by <paramref name="destination"/>
    /// (the OAuth client ID). Targets the SignalR group "ha:{destination}" within the tenant.
    /// </summary>
    public async Task SendAsync(Guid tenantId, string destination, AlertPayload payload, CancellationToken ct)
    {
        var group = TenantAwareHub.FormatTenantGroup(tenantId.ToString(), $"ha:{destination}");

        try
        {
            await hubContext.Clients.Group(group)
                .SendCoreAsync("alert_dispatch", new object[] { payload }, ct);

            logger.LogDebug(
                "HA alert dispatched to instance {Destination} for alert instance {InstanceId}",
                destination, payload.InstanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to dispatch HA alert to instance {Destination} for alert instance {InstanceId}",
                destination, payload.InstanceId);
            throw;
        }
    }
}
