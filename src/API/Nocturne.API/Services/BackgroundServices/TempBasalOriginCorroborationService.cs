using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Runs <see cref="TempBasalOriginCorroborator"/> for every tenant after startup and every
/// <see cref="Interval"/> after that, on the temp basals written at least <see cref="SettlingDelay"/> ago,
/// first reopening those that snapshots written since the last run now lie near
/// (<see cref="TempBasalOriginCorroborator.ReopenNearNewSnapshotsAsync"/>).
/// </summary>
/// <remarks>
/// Per tenant because <c>temp_basals</c> and <c>aps_snapshots</c> are RLS-scoped and the policy is
/// fail-closed. A tenant that fails is logged and skipped, and the next run retries it. A tenant
/// whose temp basals changed has its cached treatment and statistics reads dropped.
/// </remarks>
internal sealed class TempBasalOriginCorroborationService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    ILogger<TempBasalOriginCorroborationService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a temp basal is left after it is written, for the devicestatus uploaded around it
    /// to arrive: an uploader sends the two separately, and a phone that was offline sends both late.
    /// </summary>
    internal static readonly TimeSpan SettlingDelay = TimeSpan.FromMinutes(30);

    private const string AuditEndpoint = "service:temp-basal-origin";

    private readonly SnapshotWatermarks _watermarks = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            using var timer = new PeriodicTimer(Interval, timeProvider);
            do
            {
                await RunAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var factory = serviceProvider.GetRequiredService<INocturneDbContextFactory>();
            List<(Guid Id, string Slug, string DisplayName, bool IsActive)> tenants;
            await using (var platform = await factory.CreatePlatformAsync(ct))
            {
                tenants = (await platform.Tenants.AsNoTracking()
                        .Select(t => new { t.Id, t.Slug, t.DisplayName, t.IsActive })
                        .ToListAsync(ct))
                    .Select(t => (t.Id, t.Slug, t.DisplayName, t.IsActive))
                    .ToList();
            }

            var settledBefore = timeProvider.GetUtcNow().UtcDateTime - SettlingDelay;
            var writtenTotal = 0;
            var failed = 0;
            foreach (var tenant in tenants)
            {
                try
                {
                    int written;
                    DateTime? snapshotsWrittenThrough;
                    await using (var db = await factory.CreateForTenantAsync(tenant.Id, ct))
                    {
                        (_, snapshotsWrittenThrough) = await TempBasalOriginCorroborator.ReopenNearNewSnapshotsAsync(
                            db,
                            tenant.Id,
                            _watermarks.Since(tenant.Id, timeProvider.GetUtcNow().UtcDateTime),
                            ApsSnapshotCurvesBackfillService.PageSize,
                            ct);
                        written = await TempBasalOriginCorroborator.CorroborateTenantAsync(
                            db,
                            tenant.Id,
                            settledBefore,
                            ApsSnapshotCurvesBackfillService.PageSize,
                            ApsSnapshotCurvesBackfillService.PauseAfterWrite,
                            ApsSnapshotCurvesBackfillService.PauseAfterRead,
                            ct);
                    }

                    _watermarks.Advance(tenant.Id, snapshotsWrittenThrough);
                    if (written == 0)
                        continue;

                    writtenTotal += written;
                    await using var job = TenantJobScope.Open(
                        serviceProvider,
                        new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, IsDemo: false),
                        AuditEndpoint);
                    var cache = job.ServiceProvider.GetRequiredService<ITenantCache>();
                    await cache.RemoveFamilyAsync(TenantCacheFamily.Treatments, ct);
                    await cache.RemoveFamilyAsync(TenantCacheFamily.Statistics, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    logger.LogError(ex, "Error corroborating temp basal origins for tenant {TenantId}", tenant.Id);
                }
            }

            if (writtenTotal > 0 || failed > 0)
            {
                logger.LogInformation(
                    "Temp basal origin corroboration finished: {Written} temp basal(s) judged across {Tenants} tenant(s), {Failed} failed",
                    writtenTotal,
                    tenants.Count,
                    failed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error corroborating temp basal origins");
        }
    }
}

/// <summary>Per tenant, the latest snapshot write a run has reopened temp basals for.</summary>
internal sealed class SnapshotWatermarks
{
    /// <summary>
    /// How far back the first run after a start looks for snapshot writes, the watermark being kept
    /// in memory only. A week covers a restart in the middle of an import; a snapshot written longer
    /// before a restart than that, beside a temp that had already settled, is not looked at again.
    /// Each start therefore re-reads a week of every tenant's snapshot writes, and reopens the few
    /// Uncorroborated temps beside them to be judged the same again.
    /// </summary>
    internal static readonly TimeSpan RestartLookback = TimeSpan.FromDays(7);

    private readonly Dictionary<Guid, DateTime> _writtenThrough = [];

    public DateTime Since(Guid tenantId, DateTime now) =>
        _writtenThrough.TryGetValue(tenantId, out var through)
            ? through - TempBasalOriginCorroborator.CommitMargin
            : now - RestartLookback;

    public void Advance(Guid tenantId, DateTime? writtenThrough)
    {
        if (writtenThrough is { } through)
            _writtenThrough[tenantId] = through;
    }
}
