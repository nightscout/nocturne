using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Covers the shared restore quadrant on the shapes that stay off <c>V4RepositoryBase</c> —
/// TempBasal (span-shaped, keys on StartTimestamp) and PatientDevice (no timestamp column at all) —
/// plus BasalInjection, which reaches the same extension through the base. Pins the tenant scoping
/// and the already-live edge the extension is responsible for.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class SoftDeleteRestoreQuadrantTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
    private static readonly DateTime Base = new(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc);

    private readonly DbConnection _connection;
    private readonly NocturneDbContext _contextA;
    private readonly NocturneDbContext _contextB;
    private readonly TempBasalRepository _tempBasalsA;
    private readonly TempBasalRepository _tempBasalsB;
    private readonly PatientDeviceRepository _devicesA;
    private readonly PatientDeviceRepository _devicesB;
    private readonly BasalInjectionRepository _basalInjectionsA;
    private readonly BasalInjectionRepository _basalInjectionsB;

    public SoftDeleteRestoreQuadrantTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using (var seed = new NocturneDbContext(options) { TenantId = TenantA })
        {
            seed.Database.EnsureCreated();
            seed.Tenants.Add(new TenantEntity { Id = TenantA, Slug = "tenant-a" });
            seed.Tenants.Add(new TenantEntity { Id = TenantB, Slug = "tenant-b" });
            seed.SaveChanges();
        }

        _contextA = new NocturneDbContext(options) { TenantId = TenantA };
        _contextB = new NocturneDbContext(options) { TenantId = TenantB };

        var dedup = new Mock<IDeduplicationService>().Object;
        var audit = new Mock<IAuditContext>().Object;

        _tempBasalsA = new TempBasalRepository(
            new TestTenantDbContextFactory(_contextA), dedup, audit, NullLogger<TempBasalRepository>.Instance);
        _tempBasalsB = new TempBasalRepository(
            new TestTenantDbContextFactory(_contextB), dedup, audit, NullLogger<TempBasalRepository>.Instance);
        _devicesA = new PatientDeviceRepository(
            new TestTenantDbContextFactory(_contextA), NullLogger<PatientDeviceRepository>.Instance);
        _devicesB = new PatientDeviceRepository(
            new TestTenantDbContextFactory(_contextB), NullLogger<PatientDeviceRepository>.Instance);
        _basalInjectionsA = new BasalInjectionRepository(new TestTenantDbContextFactory(_contextA), audit);
        _basalInjectionsB = new BasalInjectionRepository(new TestTenantDbContextFactory(_contextB), audit);
    }

    public void Dispose()
    {
        _contextA.Dispose();
        _contextB.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TempBasal NewTempBasal(DateTime start) => new()
    {
        StartTimestamp = start,
        UtcOffset = 0,
        Rate = 1.0,
        Origin = TempBasalOrigin.Manual,
    };

    private static BasalInjection NewBasalInjection(string syncIdentifier) => new()
    {
        Timestamp = Base,
        DataSource = "aaps",
        SyncIdentifier = syncIdentifier,
        Units = 10.0,
    };

    private static PatientDevice NewDevice(string model) => new()
    {
        DeviceCategory = DeviceCategory.InsulinPump,
        Manufacturer = "Tandem",
        Model = model,
        IsCurrent = true,
    };

    /// <summary>Stamps <c>DeletedAt</c> directly so ordering assertions get distinct, known values.</summary>
    private static void SoftDelete<TEntity>(NocturneDbContext ctx, Guid id, DateTime deletedAt)
        where TEntity : class, ISoftDeletable
    {
        var entity = ctx.Set<TEntity>().IgnoreQueryFilters()
            .Single(e => EF.Property<Guid>(e, "Id") == id);
        entity.DeletedAt = deletedAt;
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
    }

    [Fact]
    public async Task RestoreAsync_ClearsDeletedAt_AndTheRecordIsReadableAgain()
    {
        var created = await _tempBasalsA.CreateAsync(NewTempBasal(Base), WriteOrigin.Live);
        SoftDelete<TempBasalEntity>(_contextA, created.Id, Base.AddHours(1));

        var restored = await _tempBasalsA.RestoreAsync(created.Id, WriteOrigin.Live);

        restored.Id.Should().Be(created.Id);
        _contextA.TempBasals.IgnoreQueryFilters()
            .Single(e => e.Id == created.Id).DeletedAt.Should().BeNull();
        (await _tempBasalsA.GetByIdAsync(created.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenTheRecordIsNotDeleted()
    {
        var created = await _tempBasalsA.CreateAsync(NewTempBasal(Base), WriteOrigin.Live);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _tempBasalsA.RestoreAsync(created.Id, WriteOrigin.Live));
    }

    [Fact]
    public async Task RestoreAsync_Throws_AndLeavesTheRowDeleted_WhenItBelongsToAnotherTenant()
    {
        var created = await _tempBasalsB.CreateAsync(NewTempBasal(Base), WriteOrigin.Live);
        SoftDelete<TempBasalEntity>(_contextB, created.Id, Base.AddHours(1));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _tempBasalsA.RestoreAsync(created.Id, WriteOrigin.Live));

        _contextB.TempBasals.IgnoreQueryFilters()
            .Single(e => e.Id == created.Id).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task BulkRestoreAsync_RestoresOnlyTheSoftDeletedIdsItWasGiven()
    {
        var asked = await _tempBasalsA.CreateAsync(NewTempBasal(Base), WriteOrigin.Live);
        var live = await _tempBasalsA.CreateAsync(NewTempBasal(Base.AddMinutes(30)), WriteOrigin.Live);
        var otherDeleted = await _tempBasalsA.CreateAsync(NewTempBasal(Base.AddMinutes(60)), WriteOrigin.Live);
        SoftDelete<TempBasalEntity>(_contextA, asked.Id, Base.AddHours(1));
        SoftDelete<TempBasalEntity>(_contextA, otherDeleted.Id, Base.AddHours(1));

        var restored = (await _tempBasalsA.BulkRestoreAsync(
            [asked.Id, live.Id, Guid.CreateVersion7()], WriteOrigin.Live)).ToList();

        restored.Select(r => r.Id).Should().Equal(asked.Id);
        _contextA.TempBasals.IgnoreQueryFilters()
            .Single(e => e.Id == otherDeleted.Id).DeletedAt.Should()
            .NotBeNull("a soft-deleted row the caller did not name must stay deleted");
        _contextA.TempBasals.IgnoreQueryFilters()
            .Count(e => e.DeletedAt == null).Should().Be(2);
    }

    [Fact]
    public async Task BulkRestoreAsync_LeavesAnotherTenantsSoftDeletedRowsAlone()
    {
        var mine = await _tempBasalsA.CreateAsync(NewTempBasal(Base), WriteOrigin.Live);
        var theirs = await _tempBasalsB.CreateAsync(NewTempBasal(Base), WriteOrigin.Live);
        SoftDelete<TempBasalEntity>(_contextA, mine.Id, Base.AddHours(1));
        SoftDelete<TempBasalEntity>(_contextB, theirs.Id, Base.AddHours(1));

        var restored = (await _tempBasalsA.BulkRestoreAsync([mine.Id, theirs.Id], WriteOrigin.Live)).ToList();

        restored.Select(r => r.Id).Should().Equal(mine.Id);
        _contextB.TempBasals.IgnoreQueryFilters()
            .Single(e => e.Id == theirs.Id).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDeletedAsync_OrdersByNewestDeletionFirst_AndPages()
    {
        var oldest = await _devicesA.CreateAsync(NewDevice("oldest"), WriteOrigin.Live);
        var middle = await _devicesA.CreateAsync(NewDevice("middle"), WriteOrigin.Live);
        var newest = await _devicesA.CreateAsync(NewDevice("newest"), WriteOrigin.Live);
        SoftDelete<PatientDeviceEntity>(_contextA, oldest.Id, Base);
        SoftDelete<PatientDeviceEntity>(_contextA, middle.Id, Base.AddHours(1));
        SoftDelete<PatientDeviceEntity>(_contextA, newest.Id, Base.AddHours(2));

        var page = (await _devicesA.GetDeletedAsync(limit: 2, offset: 0)).ToList();
        var next = (await _devicesA.GetDeletedAsync(limit: 2, offset: 2)).ToList();

        page.Select(d => d.Id).Should().Equal(newest.Id, middle.Id);
        next.Select(d => d.Id).Should().Equal(oldest.Id);
    }

    [Fact]
    public async Task GetDeletedAsync_ExcludesLiveRowsAndOtherTenants()
    {
        var mine = await _devicesA.CreateAsync(NewDevice("mine"), WriteOrigin.Live);
        await _devicesA.CreateAsync(NewDevice("still-live"), WriteOrigin.Live);
        var theirs = await _devicesB.CreateAsync(NewDevice("theirs"), WriteOrigin.Live);
        SoftDelete<PatientDeviceEntity>(_contextA, mine.Id, Base);
        SoftDelete<PatientDeviceEntity>(_contextB, theirs.Id, Base);

        var deleted = (await _devicesA.GetDeletedAsync(limit: 50, offset: 0)).ToList();

        deleted.Select(d => d.Id).Should().Equal(mine.Id);
    }

    [Fact]
    public async Task CountDeletedAsync_CountsOnlyThisTenantsDeletedRows()
    {
        var mine = await _devicesA.CreateAsync(NewDevice("mine"), WriteOrigin.Live);
        await _devicesA.CreateAsync(NewDevice("still-live"), WriteOrigin.Live);
        var theirs = await _devicesB.CreateAsync(NewDevice("theirs"), WriteOrigin.Live);
        SoftDelete<PatientDeviceEntity>(_contextA, mine.Id, Base);
        SoftDelete<PatientDeviceEntity>(_contextB, theirs.Id, Base);

        (await _devicesA.CountDeletedAsync()).Should().Be(1);
        (await _devicesB.CountDeletedAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RestoreAsync_ClearsDeletedAt_OnAnEntityWithNoTimestampColumn()
    {
        var created = await _devicesA.CreateAsync(NewDevice("t:slim X2"), WriteOrigin.Live);
        SoftDelete<PatientDeviceEntity>(_contextA, created.Id, Base);

        var restored = await _devicesA.RestoreAsync(created.Id, WriteOrigin.Live);

        restored.Id.Should().Be(created.Id);
        (await _devicesA.CountDeletedAsync()).Should().Be(0);
        (await _devicesA.GetAllAsync()).Select(d => d.Id).Should().Equal(created.Id);
    }

    [Fact]
    public async Task RestoreAsync_AndGetDeletedAsync_WorkThroughV4RepositoryBase()
    {
        var deleted = await _basalInjectionsA.CreateAsync(NewBasalInjection("sync-1"), WriteOrigin.Live);
        await _basalInjectionsA.CreateAsync(NewBasalInjection("sync-2"), WriteOrigin.Live);
        var theirs = await _basalInjectionsB.CreateAsync(NewBasalInjection("sync-3"), WriteOrigin.Live);
        SoftDelete<BasalInjectionEntity>(_contextA, deleted.Id, Base);
        SoftDelete<BasalInjectionEntity>(_contextB, theirs.Id, Base);

        (await _basalInjectionsA.GetDeletedAsync(limit: 50, offset: 0)).Select(b => b.Id)
            .Should().Equal(deleted.Id);
        (await _basalInjectionsA.CountDeletedAsync()).Should().Be(1);

        (await _basalInjectionsA.RestoreAsync(deleted.Id, WriteOrigin.Live)).Id.Should().Be(deleted.Id);

        (await _basalInjectionsA.CountDeletedAsync()).Should().Be(0);
        (await _basalInjectionsB.CountDeletedAsync()).Should().Be(1);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _basalInjectionsA.RestoreAsync(theirs.Id, WriteOrigin.Live));
    }

    [Fact]
    public async Task RestoreAsync_Throws_ForAnotherTenantsDeviceWithNoTimestampColumn()
    {
        var theirs = await _devicesB.CreateAsync(NewDevice("theirs"), WriteOrigin.Live);
        SoftDelete<PatientDeviceEntity>(_contextB, theirs.Id, Base);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _devicesA.RestoreAsync(theirs.Id, WriteOrigin.Live));

        (await _devicesB.CountDeletedAsync()).Should().Be(1);
    }
}
