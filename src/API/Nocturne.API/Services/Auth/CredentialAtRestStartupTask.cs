using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Security;
using Npgsql;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Runs the one-pass conversions of credential columns to their at-rest storage format, and
/// notifies the owner of every tenant whose share link was retired by that pass.
/// </summary>
/// <remarks>
/// Invoked inline during startup, before the server accepts requests, rather than as an
/// <see cref="IHostedService"/>: hosted services start after Kestrel, so a login or a share-link
/// request arriving mid-pass would meet a column in the old format and fail.
/// </remarks>
public static class CredentialAtRestStartupTask
{
    /// <summary>Notification type for a share link retired by the rotation pass.</summary>
    public const string ShareLinkResetNotificationType = "sharing.link_reset";

    /// <summary>
    /// Encrypts pre-existing TOTP secrets, rotates pre-existing plaintext share tokens, and
    /// notifies the affected tenants' owners.
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();

        // The same factory the EF model uses, so payloads written here are readable through it.
        var protector = TotpSecretProtection.CreateProtector(services);
        await CredentialAtRestInitializationExtensions.ProtectTotpSecretsAsync(
            dataSource, protector, logger, cancellationToken);

        var tokenGenerator = services.GetRequiredService<IShareTokenGenerator>();
        var rotatedTenantIds = await CredentialAtRestInitializationExtensions.RotatePlaintextShareTokensAsync(
            dataSource, tokenGenerator.Generate, logger, cancellationToken);

        foreach (var tenantId in rotatedTenantIds)
        {
            await NotifyShareLinkResetAsync(services, tenantId, logger, cancellationToken);
        }
    }

    /// <summary>
    /// Files an in-app notification for the tenant's owner. Best-effort: a tenant with no owner, or
    /// a notification that cannot be filed, must not stop startup — the token has already been
    /// rotated and the pass will not revisit it.
    /// </summary>
    private static async Task NotifyShareLinkResetAsync(
        IServiceProvider services,
        Guid tenantId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();

            var ownerSubjectId = await scope.ServiceProvider
                .GetRequiredService<ITenantOwnerResolver>()
                .GetOwnerSubjectIdAsync(tenantId, cancellationToken);

            if (ownerSubjectId is null)
            {
                logger.LogWarning(
                    "Tenant {TenantId} has no owner; its share link was reset without a notification.",
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
            // (notification-labels.ts); the backend has no copy layer. Neither the retired token
            // nor the new one appears here.
            await scope.ServiceProvider.GetRequiredService<IInAppNotificationService>()
                .CreateNotificationAsync(
                    userId: ownerSubjectId,
                    type: ShareLinkResetNotificationType,
                    title: "share_link_reset",
                    subtitle: "share_link_reset_subtitle",
                    sourceId: tenantId.ToString(),
                    cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to notify the owner of tenant {TenantId} that its share link was reset.",
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
