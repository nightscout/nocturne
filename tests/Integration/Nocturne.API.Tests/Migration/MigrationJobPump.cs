using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// Runs the fixture tenant's live migration job to its end, slice after slice, as the scheduler
/// the fixture's host does not run would (<see cref="ApiIntegrationTestFixture.RemoveJobScheduler"/>).
/// <see cref="MigrationRunJobIntegrationTests"/> covers the scheduler's own claim of the job.
/// </summary>
internal static class MigrationJobPump
{
    public static async Task RunAsync(ApiIntegrationTestFixture fixture)
    {
        var tenant = new TenantContext(
            fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, IsDemo: false);
        var migrations = fixture.Services.GetRequiredService<IMigrationJobService>();
        while (await migrations.RunSliceAsync(tenant, CancellationToken.None))
        {
        }
    }
}
