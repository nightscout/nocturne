using System.Collections.Concurrent;
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
    /// Tracks active HA instance connection counts. Key is the tenant-scoped group name for
    /// "ha:{instanceId}", value is the number of active connections in that group.
    /// Used by <see cref="Services.Alerts.Providers.HomeAssistantProvider"/> to gate delivery marking.
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> _instanceConnectionCounts = new();

    /// <summary>
    /// Subscribe the calling connection to glucose relay and per-instance alert groups.
    /// Also performs catch-up delivery for any failed HA deliveries targeting this instance.
    /// </summary>
    /// <param name="instanceId">The Home Assistant instance identifier (matches channel Destination).</param>
    public async Task Subscribe(string instanceId)
    {
        var ct = Context.ConnectionAborted;

        if (string.IsNullOrWhiteSpace(instanceId))
            throw new HubException("instanceId must not be empty.");

        var tenantId = TenantContext?.TenantId
            ?? throw new HubException("No tenant context resolved.");

        // Join tenant-scoped glucose relay, per-instance, and alert groups
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup("ha-glucose"), ct);
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup($"ha:{instanceId}"), ct);
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup("ha-alerts"), ct);

        // Track connection count for this instance and store instanceId for cleanup on disconnect
        var instanceGroupKey = FormatTenantGroup(tenantId.ToString(), $"ha:{instanceId}");
        _instanceConnectionCounts.AddOrUpdate(instanceGroupKey, 1, (_, count) => count + 1);
        Context.Items["ha_instance_id"] = instanceId;

        // Catch-up: re-dispatch failed deliveries for this instance
        await CatchUpFailedDeliveriesAsync(tenantId, instanceId, ct);
    }

    /// <summary>
    /// Decrements the connection count for the instance this connection was subscribed to.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var instanceId = Context.Items.TryGetValue("ha_instance_id", out var val) ? val as string : null;
        if (instanceId is not null)
        {
            var tenantId = TenantContext?.TenantId.ToString();
            if (tenantId is not null)
            {
                var groupKey = FormatTenantGroup(tenantId, $"ha:{instanceId}");
                _instanceConnectionCounts.AddOrUpdate(groupKey, 0, (_, count) => Math.Max(0, count - 1));
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Returns whether a specific HA instance currently has at least one active SignalR connection.
    /// Used by <see cref="Services.Alerts.Providers.HomeAssistantProvider"/> to decide whether to mark delivery as delivered.
    /// </summary>
    public static bool IsInstanceConnected(string tenantId, string instanceId)
    {
        var key = FormatTenantGroup(tenantId, $"ha:{instanceId}");
        return _instanceConnectionCounts.TryGetValue(key, out var count) && count > 0;
    }

    /// <summary>
    /// Acknowledge a specific excursion from the Home Assistant side.
    /// Requires the "alerts.readwrite" OAuth scope and the channel's metadata must have allow_ack enabled.
    /// </summary>
    /// <param name="excursionId">The excursion to acknowledge.</param>
    /// <param name="acknowledgedBy">Display name or identifier of the person acknowledging.</param>
    public async Task Acknowledge(Guid excursionId, string acknowledgedBy)
    {
        var ct = Context.ConnectionAborted;

        var tenantId = TenantContext?.TenantId
            ?? throw new HubException("No tenant context resolved.");

        // Gate 1: OAuth scope check — require "alerts.readwrite"
        var scopes = Context.User?.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet() ?? new HashSet<string>();

        if (!scopes.Contains("alerts.readwrite"))
            throw new HubException("Insufficient permissions: alerts.readwrite scope required.");

        // Gate 2: Channel config check — find HA channels for this excursion's rule and verify allow_ack
        var services = Context.GetHttpContext()!.RequestServices;
        var contextFactory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        var excursion = await db.AlertExcursions
            .AsNoTracking()
            .Where(e => e.Id == excursionId && e.TenantId == tenantId)
            .Select(e => new { e.AlertRuleId })
            .FirstOrDefaultAsync(ct);

        if (excursion is null)
            throw new HubException("Excursion not found.");

        var haChannels = await db.AlertRuleChannels
            .AsNoTracking()
            .Where(c => c.AlertRuleId == excursion.AlertRuleId
                        && c.TenantId == tenantId
                        && c.ChannelType == ChannelType.HomeAssistant)
            .Select(c => c.Metadata)
            .ToListAsync(ct);

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
        await ackService.AcknowledgeExcursionAsync(tenantId, excursionId, acknowledgedBy, broadcast: true, ct);
    }

    private async Task CatchUpFailedDeliveriesAsync(Guid tenantId, string instanceId, CancellationToken ct)
    {
        var services = Context.GetHttpContext()!.RequestServices;
        var contextFactory = services.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.TenantId = tenantId;

        // Find failed HA deliveries for this instance that belong to open excursions
        var failedDeliveries = await db.AlertDeliveries
            .Include(d => d.AlertInstance)
                .ThenInclude(i => i!.AlertExcursion)
            .Include(d => d.AlertRuleChannel)
            .Where(d => d.TenantId == tenantId
                        && d.ChannelType == ChannelType.HomeAssistant
                        && d.Destination == instanceId
                        && d.Status == "failed"
                        && d.AlertInstance != null
                        && d.AlertInstance.AlertExcursion != null
                        && d.AlertInstance.AlertExcursion.EndedAt == null)
            .ToListAsync(ct);

        foreach (var delivery in failedDeliveries)
        {
            try
            {
                // Re-dispatch the payload to the caller, including allow_ack from channel metadata
                var payload = JsonSerializer.Deserialize<AlertPayload>(delivery.Payload);
                if (payload is not null)
                {
                    var allowAck = false;
                    if (!string.IsNullOrEmpty(delivery.AlertRuleChannel?.Metadata))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(delivery.AlertRuleChannel.Metadata);
                            allowAck = doc.RootElement.TryGetProperty("allow_ack", out var prop)
                                       && prop.ValueKind == JsonValueKind.True;
                        }
                        catch (JsonException) { }
                    }

                    var channelMeta = new { allowAck };
                    await Clients.Caller.SendCoreAsync("alert_dispatch", new object[] { payload, channelMeta }, ct);

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
