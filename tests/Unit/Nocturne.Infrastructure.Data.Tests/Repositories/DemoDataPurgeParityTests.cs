using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="DemoDataPurgeRepository"/> has three entry points, the scope's purge and the two a
/// platform admin calls per tenant, and each has to empty exactly the same tables and account for the
/// same rows. A table added to the seeder and to one group but not another leaves a row behind or a
/// count short here.
/// </summary>
[Trait("Category", "Unit")]
public class DemoDataPurgeParityTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly TenantContext Tenant = new(TenantId, "demo", "Nocturne Demo", true, true);

    /// <summary>Number of rows <see cref="SeedOneDemoRowOfEveryType"/> writes.</summary>
    private const int SeededRows = 14;

    private readonly SqliteTestDatabase _db;
    private readonly List<(Guid? Tenant, IReadOnlyCollection<Type> Models)> _tenantReports = [];
    private readonly Mock<ILegacyReadCacheInvalidation> _scopeReads = new();
    private readonly List<Type> _scopeReported = [];

    public DemoDataPurgeParityTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        using var db = NewContext();
        db.Tenants.Add(new TenantEntity { Id = TenantId, Slug = "demo", IsDemo = true });
        db.SaveChanges();

        _scopeReads
            .Setup(r => r.WrittenAsync(It.IsAny<IReadOnlyCollection<Type>>()))
            .Callback((IReadOnlyCollection<Type> models) => _scopeReported.AddRange(models))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private NocturneDbContext NewContext() => _db.CreateContext(TenantId);

    private INocturneDbContextFactory TenantlessScopeFactory() => _db.CreateContext(Guid.Empty).AsFactory();

    /// <summary>
    /// A purge whose own scope resolves no tenant, as a platform admin's request does: its factory's
    /// <c>CreateAsync</c> reads as no tenant, so only a context pinned to the tenant sees the demo rows,
    /// and every report made from a scope the purge opens is recorded with the tenant that scope read as.
    /// </summary>
    private DemoDataPurgeRepository PlatformAdminPurge()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, ScopeTenantAccessor>();
        services.AddScoped(_ => Mock.Of<ICategoryReadContext>());
        services.AddScoped<ILegacyReadCacheInvalidation>(
            sp => new TenantRecordingLegacyReads(sp.GetRequiredService<ITenantAccessor>(), _tenantReports));

        return new DemoDataPurgeRepository(
            TenantlessScopeFactory(), TimeProvider.System,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private DemoDataPurgeRepository ScopePurge() =>
        new(NewContext().AsFactory(), TimeProvider.System, Mock.Of<IServiceScopeFactory>(), _scopeReads.Object);

    private DemoDataPurgeRepository ScopePurgeOver(NocturneDbContext context) =>
        new(context.AsFactory(), TimeProvider.System, Mock.Of<IServiceScopeFactory>(), _scopeReads.Object);

    /// <summary>One demo row in every table the demo seeder and the realtime tick write to.</summary>
    private void SeedOneDemoRowOfEveryType(bool softDeleted = false)
    {
        var timestamp = DateTime.UtcNow;
        var timestampMills = new DateTimeOffset(timestamp).ToUnixTimeMilliseconds();
        var demoDeviceId = Guid.CreateVersion7();
        DateTime? deletedAt = softDeleted ? timestamp : null;

        using var db = NewContext();
        db.PatientDevices.Add(new PatientDeviceEntity
        {
            Id = demoDeviceId, TenantId = TenantId, DeviceCategory = "cgm",
            Manufacturer = "Dexcom", Model = "G7", SerialNumber = "demo-cgm",
        });
        db.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Mgdl = 120, PatientDeviceId = demoDeviceId, DeletedAt = deletedAt,
        });
        db.MeterGlucose.Add(new MeterGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Mgdl = 118, DeletedAt = deletedAt,
        });
        db.Calibrations.Add(new CalibrationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Slope = 1, Intercept = 0, DeletedAt = deletedAt,
        });
        db.Boluses.Add(new BolusEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Insulin = 1, DeletedAt = deletedAt,
        });
        db.CarbIntakes.Add(new CarbIntakeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Carbs = 20, DeletedAt = deletedAt,
        });
        db.BGChecks.Add(new BGCheckEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Glucose = 100, DeletedAt = deletedAt,
        });
        db.Notes.Add(new NoteEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Text = "note", DeletedAt = deletedAt,
        });
        db.DeviceEvents.Add(new DeviceEventEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, EventType = "SiteChange", DeletedAt = deletedAt,
        });
        db.BolusCalculations.Add(new BolusCalculationEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, DeletedAt = deletedAt,
        });
        db.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Timestamp = timestamp, Rate = 0.5, Origin = "pump", DeletedAt = deletedAt,
        });
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Source = DataSources.DemoService,
            Category = "PumpMode", State = "Automatic", StartTimestamp = timestamp,
        });
        db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Source = "compression-low-detection",
            Category = "DataExclusion", State = "CompressionLow", StartTimestamp = timestamp,
            MetadataJson = "{\"DemoSeed\":true}",
        });
        db.CompressionLowSuggestions.Add(new CompressionLowSuggestionEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, StartMills = timestampMills - 60_000,
            EndMills = timestampMills + 60_000,
            NightOf = DateOnly.FromDateTime(timestamp), CreatedAt = 1,
            DataSource = DataSources.DemoService,
        });
        db.ApsSnapshots.Add(new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, DataSource = DataSources.DemoService,
            Device = "trio-rig", Timestamp = timestamp, AidAlgorithm = "Trio", DeletedAt = deletedAt,
        });
        db.SaveChanges();
    }

    private async Task<long> RemainingDemoRowsAsync()
    {
        await using var db = NewContext();
        return await db.SensorGlucose.IgnoreQueryFilters().LongCountAsync()
            + await db.MeterGlucose.IgnoreQueryFilters().LongCountAsync()
            + await db.Calibrations.IgnoreQueryFilters().LongCountAsync()
            + await db.Boluses.IgnoreQueryFilters().LongCountAsync()
            + await db.CarbIntakes.IgnoreQueryFilters().LongCountAsync()
            + await db.BGChecks.IgnoreQueryFilters().LongCountAsync()
            + await db.Notes.IgnoreQueryFilters().LongCountAsync()
            + await db.DeviceEvents.IgnoreQueryFilters().LongCountAsync()
            + await db.BolusCalculations.IgnoreQueryFilters().LongCountAsync()
            + await db.TempBasals.IgnoreQueryFilters().LongCountAsync()
            + await db.StateSpans.IgnoreQueryFilters().LongCountAsync()
            + await db.CompressionLowSuggestions.IgnoreQueryFilters().LongCountAsync()
            + await db.ApsSnapshots.IgnoreQueryFilters().LongCountAsync();
    }

    private async Task<long> TenantPurgeAsync()
    {
        var purge = PlatformAdminPurge();
        return await purge.PurgeTreatmentsAsync(Tenant, default) + await purge.PurgeEntriesAsync(Tenant, default);
    }

    private async Task<long> ScopePurgeTotalAsync()
    {
        var counts = await ScopePurge().PurgeAsync(default);
        return counts.Glucose + counts.Treatments + counts.DeviceStatus;
    }

    [Fact]
    public async Task BothPurgePaths_AccountForEveryDemoRowAndLeaveNothingBehind()
    {
        SeedOneDemoRowOfEveryType();
        var tenantDeleted = await TenantPurgeAsync();
        (await RemainingDemoRowsAsync()).Should().Be(0);

        SeedOneDemoRowOfEveryType();
        var scopeDeleted = await ScopePurgeTotalAsync();
        (await RemainingDemoRowsAsync()).Should().Be(0);

        tenantDeleted.Should().Be(SeededRows);
        scopeDeleted.Should().Be(SeededRows);
    }

    [Fact]
    public async Task BothPurgePaths_PurgeRowsAVisitorHadAlreadyDeleted()
    {
        SeedOneDemoRowOfEveryType(softDeleted: true);
        await TenantPurgeAsync();
        (await RemainingDemoRowsAsync()).Should().Be(0);

        SeedOneDemoRowOfEveryType(softDeleted: true);
        await ScopePurgeTotalAsync();
        (await RemainingDemoRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ScopePurge_CountsEachGroupSeparately()
    {
        SeedOneDemoRowOfEveryType();

        var counts = await ScopePurge().PurgeAsync(default);

        counts.Glucose.Should().Be(3);
        counts.Treatments.Should().Be(10);
        counts.DeviceStatus.Should().Be(1);
    }

    [Fact]
    public async Task ScopePurge_ReportsTheModelsOfEveryGroupItDeleted()
    {
        SeedOneDemoRowOfEveryType();

        await ScopePurge().PurgeAsync(default);

        _scopeReported.Should().Contain([
            typeof(SensorGlucose), typeof(MeterGlucose), typeof(Calibration),
            typeof(Bolus), typeof(CarbIntake), typeof(BGCheck), typeof(Note), typeof(DeviceEvent),
            typeof(BolusCalculation), typeof(TempBasal), typeof(StateSpan), typeof(ApsSnapshot),
        ]);
    }

    [Fact]
    public async Task ScopePurge_ReportsNothingWhenThereIsNothingToDelete()
    {
        await ScopePurge().PurgeAsync(default);

        _scopeReported.Should().BeEmpty();
    }

    [Fact]
    public async Task ScopePurge_ReportsTheGroupThatFailedPartWayAndNoGroupThatNeverStarted()
    {
        SeedOneDemoRowOfEveryType();

        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync("DROP TABLE lab_hba1c_results");
        var purge = () => ScopePurgeOver(ctx).PurgeAsync(default);

        await purge.Should().ThrowAsync<Exception>();

        _scopeReported.Should().Contain([typeof(Bolus), typeof(CarbIntake)])
            .And.NotContain([typeof(SensorGlucose), typeof(ApsSnapshot)]);
    }

    [Fact]
    public async Task PurgeEntries_ReportsTheGlucoseModelsAsTheDemoTenant()
    {
        SeedOneDemoRowOfEveryType();

        await PlatformAdminPurge().PurgeEntriesAsync(Tenant, default);

        var report = _tenantReports.Should().ContainSingle().Subject;
        report.Tenant.Should().Be(TenantId);
        report.Models.Should().BeEquivalentTo([typeof(SensorGlucose), typeof(MeterGlucose), typeof(Calibration)]);
    }

    [Fact]
    public async Task PurgeTreatments_ReportsTheTreatmentAndDeviceStatusModelsAsTheDemoTenant()
    {
        SeedOneDemoRowOfEveryType();

        await PlatformAdminPurge().PurgeTreatmentsAsync(Tenant, default);

        var report = _tenantReports.Should().ContainSingle().Subject;
        report.Tenant.Should().Be(TenantId);
        report.Models.Should().Contain([typeof(Bolus), typeof(CarbIntake), typeof(StateSpan), typeof(ApsSnapshot)])
            .And.NotContain(typeof(SensorGlucose));
    }

    [Fact]
    public async Task PurgeTreatments_ThatFailsPartWay_StillReportsTheGroupAndNoGroupThatNeverStarted()
    {
        SeedOneDemoRowOfEveryType();
        await using (var db = NewContext())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE lab_hba1c_results");

        var purge = () => PlatformAdminPurge().PurgeTreatmentsAsync(Tenant, default);

        await purge.Should().ThrowAsync<Exception>();

        var report = _tenantReports.Should().ContainSingle().Subject;
        report.Tenant.Should().Be(TenantId);
        report.Models.Should().Contain([typeof(Bolus), typeof(CarbIntake)])
            .And.NotContain([typeof(SensorGlucose), typeof(ApsSnapshot)]);
    }

    [Fact]
    public async Task ATenantPurgeThatDeletesNothing_ReportsNothing()
    {
        var purge = PlatformAdminPurge();

        await purge.PurgeEntriesAsync(Tenant, default);
        await purge.PurgeTreatmentsAsync(Tenant, default);

        _tenantReports.Should().BeEmpty();
    }

    [Fact]
    public async Task ScopePurge_PreservesRealReviewRowsAndDeletesSuggestionsOnDemoDevice()
    {
        var deviceId = Guid.CreateVersion7();
        var demoTime = DateTime.UtcNow;
        var demoMills = new DateTimeOffset(demoTime).ToUnixTimeMilliseconds();
        var demoSuggestion = Guid.CreateVersion7();
        var realSuggestion = Guid.CreateVersion7();
        await using (var db = NewContext())
        {
            db.PatientDevices.Add(new PatientDeviceEntity
            {
                Id = deviceId, TenantId = TenantId, DeviceCategory = "cgm",
                Manufacturer = "Dexcom", Model = "G7", SerialNumber = "demo-cgm",
            });
            db.SensorGlucose.Add(new SensorGlucoseEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, PatientDeviceId = deviceId,
                Timestamp = demoTime, Mgdl = 58,
            });
            db.CompressionLowSuggestions.AddRange(
                new CompressionLowSuggestionEntity
                {
                    Id = demoSuggestion, TenantId = TenantId, StartMills = demoMills - 60_000,
                    EndMills = demoMills + 60_000, NightOf = DateOnly.FromDateTime(demoTime), CreatedAt = 1,
                    DataSource = DataSources.DemoService,
                },
                new CompressionLowSuggestionEntity
                {
                    Id = realSuggestion, TenantId = TenantId, StartMills = demoMills + 3_600_000,
                    EndMills = demoMills + 3_660_000, NightOf = DateOnly.FromDateTime(demoTime), CreatedAt = 1,
                    DataSource = "manual",
                });
            db.StateSpans.Add(new StateSpanEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, Source = "compression-low-detection",
                Category = "DataExclusion", State = "CompressionLow", StartTimestamp = demoTime,
                MetadataJson = "{\"DemoSeed\":true,\"SuggestionId\":\"" + demoSuggestion + "\"}",
            });
            db.SaveChanges();
        }

        await ScopePurge().PurgeAsync(default);

        await using var verify = NewContext();
        (await verify.CompressionLowSuggestions.Select(s => s.Id).ToListAsync())
            .Should().ContainSingle().Which.Should().Be(realSuggestion);
        (await verify.StateSpans.CountAsync()).Should().Be(0);
    }

    private sealed class ScopeTenantAccessor : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;

        public bool IsResolved => Context is not null;

        public TenantContext? Context { get; private set; }

        public void SetTenant(TenantContext context) => Context = context;
    }

    private sealed class TenantRecordingLegacyReads(
        ITenantAccessor tenant, List<(Guid? Tenant, IReadOnlyCollection<Type> Models)> reports)
        : ILegacyReadCacheInvalidation
    {
        public Task WrittenAsync(IReadOnlyCollection<Type> models)
        {
            reports.Add((tenant.Context?.TenantId, models));
            return Task.CompletedTask;
        }
    }
}
