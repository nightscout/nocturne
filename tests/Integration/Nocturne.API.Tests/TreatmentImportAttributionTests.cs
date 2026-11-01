using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Audit;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// An import writes as the system, not as the user who started it, removals included.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class TreatmentImportAttributionTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Fact]
    public async Task An_import_that_drops_a_meals_carbs_drops_them_as_the_system()
    {
        var at = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        var group = Guid.CreateVersion7();
        var importer = Guid.CreateVersion7();

        await InScopeAsync(importer, async services =>
        {
            await services.GetRequiredService<IBolusRepository>().BulkCreateAsync(
                [new Bolus { LegacyId = "import-meal-b", Timestamp = at, Insulin = 2, CorrelationId = group, DataSource = "connector" }],
                WriteOrigin.Backfill);
            await services.GetRequiredService<ICarbIntakeRepository>().BulkCreateAsync(
                [new CarbIntake { LegacyId = "import-meal-c", Timestamp = at, Carbs = 20, CorrelationId = group, DataSource = "connector" }],
                WriteOrigin.Backfill);
        });

        await InScopeAsync(importer, services => services.GetRequiredService<ITreatmentService>().ImportTreatmentsAsync(
            [new Treatment { Id = "import-meal-b", EventType = "Correction Bolus", Insulin = 2, Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds() }]));

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var carbs = await db.Set<CarbIntakeEntity>().IgnoreQueryFilters().SingleAsync(c => c.LegacyId == "import-meal-c");
        carbs.DeletedAt.Should().NotBeNull("the bolus no longer carries carbs");
        (await db.Set<MutationAuditLogEntity>().IgnoreQueryFilters().Where(a => a.EntityId == carbs.Id && a.Action == "delete").ToListAsync())
            .Should().BeEmpty("a system-attributed removal writes no audit row under the importing user");
    }

    private async Task InScopeAsync(Guid subject, Func<IServiceProvider, Task> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        var audit = (AuditContext)scope.ServiceProvider.GetRequiredService<IAuditContext>();
        audit.SubjectId = subject;
        audit.SubjectName = "importer";
        audit.AuthType = "Session";
        await action(scope.ServiceProvider);
    }
}
