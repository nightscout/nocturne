using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for the new alert engine. Clients subscribe to receive alert events
/// (dispatch, resolved, acknowledged) and can acknowledge all active excursions.
/// Mounted at /hubs/alerts — the legacy AlarmHub remains at /hubs/alarms for compat.
/// </summary>
// The handshake is anonymous so it does not gate on the HTTP fallback authorization policy; this hub
// has no in-band handshake method, so callers must present a credential on the HTTP upgrade request.
// The hub endpoint is internet-reachable (the cloud gateway publishes /hubs/**), so authorization
// happens per method in HubAuthorizationFilter.
[AllowAnonymous]
public class AlertHub : TenantAwareHub
{
    /// <summary>
    /// Subscribe the calling connection to alert events for the current tenant.
    /// </summary>
    // alert-subscribers carries every rule's dispatches, resolutions and acknowledgements for the
    // whole tenant, including the alert text and who acknowledged it, so it is not one data category
    // and a share-style credential is refused it.
    [HubScope(Scope.AlertsRead)]
    [HubTenantGroup]
    public async Task Subscribe()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup("alert-subscribers"));
    }

    /// <summary>
    /// Acknowledge ALL active excursions for the current tenant.
    /// This halts escalation but does not close excursions.
    /// </summary>
    /// <param name="acknowledgedBy">Display name or identifier of the person acknowledging.</param>
    [HubScope(Scope.AlertsReadWrite)]
    public async Task Acknowledge(string acknowledgedBy)
    {
        var ackService = Context.GetHttpContext()!.RequestServices
            .GetRequiredService<IAlertAcknowledgementService>();

        var tenantId = TenantContext?.TenantId
            ?? throw new Microsoft.AspNetCore.SignalR.HubException("No tenant context resolved.");

        await ackService.AcknowledgeAllAsync(tenantId, acknowledgedBy, CancellationToken.None);
    }
}
