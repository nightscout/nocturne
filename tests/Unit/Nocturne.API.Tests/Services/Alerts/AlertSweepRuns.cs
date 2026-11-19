using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>Runs of <see cref="AlertSweepJob"/> as the job scheduler starts them, for a test to call directly.</summary>
internal static class AlertSweepRuns
{
    /// <summary>
    /// A repository whose tenant is active and whose four sweep reads find nothing, for a test to
    /// override the read it drives.
    /// </summary>
    public static Mock<IAlertRepository> Repository(Guid tenant, Func<DateTime?> lastReadingAt)
    {
        var repository = new Mock<IAlertRepository>();
        repository
            .Setup(r => r.GetTenantAlertContextAsync(tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TenantAlertContext(tenant, "owner", "slug", "Slug", true, lastReadingAt()));
        repository
            .Setup(r => r.GetExcursionsInHysteresisAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(r => r.GetExpiredSnoozedInstancesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository
            .Setup(r => r.GetAutoResolveExcursionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return repository;
    }

    /// <summary>
    /// Runs <paramref name="job"/> for <paramref name="tenant"/> at <paramref name="at"/> in a scope
    /// opened as the scheduler opens one.
    /// </summary>
    public static async Task<JobOutcome> RunAsync(
        this AlertSweepJob job, IServiceProvider root, Guid tenant, DateTimeOffset at, bool active = true)
    {
        var context = new TenantContext(tenant, "slug", "Slug", active, IsDemo: false);
        await using var scope = TenantJobScope.Open(root, context, $"job:{AlertSweepJob.JobName}");
        return await job.RunAsync(new JobRun(context, null, 0, scope.ServiceProvider, at), CancellationToken.None);
    }
}
