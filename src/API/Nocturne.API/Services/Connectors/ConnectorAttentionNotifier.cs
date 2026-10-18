using Microsoft.EntityFrameworkCore;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Connectors;

/// <summary>
/// Turns a connector's <see cref="SyncResult.Attention"/> into the tenant owner's in-app
/// notification, and takes it down again once the connector no longer asks. One notification
/// per connector and kind at a time: a sync every fifteen minutes must not file a fresh one each
/// cycle, and a sign-in that succeeds must clear what the failures filed.
/// </summary>
public interface IConnectorAttentionNotifier
{
    /// <summary>
    /// Reconciles the owner's notifications for <paramref name="connectorId"/> with
    /// <paramref name="attention"/>: files the one it asks for if not already up, and archives
    /// any of the other kind. Null archives both. Best-effort: a tenant with no owner, or a
    /// notification store that cannot be written, must not fail the sync that raised it.
    /// </summary>
    Task ApplyAsync(Guid tenantId, string connectorId, string connectorDisplayName, ConnectorAttention? attention, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ConnectorAttentionNotifier(IServiceProvider services, ILogger<ConnectorAttentionNotifier> logger)
    : IConnectorAttentionNotifier
{
    public const string ReconnectRequiredType = "connector.reconnect_required";
    public const string ReconnectSoonType = "connector.reconnect_soon";

    /// <summary>The action the notification offers; the web app answers it by opening the connector's settings.</summary>
    public const string OpenActionId = "open";

    /// <inheritdoc />
    public async Task ApplyAsync(Guid tenantId, string connectorId, string connectorDisplayName, ConnectorAttention? attention, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;

            var ownerSubjectId = await sp.GetRequiredService<ITenantOwnerResolver>().GetOwnerSubjectIdAsync(tenantId, cancellationToken);
            if (ownerSubjectId is null)
            {
                if (attention is not null)
                    logger.LogWarning("Tenant {TenantId} has no owner to tell that {Connector} needs a new sign-in", tenantId, connectorId);
                return;
            }

            var tenant = await LoadTenantAsync(sp, tenantId, cancellationToken);
            if (tenant is null) return;

            // in_app_notifications is tenant-scoped under FORCE ROW LEVEL SECURITY; see
            // ShareLinkRotatedNotifier for why both the accessor and the pooled context are pinned.
            sp.GetRequiredService<ITenantAccessor>()
                .SetTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false));
            sp.GetRequiredService<NocturneDbContext>().TenantId = tenantId;

            var notifications = sp.GetRequiredService<IInAppNotificationService>();
            var active = await notifications.GetActiveNotificationsAsync(ownerSubjectId, cancellationToken);

            var wanted = attention?.Kind switch
            {
                ConnectorAttentionKind.ReconnectRequired => ReconnectRequiredType,
                ConnectorAttentionKind.ReconnectSoon => ReconnectSoonType,
                _ => null,
            };

            foreach (var type in new[] { ReconnectRequiredType, ReconnectSoonType })
            {
                var standing = active.Any(n => n.Type == type && string.Equals(n.SourceId, connectorId, StringComparison.OrdinalIgnoreCase));

                if (type == wanted && !standing)
                    await FileAsync(notifications, ownerSubjectId, type, connectorId, connectorDisplayName, attention!, cancellationToken);
                else if (type != wanted && standing)
                    await notifications.ArchiveBySourceAsync(ownerSubjectId, type, connectorId, NotificationArchiveReason.Completed, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not reconcile the reconnect notification for {Connector} on tenant {TenantId}", connectorId, tenantId);
        }
    }

    private static Task FileAsync(
        IInAppNotificationService notifications, string ownerSubjectId, string type,
        string connectorId, string connectorDisplayName, ConnectorAttention attention, CancellationToken ct)
    {
        // Title and action label are i18n keys the web app resolves (notification-labels.ts); the
        // subtitle names the connector and, when known, the day the sign-in stops working.
        var subtitle = attention.Deadline is { } deadline
            ? $"{connectorDisplayName} · {deadline:yyyy-MM-dd}"
            : connectorDisplayName;

        return notifications.CreateNotificationAsync(
            userId: ownerSubjectId,
            type: type,
            title: type == ReconnectRequiredType ? "connector_reconnect_required" : "connector_reconnect_soon",
            subtitle: subtitle,
            sourceId: connectorId,
            actions: [new NotificationActionDto { ActionId = OpenActionId, Label = "connector_open_settings", Icon = "settings-2" }],
            resolutionConditions: attention.Deadline is { } d && type == ReconnectSoonType
                ? new ResolutionConditions { ExpiresAt = d }
                : null,
            metadata: new Dictionary<string, object>
            {
                ["connectorId"] = connectorId,
                ["connectorName"] = connectorDisplayName,
                ["deadline"] = attention.Deadline?.ToString("O") ?? string.Empty,
            },
            cancellationToken: ct);
    }

    private static async Task<TenantIdentity?> LoadTenantAsync(IServiceProvider sp, Guid tenantId, CancellationToken ct)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync(ct);
        return await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantIdentity(t.Id, t.Slug, t.DisplayName))
            .FirstOrDefaultAsync(ct);
    }

    private sealed record TenantIdentity(Guid Id, string Slug, string DisplayName);
}
