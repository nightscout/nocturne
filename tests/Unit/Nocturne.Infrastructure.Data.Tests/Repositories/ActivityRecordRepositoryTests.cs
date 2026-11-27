using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
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

    /// <summary>
    /// A reading sent again at a time below the millisecond holds what its row stored, which the save cut to
    /// the millisecond, so it is unchanged.
    /// </summary>
    [Fact]
    public async Task AReadingSentAgainBelowTheMillisecondIsUnchanged()
    {
        var at = Stamp.AddTicks(1_234_560);
        var heart = Heart(Id);
        heart.Timestamp = at;
        await StoreAsync(_tenant, heart);
        var reading = Reading(Id, 72);
        reading.Timestamp = at;

        var written = await Repository(_tenant).WriteHealthSeriesAsync([(0, reading)], []);

        written.HeartRateWrites.Unchanged.Select(u => u.Index).Should().Equal(0);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ANullOrEmptyIdIsDeletedByNobody(string? id)
    {
        await StoreAsync(_tenant, Heart(null), deletedByUser: true);
        await StoreAsync(_tenant, new StepCountEntity { Id = Guid.CreateVersion7(), Timestamp = Stamp, Metric = 800 }, deletedByUser: true);
        await StoreAsync(_tenant, new SleepSessionEntity { Id = Guid.CreateVersion7(), StartTime = Stamp, EndTime = Stamp.AddHours(1) }, deletedByUser: true);
        await StoreAsync(_tenant, new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), Category = nameof(StateSpanCategory.Exercise), State = "run",
            StartTimestamp = Stamp, Source = "manual",
        }, deletedByUser: true);

        (await Repository(_tenant).IsDeletedByUserAsync(id!)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ANullOrEmptyIdDeletesNothing(string? id)
    {
        await StoreAsync(_tenant, Heart(null));
        await StoreAsync(_tenant, new StepCountEntity { Id = Guid.CreateVersion7(), Timestamp = Stamp, Metric = 800 });

        (await Repository(_tenant).DeleteByLegacyIdAsync(id!)).Should().Be(0);

        await using var db = _db.CreateContext(_tenant);
        (await db.HeartRates.CountAsync()).Should().Be(1);
        (await db.StepCounts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AnIdWithNoOriginalIdIsFoundByTheKeyItDerivesTo()
    {
        const string objectId = "b0000000000000000000000b";
        var row = Heart(null);
        row.Id = RecordIdKey.ParseIdToGuid(objectId);
        await StoreAsync(_tenant, row, deletedByUser: true);
        await StoreAsync(_tenant, new StepCountEntity
        {
            Id = RecordIdKey.ParseIdToGuid("d0000000000000000000000d"), Timestamp = Stamp, Metric = 800,
        });

        (await Repository(_tenant).IsDeletedByUserAsync(objectId)).Should().BeTrue();
        (await Repository(_tenant).DeleteByLegacyIdAsync("d0000000000000000000000d")).Should().Be(1);
    }

    [Fact]
    public async Task AnActivityStateSpanIsDeletedByUserUnderItsLegacyIdOrItsDerivedKeyInItsCategoriesOnly()
    {
        const string legacy = "e0000000000000000000000e";
        const string derived = "f0000000000000000000000f";
        StateSpanEntity Span(StateSpanCategory category, string? legacyId, Guid? key = null) => new()
        {
            Id = key ?? Guid.CreateVersion7(), Category = category.ToString(), State = "run",
            StartTimestamp = Stamp, Source = "manual", LegacyId = legacyId,
        };
        await StoreAsync(_tenant, Span(StateSpanCategory.Exercise, legacy), deletedByUser: true);
        await StoreAsync(_tenant, Span(StateSpanCategory.Exercise, null, RecordIdKey.ParseIdToGuid(derived)), deletedByUser: true);
        await StoreAsync(_tenant, Span(StateSpanCategory.PumpMode, "9000000000000000000000a9"), deletedByUser: true);

        (await Repository(_tenant).IsDeletedByUserAsync(legacy)).Should().BeTrue();
        (await Repository(_tenant).IsDeletedByUserAsync(derived)).Should().BeTrue();
        (await Repository(_tenant).IsDeletedByUserAsync("9000000000000000000000a9")).Should().BeFalse();
    }
}
