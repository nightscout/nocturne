using Microsoft.Extensions.Options;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Notifications;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Platform.Releases;

/// <summary>
/// Tells each tenant owner about releases: once when a newer release is published, with when
/// Watchtower will install it, and once when a release first runs here, with its notes.
/// </summary>
/// <remarks>
/// Each notice is claimed per owner in <see cref="IOwnerNoticeLedger"/> before it is sent, and the
/// claim is released when the send fails, so every owner gets it once. Each check offers the current
/// notices to every active tenant, so a tenant created later gets them on its first check.
/// A newer notice archives the owner's older ones as <see cref="NotificationArchiveReason.Superseded"/>.
/// </remarks>
public class ReleaseNoticeService(
    IServiceProvider serviceProvider,
    ReleaseStatusService releaseStatus,
    IOwnerNoticeLedger ledger,
    ActiveTenantSnapshot activeTenants,
    IOptions<ReleaseCheckOptions> options,
    TimeProvider timeProvider,
    ILogger<ReleaseNoticeService> logger) : BackgroundService
{
    internal const string AuditEndpoint = "service:release-notices";

    public const string Source = "release-check";

    public const string UpdateAvailableType = "system.update_available";

    public const string ReleaseInstalledType = "system.release_installed";

    public const string ReleaseNotesActionId = "release_notes";

    public const string AvailableKind = "available";

    public const string InstalledKind = "installed";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        try
        {
            await Task.Delay(StartupDelay, timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error checking for a new release");
                }

                await Task.Delay(options.Value.CheckInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        var status = await releaseStatus.GetStatusAsync(cancellationToken);
        if (!status.IsReleaseBuild)
            return;

        var notices = new List<(string Kind, string Type, string Version)>();
        if (status.Current is { } current)
            notices.Add((InstalledKind, ReleaseInstalledType, current.Version));
        if (status.Latest is { } latest)
            notices.Add((AvailableKind, UpdateAvailableType, latest.Version));
        if (notices.Count == 0)
            return;

        foreach (var tenant in await activeTenants.GetAsync(cancellationToken))
        {
            try
            {
                await NotifyOwnersAsync(tenant, notices, status.AutoUpdate, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not send release notices to tenant {TenantId}", tenant.Id);
            }
        }
    }

    private async Task NotifyOwnersAsync(
        ActiveTenant tenant,
        IReadOnlyList<(string Kind, string Type, string Version)> notices,
        AutoUpdateStatusDto autoUpdate,
        CancellationToken cancellationToken)
    {
        await using var scope = TenantJobScope.Open(
            serviceProvider,
            new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
            AuditEndpoint);

        var owners = await scope.ServiceProvider.GetRequiredService<ITenantOwnerResolver>()
            .GetOwnerSubjectIdsAsync(tenant.Id, cancellationToken);

        foreach (var owner in owners)
        {
            if (!Guid.TryParse(owner, out var subjectId))
                continue;

            foreach (var (kind, type, version) in notices)
            {
                if (!await ledger.TryClaimAsync(kind, version, tenant.Id, subjectId, cancellationToken))
                    continue;

                try
                {
                    await NotifyOwnerAsync(scope.ServiceProvider, owner, type, version, autoUpdate, cancellationToken);
                    logger.LogInformation("Sent the {Type} notice for {Version} to an owner of tenant {TenantId}",
                        type, version, tenant.Id);
                }
                catch
                {
                    await ledger.ReleaseAsync(kind, version, tenant.Id, subjectId, CancellationToken.None);
                    throw;
                }
            }
        }
    }

    private static async Task NotifyOwnerAsync(
        IServiceProvider services, string ownerId, string type, string version,
        AutoUpdateStatusDto autoUpdate, CancellationToken cancellationToken)
    {
        var notifications = services.GetRequiredService<IInAppNotificationService>();
        var active = await services.GetRequiredService<IInAppNotificationRepository>()
            .GetActiveBySourceAsync(ownerId, Source, cancellationToken);
        foreach (var older in active.Where(n => Supersedes(type, version, n.Type, n.SourceId)))
        {
            await notifications.ArchiveNotificationAsync(
                older.Id, NotificationArchiveReason.Superseded, ownerId, cancellationToken);
        }

        var metadata = new Dictionary<string, object> { ["Version"] = version };
        if (type == UpdateAvailableType)
        {
            metadata["AutoUpdateMode"] = autoUpdate.Mode.ToString();
            if (autoUpdate.NextCheckAt is { } next)
                metadata["NextCheckAt"] = next.ToString("O");
            if (autoUpdate.CheckIntervalSeconds is { } interval)
                metadata["CheckIntervalSeconds"] = interval;
        }

        await notifications.CreateNotificationAsync(
            userId: ownerId,
            type: type,
            title: type == UpdateAvailableType ? "update_available" : "release_installed",
            sourceId: version,
            metadata: metadata,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// An available notice supersedes older available notices. An installed notice supersedes older
    /// installed notices and the available notices for versions it has reached, but not one for a
    /// newer release, which still applies.
    /// </summary>
    internal static bool Supersedes(string type, string version, string olderType, string? olderVersion)
    {
        if (type == UpdateAvailableType)
            return olderType == UpdateAvailableType;

        if (olderType != UpdateAvailableType)
            return true;

        return !ReleaseVersion.TryParse(olderVersion, out var older)
            || !ReleaseVersion.TryParse(version, out var installed)
            || !(older > installed);
    }
}
