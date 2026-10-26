using Microsoft.Extensions.Options;
using Nocturne.API.Services.BackgroundServices;
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
/// Each notice is claimed in <see cref="IReleaseNoticeLedger"/> before it is sent, so it reaches
/// the owners of the tenants active at that time, once. A tenant created later does not get it.
/// A newer notice archives the owner's older ones as <see cref="NotificationArchiveReason.Superseded"/>.
/// </remarks>
public class ReleaseNoticeService(
    IServiceProvider serviceProvider,
    ReleaseStatusService releaseStatus,
    IReleaseNoticeLedger ledger,
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

        if (status.Current is { } current
            && await ledger.TryClaimAsync(ReleaseNoticeLedger.Installed, current.Version, cancellationToken))
        {
            logger.LogInformation("Release {Version} is running here for the first time; notifying owners", current.Version);
            await NotifyOwnersAsync(ReleaseInstalledType, current.Version, status.AutoUpdate, cancellationToken);
        }

        if (status.Latest is { } latest
            && await ledger.TryClaimAsync(ReleaseNoticeLedger.Available, latest.Version, cancellationToken))
        {
            logger.LogInformation("Release {Version} is available; notifying owners", latest.Version);
            await NotifyOwnersAsync(UpdateAvailableType, latest.Version, status.AutoUpdate, cancellationToken);
        }
    }

    private async Task NotifyOwnersAsync(
        string type, string version, AutoUpdateStatusDto autoUpdate, CancellationToken cancellationToken)
    {
        foreach (var tenant in await activeTenants.GetAsync(cancellationToken))
        {
            try
            {
                await NotifyOwnerAsync(tenant, type, version, autoUpdate, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not send the {Type} notice for {Version} to tenant {TenantId}", type, version, tenant.Id);
            }
        }
    }

    private async Task NotifyOwnerAsync(
        ActiveTenant tenant, string type, string version, AutoUpdateStatusDto autoUpdate, CancellationToken cancellationToken)
    {
        await using var scope = TenantJobScope.Open(
            serviceProvider,
            new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
            AuditEndpoint);

        var ownerId = await scope.ServiceProvider.GetRequiredService<ITenantOwnerResolver>()
            .GetOwnerSubjectIdAsync(tenant.Id, cancellationToken);
        if (ownerId is null)
            return;

        var notifications = scope.ServiceProvider.GetRequiredService<IInAppNotificationService>();
        var superseded = await scope.ServiceProvider.GetRequiredService<IInAppNotificationRepository>()
            .GetActiveBySourceAsync(ownerId, Source, cancellationToken);
        foreach (var older in superseded.Where(n => type == ReleaseInstalledType || n.Type == type))
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
}
