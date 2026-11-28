using Nocturne.API.Services.BackgroundServices;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Tells each tenant's owner, once, that <see cref="IRawMmolProfileDetector"/> lists schedules of
/// theirs, so they open the profile page that shows them. Run once the server has started, after the
/// migration that leaves them.
/// </summary>
/// <remarks>
/// The detector runs on every start, so a notification of <see cref="InAppNotificationType.ProfileRawMmolValues"/> the owner was
/// ever sent for the tenant, read or archived, stops another. That holds only while archived rows
/// stay: <see cref="NotificationCleanupJob"/> purges them after 30 days and has no type filter, so
/// an owner who archived this notification is told again on the first start after that.
/// </remarks>
public sealed class RawMmolProfileNotifier(
    IServiceProvider services,
    ActiveTenantSnapshot activeTenants,
    ILogger<RawMmolProfileNotifier> logger)
{
    private const string AuditEndpoint = "service:raw-mmol-profile-notifier";

    /// <summary>Notifies the owner of every active tenant the detector finds schedules for. Never throws.</summary>
    public async Task NotifyAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ActiveTenant> tenants;
        try
        {
            tenants = await activeTenants.GetAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not list the tenants to check for profile values still in mmol/L");
            return;
        }

        try
        {
            foreach (var tenant in tenants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await NotifyAsync(tenant, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Stopped checking tenants for profile values still in mmol/L: the host is stopping");
        }
    }

    private async Task NotifyAsync(ActiveTenant tenant, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = TenantJobScope.Open(
                services,
                new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
                AuditEndpoint);
            var provider = scope.ServiceProvider;

            var found = await provider.GetRequiredService<IRawMmolProfileDetector>().DetectAsync(cancellationToken);
            if (found.Count == 0)
                return;

            var owner = await provider.GetRequiredService<ITenantOwnerResolver>()
                .GetOwnerSubjectIdAsync(tenant.Id, cancellationToken);
            if (owner is null)
            {
                logger.LogWarning(
                    "Tenant {TenantId} has {Count} profile schedules still in mmol/L and no owner to tell",
                    tenant.Id, found.Count);
                return;
            }

            var sourceId = tenant.Id.ToString();
            if (await provider.GetRequiredService<IInAppNotificationRepository>()
                    .AnyBySourceAsync(owner, InAppNotificationType.ProfileRawMmolValues.ToWireString(), sourceId, cancellationToken))
                return;

            // Title, subtitle and action label are i18n keys resolved by notification-labels.ts.
            await provider.GetRequiredService<IInAppNotificationService>().CreateNotificationAsync(
                userId: owner,
                type: InAppNotificationType.ProfileRawMmolValues.ToWireString(),
                title: NotificationLabel.RawMmolProfileValues.ToWireString(),
                subtitle: NotificationLabel.RawMmolProfileValuesSubtitle.ToWireString(),
                sourceId: sourceId,
                actions: [new NotificationActionDto { ActionId = "review", Label = NotificationLabel.Review.ToWireString(), Icon = "eye", Variant = "default" }],
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                ex, "Could not tell the owner of tenant {TenantId} about profile values still in mmol/L", tenant.Id);
        }
    }
}
