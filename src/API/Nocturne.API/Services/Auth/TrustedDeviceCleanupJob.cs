using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Removes trusted devices whose trust has expired. An expired row is already refused at sign-in,
/// so this only keeps the table from growing. <c>trusted_devices</c> is not tenant-scoped, so this
/// is one platform job.
/// </summary>
public class TrustedDeviceCleanupJob(ILogger<TrustedDeviceCleanupJob> logger) : IScheduledJob
{
    internal const string JobName = "trusted-device-cleanup";

    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public string Name => JobName;

    public JobEnrolment Enrolment => JobEnrolment.Platform;

    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var cutoff = run.StartedAt.UtcDateTime;

        int deleted;
        await using (var platform = await run.Services.GetRequiredService<INocturneDbContextFactory>().CreatePlatformAsync(ct))
        {
            deleted = await platform.TrustedDevices.Where(d => d.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        }

        if (deleted > 0)
            logger.LogInformation("Removed {TrustedDevices} expired trusted devices", deleted);

        return JobOutcome.Resume(null, run.StartedAt + Interval);
    }
}
