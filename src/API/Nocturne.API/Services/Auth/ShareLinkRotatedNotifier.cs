using Nocturne.API.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Tells a tenant's owner that their public share link no longer resolves and has to be
/// regenerated. Used by the rotation pass in <see cref="CredentialAtRestStartupTask"/>, which
/// retires a plaintext token. That pass keeps no recoverable copy of the replacement, so it cannot
/// hand back a URL; the owner is the only one who can produce a working link.
/// </summary>
public interface IShareLinkRotatedNotifier
{
    /// <summary>
    /// Files the notification for the tenant's owner. Best-effort: a tenant with no owner, or a
    /// notification that cannot be filed, must not stop startup — the token has already been
    /// written and the pass does not revisit it.
    /// </summary>
    Task NotifyAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ShareLinkRotatedNotifier : IShareLinkRotatedNotifier
{
    /// <summary>Notification type for a share link that no longer resolves.</summary>
    public const string NotificationType = "sharing.link_rotated";

    private const string AuditEndpoint = "service:share-link-notifier";

    private readonly IServiceProvider _services;
    private readonly ILogger<ShareLinkRotatedNotifier> _logger;

    public ShareLinkRotatedNotifier(IServiceProvider services, ILogger<ShareLinkRotatedNotifier> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _services.CreateScope();

            var ownerSubjectId = await scope.ServiceProvider
                .GetRequiredService<ITenantOwnerResolver>()
                .GetOwnerSubjectIdAsync(tenantId, cancellationToken);

            if (ownerSubjectId is null)
            {
                _logger.LogWarning(
                    "Tenant {TenantId} has no owner; its share link was rotated without a notification.",
                    tenantId);
                return;
            }

            var tenant = await LoadTenantAsync(scope.ServiceProvider, tenantId, cancellationToken);
            if (tenant is null)
                return;

            await using var tenantScope = TenantJobScope.Open(
                _services,
                new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
                AuditEndpoint);

            // Title and subtitle are i18n keys resolved by the frontend copy layer
            // (notification-labels.ts); the backend has no copy layer. No token appears here.
            await tenantScope.ServiceProvider.GetRequiredService<IInAppNotificationService>()
                .CreateNotificationAsync(
                    userId: ownerSubjectId,
                    type: NotificationType,
                    title: "share_link_rotated",
                    subtitle: "share_link_rotated_subtitle",
                    sourceId: tenantId.ToString(),
                    cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to notify the owner of tenant {TenantId} that its share link was rotated.",
                tenantId);
        }
    }

    private static async Task<TenantIdentity?> LoadTenantAsync(
        IServiceProvider scopedServices,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tenant = await scopedServices
            .GetRequiredService<ITenantRepository>()
            .GetAsync(tenantId, cancellationToken);

        return tenant is null ? null : new TenantIdentity(tenant.Id, tenant.Slug, tenant.DisplayName);
    }

    private sealed record TenantIdentity(Guid Id, string Slug, string DisplayName);
}
