using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Hubs;

/// <summary>
/// SignalR hub for Home Assistant integration. HA instances subscribe to receive
/// real-time glucose relays and alert dispatches, and can acknowledge excursions
/// when the channel is configured to allow it.
/// Mounted at /hubs/home-assistant.
/// </summary>
public class HomeAssistantHub : TenantAwareHub
{
    /// <summary>
    /// Subscribe the calling connection to glucose relay and per-instance alert groups.
    /// Also performs catch-up delivery for any failed HA deliveries targeting this instance.
    /// </summary>
    /// <param name="instanceId">The Home Assistant instance identifier (matches channel Destination).</param>
    public async Task Subscribe(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new HubException("instanceId must not be empty.");

        var tenantId = TenantContext?.TenantId
            ?? throw new HubException("No tenant context resolved.");

        // Join tenant-scoped glucose relay and per-instance groups
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup("ha-glucose"));
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup($"ha:{instanceId}"));

        // Catch-up: re-dispatch failed deliveries for this instance
        await CatchUpFailedDeliveriesAsync(tenantId, instanceId);
    }

    /// <summary>
    /// Acknowledge a specific excursion from the Home Assistant side.
    /// Requires the "alerts.readwrite" OAuth scope and the channel's metadata must have allow_ack enabled.
    /// </summary>
    /// <param name="excursionId">The excursion to acknowledge.</param>
    /// <param name="acknowledgedBy">Display name or identifier of the person acknowledging.</param>
    public async Task Acknowledge(Guid excursionId, string acknowledgedBy)
    {
        var tenantId = TenantContext?.TenantId
            ?? throw new HubException("No tenant context resolved.");

        // Gate 1: OAuth scope check — require "alerts.readwrite"
        var scopeClaim = Context.User?.FindFirst("scope")?.Value;
        if (scopeClaim is null || !scopeClaim.Split(' ').Contains("alerts.readwrite"))
            throw new HubException("Insufficient scope: alerts.readwrite is required.");

        // Gate 2: Channel config check — find HA channels for this excursion's rule and verify allow_ack
        var services = Context.GetHttpContext()!.RequestServices;
        var contextFactory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
        db.TenantId = tenantId;

        var excursion = await db.AlertExcursions
            .AsNoTracking()
            .Where(e => e.Id == excursionId && e.TenantId == tenantId)
            .Select(e => new { e.AlertRuleId })
            .FirstOrDefaultAsync(CancellationToken.None);

        if (excursion is null)
            throw new HubException("Excursion not found.");

        var haChannels = await db.AlertRuleChannels
            .AsNoTracking()
            .Where(c => c.AlertRuleId == excursion.AlertRuleId
                        && c.TenantId == tenantId
                        && c.ChannelType == ChannelType.HomeAssistant)
            .Select(c => c.Metadata)
            .ToListAsync(CancellationToken.None);

        var allowAck = haChannels.Any(metadata =>
        {
            if (string.IsNullOrEmpty(metadata))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(metadata);
                return doc.RootElement.TryGetProperty("allow_ack", out var prop)
                       && prop.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                return false;
            }
        });

        if (!allowAck)
            throw new HubException("Acknowledgement is not permitted for this alert channel.");

        // Both gates passed — acknowledge
        var ackService = services.GetRequiredService<IAlertAcknowledgementService>();
        await ackService.AcknowledgeExcursionAsync(tenantId, excursionId, acknowledgedBy, broadcast: true, CancellationToken.None);
    }

    private async Task CatchUpFailedDeliveriesAsync(Guid tenantId, string instanceId)
    {
        var services = Context.GetHttpContext()!.RequestServices;
        var contextFactory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        await using var db = await contextFactory.CreateDbContextAsync(CancellationToken.None);
        db.TenantId = tenantId;

        // Find failed HA deliveries for this instance that belong to open excursions
        var failedDeliveries = await db.AlertDeliveries
            .Include(d => d.AlertInstance)
                .ThenInclude(i => i!.AlertExcursion)
            .Where(d => d.TenantId == tenantId
                        && d.ChannelType == ChannelType.HomeAssistant
                        && d.Destination == instanceId
                        && d.Status == "failed"
                        && d.AlertInstance != null
                        && d.AlertInstance.AlertExcursion != null
                        && d.AlertInstance.AlertExcursion.EndedAt == null)
            .ToListAsync(CancellationToken.None);

        foreach (var delivery in failedDeliveries)
        {
            try
            {
                // Re-dispatch the payload to the caller
                var payload = JsonSerializer.Deserialize<AlertPayload>(delivery.Payload);
                if (payload is not null)
                {
                    await Clients.Caller.SendCoreAsync("alert_dispatch", [payload], CancellationToken.None);

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
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
