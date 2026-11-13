using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The heart rates and step counts an activity batch writes, and the tombstones that hold an
/// activity's id, with another tenant's and another source's rows under the same keys beside them.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityRecordRepositoryTests : IDisposable
{
    private const string Id = "a0000000000000000000000a";
    private static readonly DateTime Stamp = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public ActivityRecordRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private ActivityRecordRepository Repository(Guid tenant) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant)), TimeProvider.System);

    private static HeartRateEntity Heart(string? originalId, string? source = null, string? syncIdentifier = null) => new()
    {
        Id = Guid.CreateVersion7(), Timestamp = Stamp, Bpm = 72, OriginalId = originalId,
        DataSource = source, SyncIdentifier = syncIdentifier,
    };

    private static HeartRate Reading(string? id, int bpm, string? source = null, string? syncIdentifier = null) => new()
    {
        Id = id, Mills = Stamp.ToMills(), Bpm = bpm, DataSource = source, SyncIdentifier = syncIdentifier,
    };

    private async Task StoreAsync(Guid tenant, ITenantScoped row, bool deletedByUser = false)
    {
        await using var db = _db.CreateContext(tenant);
        row.TenantId = tenant;
        db.Add(row);
        if (deletedByUser)
        {
            ((ISoftDeletable)row).DeletedAt = Stamp;
            db.Entry(row).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = true;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnotherTenantsTombstoneUnderTheIdDoesNotHoldTheWrite()
    {
        await StoreAsync(_otherTenant, Heart(Id), deletedByUser: true);

        var written = await Repository(_tenant).WriteHealthSeriesAsync([(0, Reading(Id, 80))], []);

        written.HeartRateWrites.DeletedByUser.Should().BeEmpty();
        written.HeartRateWrites.Created.Should().ContainSingle().Which.Index.Should().Be(0);
        await using var other = _db.CreateContext(_otherTenant);
        (await other.HeartRates.IncludingDeleted().SingleAsync()).Bpm.Should().Be(72);
    }

    [Fact]
    public async Task AUserTombstoneHoldsItsIdAndItsSourcesSyncKeyOnly()
    {
        await StoreAsync(_tenant, Heart(Id), deletedByUser: true);
        await StoreAsync(_tenant, Heart(null, "xdrip", "hr-bpm:1"), deletedByUser: true);

        var written = await Repository(_tenant).WriteHealthSeriesAsync(
            [(0, Reading(Id, 80)), (1, Reading(null, 81, "xdrip", "hr-bpm:1")), (2, Reading(null, 82, "watch", "hr-bpm:1"))],
            []);

        written.HeartRateWrites.DeletedByUser.Should().Equal(0, 1);
        written.HeartRateWrites.Created.Select(c => (c.Index, c.Record.Bpm)).Should().Equal((2, 82));
    }

    [Fact]
    public async Task AStoredRowIsUpdatedOrLeftUnchangedAndTheLastOfOneKeyWins()
    {
        await StoreAsync(_tenant, Heart(Id));

        var written = await Repository(_tenant).WriteHealthSeriesAsync(
            [(0, Reading(Id, 72)), (1, Reading("b0000000000000000000000b", 60)), (2, Reading("b0000000000000000000000b", 61))],
            []);

        written.HeartRateWrites.Unchanged.Select(u => u.Index).Should().Equal(0);
        written.HeartRateWrites.Created.Select(c => (c.Index, c.Record.Bpm)).Should().Equal((2, 61));
        written.HeartRateWrites.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task AnIdIsDeletedByUserOnlyInItsOwnTenantAndNotWhenSwept()
    {
        await StoreAsync(_otherTenant, Heart(Id), deletedByUser: true);
        var swept = Heart("c0000000000000000000000c");
        swept.DeletedAt = Stamp;
        await StoreAsync(_tenant, swept);

        (await Repository(_tenant).IsDeletedByUserAsync(Id)).Should().BeFalse();
        (await Repository(_otherTenant).IsDeletedByUserAsync(Id)).Should().BeTrue();
        (await Repository(_tenant).IsDeletedByUserAsync("c0000000000000000000000c")).Should().BeFalse();
    }

    [Fact]
    public async Task ADeleteTakesTheStepCountBesideTheHeartRate()
    {
        await StoreAsync(_tenant, Heart(Id));
        await StoreAsync(_tenant, new StepCountEntity { Id = Guid.CreateVersion7(), Timestamp = Stamp, Metric = 800, OriginalId = Id });

        (await Repository(_tenant).DeleteByLegacyIdAsync(Id)).Should().Be(2);

        await using var db = _db.CreateContext(_tenant);
        (await db.StepCounts.IncludingDeleted().SingleAsync()).DeletedAt.Should().NotBeNull();
        (await db.HeartRates.IncludingDeleted().SingleAsync()).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ADeleteTakesOnlyTheTenantsRows()
    {
        await StoreAsync(_tenant, Heart(Id));
        await StoreAsync(_otherTenant, Heart(Id));

        (await Repository(_tenant).DeleteByLegacyIdAsync(Id)).Should().Be(1);

        await using var db = _db.CreateContext(_tenant);
        (await db.HeartRates.AnyAsync()).Should().BeFalse();
        await using var other = _db.CreateContext(_otherTenant);
        (await other.HeartRates.SingleAsync()).DeletedAt.Should().BeNull();
    }
}
