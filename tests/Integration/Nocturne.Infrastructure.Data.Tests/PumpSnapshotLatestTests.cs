using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The newest pump snapshot, against PostgreSQL as <c>nocturne_app</c>: two at one instant are ordered by id, as the
/// window read the device status dataset takes its tiles from orders them.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class PumpSnapshotLatestTests(RlsCompletenessFixture fx)
{
    private static readonly DateTime At = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetLatestAsync_TwoSnapshotsAtOneInstant_ReturnsTheLaterIdTheDatasetTilesTake(bool laterStoredFirst)
    {
        var tenant = Guid.NewGuid();
        await using (var migrator = await fx.OpenMigratorConnectionAsync())
            await migrator.SeedTenantAsync(tenant, $"psl-{tenant:N}", "pump-snapshot-latest");
        await using var conn = await fx.OpenAppConnectionAsync();
        await conn.PinTenantAsync(tenant);
        // The (tenant_id, timestamp DESC, id DESC) index would give the later id on its own; without it the order is
        // the query's alone.
        await conn.ExecuteAsync("SET enable_indexscan = off; SET enable_bitmapscan = off; SET enable_indexonlyscan = off");
        await using var context = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(conn).Options)
        {
            TenantId = tenant,
        };
        var earlier = new PumpSnapshotEntity { Id = Guid.CreateVersion7(At), Timestamp = At, Reservoir = 80, BatteryPercent = 90 };
        var later = new PumpSnapshotEntity { Id = Guid.CreateVersion7(At.AddMilliseconds(1)), Timestamp = At, Reservoir = 12, BatteryPercent = 40 };
        context.PumpSnapshots.AddRange(laterStoredFirst ? [later, earlier] : [earlier, later]);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var repository = new PumpSnapshotRepository(
            new TestNocturneDbContextFactory(context), new SystemAuditContext(), NullLogger<PumpSnapshotRepository>.Instance);

        var latest = await repository.GetLatestAsync(asOf: null);
        var tiles = (await repository.GetAsync(At.AddHours(-24), At, null, null, 10_000, 0, descending: false))
            .OrderBy(p => p.Mills)
            .LastOrDefault(p => p.Reservoir is not null || p.BatteryPercent is not null);

        latest!.Id.Should().Be(later.Id);
        tiles!.Id.Should().Be(latest.Id, "the newest snapshot is the one the device status tiles report");
    }
}
