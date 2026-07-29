using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Tells a tenant's owner that their public share link no longer resolves and has to be
/// regenerated. Used by the rotation pass in <see cref="CredentialAtRestStartupTask"/>, which
/// retires a plaintext token. Only the digest of the replacement is stored, so the pass cannot hand
/// back a URL — the owner is the only one who can produce a working link.
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

            scope.ServiceProvider.GetRequiredService<ITenantAccessor>()
                .SetTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true));

            // in_app_notifications is tenant-scoped and FORCE ROW LEVEL SECURITY is on, so the
            // insert is only visible to the policy once the RLS tenant is pinned on the scoped
            // context the repository writes through. Without this the write is rejected.
            scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = tenantId;

            // Title and subtitle are i18n keys resolved by the frontend copy layer
            // (notification-labels.ts); the backend has no copy layer. No token appears here.
            await scope.ServiceProvider.GetRequiredService<IInAppNotificationService>()
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
        await using var db = await scopedServices
            .GetRequiredService<IDbContextFactory<NocturneDbContext>>()
            .CreateDbContextAsync(cancellationToken);

        return await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantIdentity(t.Id, t.Slug, t.DisplayName))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record TenantIdentity(Guid Id, string Slug, string DisplayName);
}
