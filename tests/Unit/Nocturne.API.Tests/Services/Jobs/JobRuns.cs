using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>A first run of a job, as the scheduler would start it, for a test to call directly.</summary>
internal static class JobRuns
{
    public static JobRun For(Guid tenant, IServiceProvider services, DateTimeOffset? startedAt = null) =>
        new(new TenantContext(tenant, tenant.ToString("N"), "Test", true, false), null, 0, services, startedAt ?? DateTimeOffset.UtcNow);

    public static JobRun Platform(IServiceProvider services, DateTimeOffset? startedAt = null) =>
        new(null, null, 0, services, startedAt ?? DateTimeOffset.UtcNow);
}
