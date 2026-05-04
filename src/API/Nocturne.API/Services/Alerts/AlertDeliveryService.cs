using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Creates one delivery row per channel attached to a fired alert rule and dispatches each
/// to the matching channel provider (<c>web_push</c>, <c>in_app</c>, <c>webhook</c>,
/// chat-bot variants). Delivery rows are persisted before the provider call so the audit
/// trail is complete on provider failure.
/// </summary>
/// <remarks>
/// DND suppression is the orchestrator's responsibility — by the time DispatchAsync is called
/// we already know the alert should reach the user. Real-time <see cref="ISignalRBroadcastService"/>
/// notifications are sent alongside channel deliveries.
/// </remarks>
internal sealed class AlertDeliveryService(
    IDbContextFactory<NocturneDbContext> contextFactory,
    ITenantAccessor tenantAccessor,
    ISignalRBroadcastService broadcastService,
    IServiceProvider serviceProvider,
    ILogger<AlertDeliveryService> logger)
    : IAlertDeliveryService
{
    public async Task DispatchAsync(
        Guid alertInstanceId,
        IReadOnlyList<AlertRuleChannelSnapshot> channels,
        AlertPayload payload,
        CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;

        // Always emit the alert_dispatch broadcast so SignalR-connected web clients render
        // the toast even when there are zero configured channels (the user explicitly opted
        // out of every push/sound/in-app delivery surface).
        try
        {
            await broadcastService.BroadcastAlertEventAsync("alert_dispatch", payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast alert_dispatch for instance {InstanceId}", alertInstanceId);
        }

        var payloadJson = JsonSerializer.Serialize(payload);

        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // Zero-channel rules still leave an audit anchor: an InApp delivery row marked
        // delivered immediately. Without this row the History/Replay page cannot tell the
        // difference between "alert fired but every provider is broken" and "user explicitly
        // chose no channels", and the SignalR alert_dispatch broadcast (which fires
        // unconditionally above) would be the only attribution surface.
        if (channels.Count == 0)
        {
            db.AlertDeliveries.Add(new AlertDeliveryEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AlertInstanceId = alertInstanceId,
                AlertRuleChannelId = null,
                ChannelType = ChannelType.InApp,
                Destination = string.Empty,
                Payload = payloadJson,
                Status = "delivered",
                DeliveredAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        var deliveryRows = new List<AlertDeliveryEntity>(channels.Count);
        foreach (var channel in channels)
        {
            var delivery = new AlertDeliveryEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AlertInstanceId = alertInstanceId,
                AlertRuleChannelId = channel.Id,
                ChannelType = channel.ChannelType,
                Destination = channel.Destination,
                Payload = payloadJson,
                Status = "pending",
                CreatedAt = DateTime.UtcNow,
            };
            db.AlertDeliveries.Add(delivery);
            deliveryRows.Add(delivery);
        }
        await db.SaveChangesAsync(ct);

        // Hand each persisted row to its provider. Provider failures are caught and recorded
        // on the delivery row; one bad webhook does not abort the rest of the batch.
        foreach (var delivery in deliveryRows)
        {
            try
            {
                await DispatchToProviderAsync(delivery, payload, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch delivery {DeliveryId} via {ChannelType}",
                    delivery.Id, delivery.ChannelType);
                await MarkFailedAsync(delivery.Id, ex.Message, ct);
            }
        }
    }

    public async Task TestFireAsync(
        Guid alertRuleId,
        IReadOnlyList<AlertRuleChannelSnapshot> channels,
        AlertPayload payload,
        CancellationToken ct)
    {
        var tenantId = tenantAccessor.TenantId;
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // Synthesise a parent excursion so the delivery rows have somewhere to hang. The
        // excursion is opened and immediately resolved — it never participates in tracker
        // state because we don't go through IExcursionTracker (which would mutate the live
        // ActiveExcursionId on AlertTrackerStateEntity and confuse the orchestrator).
        var now = DateTime.UtcNow;
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            AlertRuleId = alertRuleId,
            StartedAt = now,
            EndedAt = now,
        };
        db.AlertExcursions.Add(excursion);

        var instance = new AlertInstanceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            AlertExcursionId = excursion.Id,
            Status = "test",
            TriggeredAt = now,
            ResolvedAt = now,
            ResolutionReason = "test",
            IsTest = true,
        };
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync(ct);

        // Broadcast so the firing toast renders for SignalR-connected web clients —
        // mirrors the real-fire UX so users learn what a real fire feels like.
        try
        {
            await broadcastService.BroadcastAlertEventAsync("alert_test_fire", payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast alert_test_fire for rule {RuleId}", alertRuleId);
        }

        if (channels.Count == 0)
        {
            // Same audit-anchor pattern as the zero-channel real-fire path.
            db.AlertDeliveries.Add(new AlertDeliveryEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AlertInstanceId = instance.Id,
                AlertRuleChannelId = null,
                ChannelType = ChannelType.InApp,
                Destination = string.Empty,
                Payload = JsonSerializer.Serialize(payload),
                Status = "delivered",
                DeliveredAt = now,
                CreatedAt = now,
                IsTest = true,
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        var payloadJson = JsonSerializer.Serialize(payload);
        var deliveryRows = new List<AlertDeliveryEntity>(channels.Count);
        foreach (var channel in channels)
        {
            var delivery = new AlertDeliveryEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                AlertInstanceId = instance.Id,
                AlertRuleChannelId = channel.Id,
                ChannelType = channel.ChannelType,
                Destination = channel.Destination,
                Payload = payloadJson,
                Status = "pending",
                CreatedAt = now,
                IsTest = true,
            };
            db.AlertDeliveries.Add(delivery);
            deliveryRows.Add(delivery);
        }
        await db.SaveChangesAsync(ct);

        foreach (var delivery in deliveryRows)
        {
            try
            {
                await DispatchToProviderAsync(delivery, payload, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Test-fire delivery {DeliveryId} via {ChannelType} failed",
                    delivery.Id, delivery.ChannelType);
                await MarkFailedAsync(delivery.Id, ex.Message, ct);
            }
        }
    }

    public async Task TestFireDryRunAsync(
        IReadOnlyList<AlertRuleChannelSnapshot> channels,
        AlertPayload payload,
        CancellationToken ct)
    {
        try
        {
            await broadcastService.BroadcastAlertEventAsync("alert_test_fire", payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast alert_test_fire (dry-run)");
        }

        // Push directly to providers without persisting delivery rows. Failures are logged
        // but not retryable — there's no row to mark failed. Dry-run callers know this is
        // an editor preview, not an audit-tracked send.
        foreach (var channel in channels)
        {
            try
            {
                var fauxDelivery = new AlertDeliveryEntity
                {
                    Id = Guid.CreateVersion7(),
                    ChannelType = channel.ChannelType,
                    Destination = channel.Destination,
                };
                await DispatchToProviderAsync(fauxDelivery, payload, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Test-fire dry-run dispatch via {ChannelType} failed", channel.ChannelType);
            }
        }
    }

    public async Task MarkDeliveredAsync(Guid deliveryId, string? platformMessageId, string? platformThreadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var delivery = await db.AlertDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null) return;

        delivery.Status = "delivered";
        delivery.DeliveredAt = DateTime.UtcNow;
        delivery.PlatformMessageId = platformMessageId;
        delivery.PlatformThreadId = platformThreadId;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid deliveryId, string error, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var delivery = await db.AlertDeliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null) return;

        delivery.Status = "failed";
        delivery.RetryCount++;
        delivery.LastError = error;
        await db.SaveChangesAsync(ct);
    }

    private async Task DispatchToProviderAsync(AlertDeliveryEntity delivery, AlertPayload payload, CancellationToken ct)
    {
        switch (delivery.ChannelType)
        {
            case ChannelType.WebPush:
                var webPushProvider = serviceProvider.GetService<Providers.WebPushProvider>();
                if (webPushProvider is not null)
                {
                    await webPushProvider.SendAsync(payload, ct);
                    await MarkDeliveredAsync(delivery.Id, null, null, ct);
                }
                break;

            case ChannelType.InApp:
                var inAppProvider = serviceProvider.GetService<Providers.InAppProvider>();
                if (inAppProvider is not null)
                {
                    await inAppProvider.SendAsync(delivery.Destination, payload, ct);
                    await MarkDeliveredAsync(delivery.Id, null, null, ct);
                }
                break;

            case ChannelType.Webhook:
                var webhookProvider = serviceProvider.GetService<Providers.WebhookProvider>();
                if (webhookProvider is not null)
                {
                    await webhookProvider.SendAsync(delivery.Destination, payload, ct);
                    await MarkDeliveredAsync(delivery.Id, null, null, ct);
                }
                break;

            case var ch when Providers.ChatBotProvider.SupportedChannelTypes.Contains(ch):
                var chatBotProvider = serviceProvider.GetService<Providers.ChatBotProvider>();
                if (chatBotProvider is not null)
                {
                    await chatBotProvider.SendAsync(delivery.Id, delivery.ChannelType, delivery.Destination, payload, ct);
                }
                break;

            default:
                logger.LogWarning("Unsupported channel type '{ChannelType}' for delivery {DeliveryId}",
                    delivery.ChannelType, delivery.Id);
                break;
        }
    }
}
