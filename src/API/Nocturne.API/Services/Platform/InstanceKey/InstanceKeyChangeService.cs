using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Notifications;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Platform.InstanceKey;

/// <summary>
/// Tells the people who can re-enter them which stored secrets stopped decrypting when the
/// instance started with a different key, and clears the notices once they have.
/// </summary>
/// <remarks>
/// While <see cref="InstanceKeyCheck"/> reports an outstanding key change, each tenant owner is told
/// once about the connectors and alert channels of their tenant that no longer decrypt, and each
/// platform admin once about the platform settings, claimed per owner in
/// <see cref="IOwnerNoticeLedger"/> under the change's generation. A notice for a newer generation
/// supersedes the owner's older ones. The scan repeats every <see cref="RescanInterval"/>, and a
/// failed scan is tried again then: a tenant with nothing left has its notices archived, and when
/// nothing is left anywhere the change is cleared and the scans stop. Nothing is re-encrypted here;
/// the old key is gone, so only a person re-entering a secret can repair it.
/// </remarks>
public class InstanceKeyChangeService(
    IServiceProvider serviceProvider,
    InstanceKeyCheck keyCheck,
    UndecryptableSecretScanner scanner,
    IOwnerNoticeLedger ledger,
    ActiveTenantSnapshot activeTenants,
    INocturneDbContextFactory contexts,
    TimeProvider timeProvider,
    ILogger<InstanceKeyChangeService> logger) : BackgroundService
{
    internal const string AuditEndpoint = "service:instance-key-change";

    public const string Source = "instance-key";

    public const string TenantNoticeType = "system.instance_key_changed";

    public const string PlatformNoticeType = "system.instance_key_changed_platform";

    public const string TenantKind = "instance_key";

    public const string PlatformKind = "instance_key_platform";

    public const string OpenConnectorsActionId = "open_connectors";

    public const string OpenAlertsActionId = "open_alerts";

    public const string OpenPlatformSettingsActionId = "open_platform_settings";

    internal static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!await PassAsync(stoppingToken))
                await Task.Delay(RescanInterval, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One scan. True when there is nothing more to do.</summary>
    internal async Task<bool> PassAsync(CancellationToken cancellationToken)
    {
        try
        {
            var finding = await keyCheck.FindChangeAsync(cancellationToken);
            if (finding.State == InstanceKeyState.Superseded)
            {
                logger.LogError(
                    "The instance runs on two instance keys: another replica replaced this one's key. Give every replica the same INSTANCE_KEY");
                return false;
            }

            if (finding.Change is not { } generation)
                return true;

            logger.LogWarning(
                "Some stored secrets were encrypted with an earlier instance key; telling owners what to re-enter");

            if (!await ReconcileAsync(generation, cancellationToken))
                return false;

            await keyCheck.ClearAsync(generation, cancellationToken);
            logger.LogInformation("Every stored secret decrypts under the instance key");
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Error checking the stored secrets against the instance key");
            return false;
        }
    }

    /// <summary>
    /// Sends the notices not yet sent for <paramref name="generation"/> and archives those with
    /// nothing left to re-enter. True when no tenant and no platform setting has a secret left that
    /// does not decrypt.
    /// </summary>
    internal async Task<bool> ReconcileAsync(Guid generation, CancellationToken cancellationToken)
    {
        var clear = true;
        foreach (var tenant in await activeTenants.GetAsync(cancellationToken))
        {
            try
            {
                clear &= await ReconcileTenantAsync(tenant, generation, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                clear = false;
                logger.LogError(ex, "Could not check the secrets of tenant {TenantId} against the instance key", tenant.Id);
            }
        }

        try
        {
            clear &= await ReconcilePlatformAsync(generation, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clear = false;
            logger.LogError(ex, "Could not check the platform settings against the instance key");
        }

        return clear;
    }

    private async Task<bool> ReconcileTenantAsync(ActiveTenant tenant, Guid generation, CancellationToken cancellationToken)
    {
        await using var scope = TenantJobScope.Open(
            serviceProvider,
            new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
            AuditEndpoint);

        var found = await scope.ServiceProvider.GetRequiredService<UndecryptableSecretScanner>()
            .ScanTenantAsync(cancellationToken);
        var owners = await scope.ServiceProvider.GetRequiredService<ITenantOwnerResolver>()
            .GetOwnerSubjectIdsAsync(tenant.Id, cancellationToken);

        var actions = new List<NotificationActionDto>();
        if (found.Connectors.Count > 0)
            actions.Add(Action(OpenConnectorsActionId));
        if (found.AlertChannels.Count > 0)
            actions.Add(Action(OpenAlertsActionId));

        var metadata = new Dictionary<string, object>
        {
            ["Connectors"] = found.Connectors.Select(c => c.DisplayName).ToList(),
            ["AlertChannels"] = found.AlertChannels,
        };

        foreach (var owner in owners)
        {
            await NotifyAsync(
                scope.ServiceProvider, tenant.Id, owner, TenantKind, TenantNoticeType, generation, found.IsEmpty,
                found.AlertChannels.Count > 0 ? NotificationUrgency.Urgent : NotificationUrgency.Warn,
                actions, metadata, cancellationToken);
        }

        return found.IsEmpty;
    }

    private async Task<bool> ReconcilePlatformAsync(Guid generation, CancellationToken cancellationToken)
    {
        var found = await scanner.ScanPlatformAsync(cancellationToken);
        var metadata = new Dictionary<string, object>
        {
            ["PlatformSettings"] = found.Select(s => s.Category).ToList(),
        };
        // Bot and email credentials deliver alerts for every tenant.
        var urgency = found.Any(s => s.Enabled) ? NotificationUrgency.Urgent : NotificationUrgency.Warn;

        foreach (var admin in await PlatformAdminsAsync(cancellationToken))
        {
            await using var scope = TenantJobScope.Open(
                serviceProvider,
                new TenantContext(admin.TenantId, admin.Slug, admin.DisplayName, true, IsDemo: false),
                AuditEndpoint);

            await NotifyAsync(
                scope.ServiceProvider, admin.TenantId, admin.SubjectId.ToString(), PlatformKind, PlatformNoticeType,
                generation, found.Count == 0, urgency, null, metadata, cancellationToken);
        }

        return found.Count == 0;
    }

    private async Task NotifyAsync(
        IServiceProvider services, Guid tenantId, string ownerId, string kind, string type, Guid generation,
        bool clear, NotificationUrgency urgency, List<NotificationActionDto>? actions,
        Dictionary<string, object> metadata, CancellationToken cancellationToken)
    {
        var notifications = services.GetRequiredService<IInAppNotificationService>();
        var sourceId = generation.ToString();
        var active = (await services.GetRequiredService<IInAppNotificationRepository>()
                .GetActiveBySourceAsync(ownerId, Source, cancellationToken))
            .Where(n => n.Type == type)
            .ToList();

        if (clear)
        {
            foreach (var notice in active)
                await notifications.ArchiveNotificationAsync(
                    notice.Id, NotificationArchiveReason.Completed, ownerId, cancellationToken);
            return;
        }

        if (!Guid.TryParse(ownerId, out var subjectId)
            || !await ledger.TryClaimAsync(kind, sourceId, tenantId, subjectId, cancellationToken))
            return;

        try
        {
            foreach (var older in active.Where(n => n.SourceId != sourceId))
                await notifications.ArchiveNotificationAsync(
                    older.Id, NotificationArchiveReason.Superseded, ownerId, cancellationToken);

            await notifications.CreateNotificationAsync(
                userId: ownerId,
                type: type,
                title: type == TenantNoticeType ? "instance_key_changed" : "instance_key_changed_platform",
                urgency: urgency,
                sourceId: sourceId,
                actions: actions,
                metadata: metadata,
                cancellationToken: cancellationToken);
            logger.LogInformation("Sent the {Type} notice to an owner of tenant {TenantId}", type, tenantId);
        }
        catch
        {
            await ledger.ReleaseAsync(kind, sourceId, tenantId, subjectId, CancellationToken.None);
            throw;
        }
    }

    internal sealed record PlatformAdmin(Guid SubjectId, Guid TenantId, string Slug, string DisplayName);

    /// <summary>
    /// Each platform admin, in the tenant they joined first: an in-app notice belongs to a tenant.
    /// An admin who is a member of no active tenant has nowhere to see it and is logged instead.
    /// </summary>
    internal async Task<IReadOnlyList<PlatformAdmin>> PlatformAdminsAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreatePlatformAsync(cancellationToken);
        var memberships = await db.TenantMembers.AsNoTracking()
            .Where(m => m.Subject!.IsPlatformAdmin && m.Subject.IsActive && !m.Subject.IsSystemSubject
                && m.Tenant!.IsActive)
            .OrderBy(m => m.SysCreatedAt).ThenBy(m => m.Id)
            .Select(m => new PlatformAdmin(m.SubjectId, m.TenantId, m.Tenant!.Slug, m.Tenant.DisplayName))
            .ToListAsync(cancellationToken);
        var admins = memberships.GroupBy(m => m.SubjectId).Select(g => g.First()).ToList();

        var withoutTenant = await db.Subjects.AsNoTracking()
            .CountAsync(s => s.IsPlatformAdmin && s.IsActive && !s.IsSystemSubject, cancellationToken) - admins.Count;
        if (withoutTenant > 0)
            logger.LogWarning(
                "{Count} platform admins are members of no active tenant and cannot be told which platform settings to re-enter",
                withoutTenant);

        return admins;
    }

    private static NotificationActionDto Action(string actionId) => new()
    {
        ActionId = actionId,
        Label = actionId,
        Icon = "key-round",
        Variant = "default",
    };
}
