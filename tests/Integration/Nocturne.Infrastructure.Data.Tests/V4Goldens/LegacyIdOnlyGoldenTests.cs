using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning the CURRENT behaviour of the LegacyId-only V4 repositories — the ones that do NOT
/// participate in cross-connector canonical dedup. Each: intra-batch LegacyId dedup collapses a
/// duplicate, and (the classification guard) NO canonical links are created. If the V4RepositoryBase
/// refactor accidentally wires one of these onto the DeduplicationService axis, the zero-links
/// assertion fails. Held identical across the refactor.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class LegacyIdOnlyGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public LegacyIdOnlyGoldenTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private async Task AssertLegacyIdDedupAndNoLinksAsync(Guid tenant, Func<NocturneDbContext, Task<int>> countRows)
    {
        // Two distinct LegacyIds + one intra-batch duplicate of the first → the duplicate collapses.
        (await _fx.QueryAsync(tenant, countRows)).Should().Be(2, "intra-batch LegacyId dedup keeps one row per LegacyId");
        // These types are not DeduplicationService participants — they never create canonical links.
        (await _fx.QueryAsync(tenant, ctx => ctx.LinkedRecords.AsNoTracking().CountAsync()))
            .Should().Be(0, "LegacyId-only repositories must not create canonical links");
    }

    [Fact]
    public async Task MeterGlucose_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IMeterGlucoseRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new MeterGlucose { Timestamp = T0, Mgdl = 100, DataSource = "aaps", LegacyId = "m-1" },
            new MeterGlucose { Timestamp = T0.AddSeconds(5), Mgdl = 101, DataSource = "loop", LegacyId = "m-2" },
            new MeterGlucose { Timestamp = T0, Mgdl = 100, DataSource = "aaps", LegacyId = "m-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.MeterGlucose.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Calibration_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ICalibrationRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new Calibration { Timestamp = T0, DataSource = "dexcom", LegacyId = "cal-1" },
            new Calibration { Timestamp = T0.AddSeconds(5), DataSource = "dexcom", LegacyId = "cal-2" },
            new Calibration { Timestamp = T0, DataSource = "dexcom", LegacyId = "cal-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.Calibrations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ApsSnapshot_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new ApsSnapshot { Timestamp = T0, DataSource = "loop", LegacyId = "aps-1" },
            new ApsSnapshot { Timestamp = T0.AddSeconds(5), DataSource = "loop", LegacyId = "aps-2" },
            new ApsSnapshot { Timestamp = T0, DataSource = "loop", LegacyId = "aps-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.ApsSnapshots.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task PumpSnapshot_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IPumpSnapshotRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new PumpSnapshot { Timestamp = T0, DataSource = "loop", LegacyId = "pump-1" },
            new PumpSnapshot { Timestamp = T0.AddSeconds(5), DataSource = "loop", LegacyId = "pump-2" },
            new PumpSnapshot { Timestamp = T0, DataSource = "loop", LegacyId = "pump-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.PumpSnapshots.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task UploaderSnapshot_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IUploaderSnapshotRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new UploaderSnapshot { Timestamp = T0, DataSource = "xdrip", LegacyId = "up-1" },
            new UploaderSnapshot { Timestamp = T0.AddSeconds(5), DataSource = "xdrip", LegacyId = "up-2" },
            new UploaderSnapshot { Timestamp = T0, DataSource = "xdrip", LegacyId = "up-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.UploaderSnapshots.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task BasalSchedule_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBasalScheduleRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new BasalSchedule { Timestamp = T0, DataSource = "aaps", LegacyId = "bs-1" },
            new BasalSchedule { Timestamp = T0.AddSeconds(5), DataSource = "aaps", LegacyId = "bs-2" },
            new BasalSchedule { Timestamp = T0, DataSource = "aaps", LegacyId = "bs-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.BasalSchedules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CarbRatioSchedule_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ICarbRatioScheduleRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new CarbRatioSchedule { Timestamp = T0, DataSource = "aaps", LegacyId = "cr-1" },
            new CarbRatioSchedule { Timestamp = T0.AddSeconds(5), DataSource = "aaps", LegacyId = "cr-2" },
            new CarbRatioSchedule { Timestamp = T0, DataSource = "aaps", LegacyId = "cr-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.CarbRatioSchedules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task SensitivitySchedule_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensitivityScheduleRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new SensitivitySchedule { Timestamp = T0, DataSource = "aaps", LegacyId = "ss-1" },
            new SensitivitySchedule { Timestamp = T0.AddSeconds(5), DataSource = "aaps", LegacyId = "ss-2" },
            new SensitivitySchedule { Timestamp = T0, DataSource = "aaps", LegacyId = "ss-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.SensitivitySchedules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task TargetRangeSchedule_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ITargetRangeScheduleRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new TargetRangeSchedule { Timestamp = T0, DataSource = "aaps", LegacyId = "tr-1" },
            new TargetRangeSchedule { Timestamp = T0.AddSeconds(5), DataSource = "aaps", LegacyId = "tr-2" },
            new TargetRangeSchedule { Timestamp = T0, DataSource = "aaps", LegacyId = "tr-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.TargetRangeSchedules.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task TherapySettings_LegacyIdDedup_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ITherapySettingsRepository>();
        await repo.BulkCreateAsync(new[]
        {
            new TherapySettings { Timestamp = T0, DataSource = "aaps", LegacyId = "th-1" },
            new TherapySettings { Timestamp = T0.AddSeconds(5), DataSource = "aaps", LegacyId = "th-2" },
            new TherapySettings { Timestamp = T0, DataSource = "aaps", LegacyId = "th-1" },
        }, WriteOrigin.Live, CancellationToken.None);
        await AssertLegacyIdDedupAndNoLinksAsync(tenant, ctx => ctx.TherapySettings.AsNoTracking().CountAsync());
    }
}
