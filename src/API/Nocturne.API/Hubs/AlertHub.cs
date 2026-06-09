using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nocturne.Core.Contracts.Alerts;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for the new alert engine. Clients subscribe to receive alert events
/// (dispatch, resolved, acknowledged) and can acknowledge all active excursions.
/// Mounted at /hubs/alerts — the legacy AlarmHub remains at /hubs/alarms for compat.
/// </summary>
// Connections authenticate in-band after negotiate and are reachable only via the internal
// realtime bridge, so the HTTP fallback authorization policy must not gate the handshake.
[AllowAnonymous]
public class AlertHub : TenantAwareHub
{
    /// <summary>
    /// Subscribe the calling connection to alert events for the current tenant.
    /// </summary>
    public async Task Subscribe()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup("alert-subscribers"));
    }

    /// <summary>
    /// Acknowledge ALL active excursions for the current tenant.
    /// This halts escalation but does not close excursions.
    /// </summary>
    /// <param name="acknowledgedBy">Display name or identifier of the person acknowledging.</param>
    public async Task Acknowledge(string acknowledgedBy)
    {
        var ackService = Context.GetHttpContext()!.RequestServices
            .GetRequiredService<IAlertAcknowledgementService>();

        var tenantId = TenantContext?.TenantId
            ?? throw new Microsoft.AspNetCore.SignalR.HubException("No tenant context resolved.");

        await ackService.AcknowledgeAllAsync(tenantId, acknowledgedBy, CancellationToken.None);
    }
}
