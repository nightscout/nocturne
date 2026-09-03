using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// A purge exists to empty a table, so it has to reach rows the soft-delete filter hides — and it
/// has to stop at the context's tenant, which the parameterless <c>IgnoreQueryFilters()</c> would
/// not.
/// </summary>
[Trait("Category", "Unit")]
public class PurgeExtensionsTests : IDisposable
{
    private const string Source = "demo-service";

    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public PurgeExtensionsTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenantA, "a").SeedTenant(_tenantB, "b");

        SeedBoluses(_tenantA);
        SeedBoluses(_tenantB);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext(Guid tenantId) => _db.CreateContext(tenantId);

    /// <summary>One live and one already-soft-deleted bolus, both carrying <see cref="Source"/>.</summary>
    private void SeedBoluses(Guid tenantId)
    {
        using var db = NewContext(tenantId);
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DataSource = Source,
            Timestamp = DateTime.UtcNow,
            Insulin = 1,
        });
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DataSource = Source,
            Timestamp = DateTime.UtcNow,
            Insulin = 2,
            DeletedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private async Task<int> CountAllAsync(Guid tenantId)
    {
        await using var db = NewContext(tenantId);
        return await db.Boluses.IgnoreQueryFilters().CountAsync(b => b.TenantId == tenantId);
    }

    [Fact]
    public async Task PurgeAsync_RemovesSoftDeletedRowsToo()
    {
        await using var db = NewContext(_tenantA);

        var purged = await db.Boluses.PurgeAsync(b => b.DataSource == Source);

        purged.Should().Be(2, "the soft-deleted row is exactly what the purge exists to remove");
        (await CountAllAsync(_tenantA)).Should().Be(0);
    }

    [Fact]
    public async Task PurgeAsync_LeavesOtherTenantsRowsAlone()
    {
        await using var db = NewContext(_tenantA);

        await db.Boluses.PurgeAsync(b => b.DataSource == Source);

        (await CountAllAsync(_tenantB)).Should().Be(2, "lifting the tenant filter would take these too");
    }

    [Fact]
    public async Task PurgeAsync_WithoutPredicate_EmptiesOnlyTheContextTenantsRows()
    {
        await using var db = NewContext(_tenantA);

        var purged = await db.Boluses.PurgeAsync();

        purged.Should().Be(2);
        (await CountAllAsync(_tenantA)).Should().Be(0);
        (await CountAllAsync(_tenantB)).Should().Be(2);
    }

    [Fact]
    public async Task PurgeAsync_DeletesFromASetWithNoSoftDeleteFilter()
    {
        await using (var seed = NewContext(_tenantA))
        {
            seed.StateSpans.Add(new StateSpanEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantA,
                Source = Source,
                Category = "PumpMode",
                State = "Automatic",
                StartTimestamp = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext(_tenantA);

        (await db.StateSpans.PurgeAsync(s => s.Source == Source)).Should().Be(1);
    }
}
