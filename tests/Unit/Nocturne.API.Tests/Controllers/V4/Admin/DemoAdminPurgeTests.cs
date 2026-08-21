using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Admin;
using Nocturne.API.Services.Demo;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Admin;

/// <summary>
/// The demo delete endpoints are what the demo service calls to empty the tenant before a
/// regenerate, so they have to reach records a visitor deleted — those are soft-deleted rows, which
/// the global filter hid from the purge.
/// </summary>
[Trait("Category", "Unit")]
public class DemoAdminPurgeTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-0000000000de");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;

    public DemoAdminPurgeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "demo", IsDemo = true, IsActive = true });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext() => new(_dbOptions) { TenantId = TenantId };

    private DemoAdminController BuildController()
    {
        var dbFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions));

        var tenantService = new Mock<ITenantService>();
        var demoTenantService = new DemoTenantService(
            dbFactory.Object,
            tenantService.Object,
            TestPublicAccessCache.Create(),
            new Mock<ICacheService>().Object,
            new Mock<ILogger<DemoTenantService>>().Object);

        return new DemoAdminController(tenantService.Object, demoTenantService, dbFactory.Object);
    }

    [Fact]
    public async Task DeleteEntries_PurgesSoftDeletedGlucose()
    {
        var deletedAt = DateTime.UtcNow;
        await using (var seed = NewContext())
        {
            seed.SensorGlucose.Add(new SensorGlucoseEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
                Timestamp = DateTime.UtcNow, Mgdl = 120, DeletedAt = deletedAt,
            });
            seed.MeterGlucose.Add(new MeterGlucoseEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
                Timestamp = DateTime.UtcNow, Mgdl = 110, DeletedAt = deletedAt,
            });
            seed.Calibrations.Add(new CalibrationEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
                Timestamp = DateTime.UtcNow, DeletedAt = deletedAt,
            });
            await seed.SaveChangesAsync();
        }

        await BuildController().DeleteEntries(CancellationToken.None);

        await using var assert = NewContext();
        (await assert.SensorGlucose.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.MeterGlucose.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.Calibrations.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteTreatments_PurgesSoftDeletedTreatments()
    {
        var deletedAt = DateTime.UtcNow;
        await using (var seed = NewContext())
        {
            seed.Boluses.Add(new BolusEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
                Timestamp = DateTime.UtcNow, Insulin = 1, DeletedAt = deletedAt,
            });
            seed.CarbIntakes.Add(new CarbIntakeEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
                Timestamp = DateTime.UtcNow, Carbs = 30, DeletedAt = deletedAt,
            });
            seed.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, Device = DataSources.DemoService,
                Timestamp = DateTime.UtcNow, AidAlgorithm = "Loop", DeletedAt = deletedAt,
            });
            await seed.SaveChangesAsync();
        }

        await BuildController().DeleteTreatments(CancellationToken.None);

        await using var assert = NewContext();
        (await assert.Boluses.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.CarbIntakes.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await assert.ApsSnapshots.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
