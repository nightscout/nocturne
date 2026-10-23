using Nocturne.Core.Models.Authorization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for Home Assistant integration. HA instances subscribe to receive
/// real-time glucose relays and alert dispatches, and can acknowledge excursions.
/// Mounted at /hubs/home-assistant.
/// </summary>
// The handshake is anonymous so it does not gate on the HTTP fallback authorization policy; this hub
// has no in-band handshake method, so callers must present a credential on the HTTP upgrade request.
// The hub endpoint is internet-reachable (the cloud gateway publishes /hubs/**), so authorization
// happens per method in HubAuthorizationFilter.
[AllowAnonymous]
public class HomeAssistantHub(HomeAssistantConnections connections) : TenantAwareHub
{
    private const string InstanceItemKey = "ha_instance_id";

    /// <summary>
    /// Subscribe the calling connection to its instance's alert dispatches, the tenant's resolved
    /// and acknowledged alert events and, when it may read glucose, the glucose relay. Replays the
    /// instance's failed deliveries to it.
    /// </summary>
    /// <param name="instanceId">
    /// The Home Assistant instance, which is the OAuth client the connection's token was issued to
    /// and the channel destination its alerts are addressed to. Any other value is refused, so a
    /// connection receives only its own instance's dispatches.
    /// </param>
    // The instance group and the catch-up carry alert payloads, and ha-alerts carries alert
    // resolutions and acknowledgements, so a share-style credential is refused.
    [HubScope(Scope.AlertsRead)]
    [HubTenantGroup]
    public async Task Subscribe(string instanceId)
    {
        var ct = Context.ConnectionAborted;

        var tenantId = TenantContext?.TenantId
            ?? throw new HubException("No tenant context resolved.");

        var authorization = HubAuthorizationState.Resolve(Context)
            ?? throw new HubException("Subscribe requires an authorized connection.");

        if (string.IsNullOrWhiteSpace(instanceId)
            || !string.Equals(instanceId, authorization.ClientId, StringComparison.Ordinal))
        {
            throw new HubException(
                "instanceId must be the OAuth client the connection's access token was issued to.");
        }

        if (authorization.MayReceiveCategory(RealtimeCategories.Entries))
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup("ha-glucose"), ct);
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup($"ha:{instanceId}"), ct);
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup("ha-alerts"), ct);

        connections.Add(tenantId, instanceId, Context.ConnectionId);
        Context.Items[InstanceItemKey] = instanceId;

        await CatchUpFailedDeliveriesAsync(tenantId, instanceId, ct);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(InstanceItemKey, out var value)
            && value is string instanceId
            && TenantContext?.TenantId is { } tenantId)
        {
            connections.Remove(tenantId, instanceId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Acknowledge a specific excursion from the Home Assistant side, with the connection's own
    /// resolved credential, exactly as any other client acknowledges.
    /// </summary>
    /// <param name="excursionId">The excursion to acknowledge.</param>
    /// <param name="acknowledgedBy">
    /// Recorded only when the connection's principal names no one; see
    /// <see cref="AcknowledgerExtensions.ResolveAcknowledger"/>.
    /// </param>
    /// <returns>Which outcome applied.</returns>
    // The authority is the HubAuthorization the filter admitted this call on, so the credential that
    // passed [HubScope] is the one the decision judges. Its scopes are the resolved,
    // membership-intersected set, not a "scope" claim on the principal: that claim is minted only by
    // JwtService and so only ever appeared on the principal the framework's JwtBearer scheme built,
    // and the custom chain owns the final principal on every path.
    [HubScope(Scope.AlertsReadWrite, Scope.DeviceNotify)]
    public async Task<AlertAcknowledgementOutcome> Acknowledge(Guid excursionId, string acknowledgedBy)
    {
        var tenantId = TenantContext?.TenantId
            ?? throw new HubException("No tenant context resolved.");

        var authorization = HubAuthorizationState.Resolve(Context)
            ?? throw new HubException("Acknowledge requires an authorized connection.");

        var ackService = Context.GetHttpContext()!.RequestServices
            .GetRequiredService<IAlertAcknowledgementService>();
        return await ackService.AcknowledgeExcursionAsync(
            tenantId,
            excursionId,
            (Context.User ?? new ClaimsPrincipal()).ResolveAcknowledger(acknowledgedBy),
            new AlertAcknowledgementAuthority(authorization.OwnSubjectId, authorization.Scopes),
            broadcast: true,
            Context.ConnectionAborted);
    }

    private async Task CatchUpFailedDeliveriesAsync(Guid tenantId, string instanceId, CancellationToken ct)
    {
        var services = Context.GetHttpContext()!.RequestServices;
        var contextFactory = services.GetRequiredService<INocturneDbContextFactory>();

        await using var db = await contextFactory.CreateForTenantAsync(tenantId, ct);
        var now = DateTime.UtcNow;

        // Find failed HA deliveries for this instance that belong to open excursions and are
        // not under an AlertSnooze (the resume dispatch re-sends those once it lapses)
        var failedDeliveries = await db.AlertDeliveries
            .Include(d => d.AlertInstance)
                .ThenInclude(i => i!.AlertExcursion)
            .Where(d => d.TenantId == tenantId
                        && d.ChannelType == ChannelType.HomeAssistant
                        && d.Destination == instanceId
                        && d.Status == "failed"
                        && d.AlertInstance != null
                        && d.AlertInstance.AlertExcursion != null
                        && d.AlertInstance.AlertExcursion.EndedAt == null
                        && (d.AlertInstance.SnoozedUntil == null || d.AlertInstance.SnoozedUntil <= now))
            .ToListAsync(ct);

        foreach (var delivery in failedDeliveries)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<AlertPayload>(delivery.Payload);
                if (payload is not null)
                {
                    await Clients.Caller.SendCoreAsync("alert_dispatch", new object[] { payload }, ct);

                    // Mark as delivered
                    delivery.Status = "delivered";
                    delivery.DeliveredAt = DateTime.UtcNow;
                }
            }
            catch (JsonException)
            {
                // Payload is malformed — skip this delivery
            }
        }

        if (failedDeliveries.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
