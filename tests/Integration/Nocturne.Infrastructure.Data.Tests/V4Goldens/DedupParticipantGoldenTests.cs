using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning the CURRENT cross-connector dedup behaviour of the remaining
/// DeduplicationService participants (Bolus is covered in <see cref="BolusGoldenTests"/>). Each type
/// links two within-window records that match its per-type MatchCriteria into a single canonical
/// group; the SyncId-upsert types also upsert in place on replay. Held identical across the
/// V4RepositoryBase refactor; intentional deltas (D1–D7) are re-baselined explicitly.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class DedupParticipantGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public DedupParticipantGoldenTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);

    // Two records within ±30s that match criteria must collapse to one canonical group (1 primary),
    // while both physical rows persist (dedup links, never deletes).
    private async Task AssertOneCanonicalGroupAsync(
        Guid tenant, int physicalRows, string recordType, Func<NocturneDbContext, Task<int>> countRows)
    {
        (await _fx.QueryAsync(tenant, countRows)).Should().Be(physicalRows, "dedup links rows, it never deletes them");

        var links = await _fx.QueryAsync(tenant, ctx =>
            ctx.LinkedRecords.AsNoTracking().Where(lr => lr.RecordType == recordType).ToListAsync());
        // The distinct-canonical + single-primary pair is the real grouping guard (a count alone would
        // still be `physicalRows` if dedup split them into separate groups); the count only catches
        // dedup not running at all.
        links.Should().HaveCount(physicalRows);
        links.Select(l => l.CanonicalId).Distinct().Should().HaveCount(1, "matching records link into one canonical group");
        links.Count(l => l.IsPrimary).Should().Be(1, "exactly one primary per canonical group");
    }

    [Fact]
    public async Task SensorGlucose_WithinWindowAndTolerance_LinksIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        await repo.BulkCreateAsync(
            new[]
            {
                new SensorGlucose { Timestamp = T0, Mgdl = 120, DataSource = "dexcom", LegacyId = "g-a" },
                new SensorGlucose { Timestamp = T0.AddSeconds(10), Mgdl = 120.5, DataSource = "libre", LegacyId = "g-b" },
            }, WriteOrigin.Live,
            CancellationToken.None);

        await AssertOneCanonicalGroupAsync(tenant, 2, "sensorglucose", ctx => ctx.SensorGlucose.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task SensorGlucose_SameSyncIdentifierTwice_UpsertsInPlace()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        await repo.BulkCreateAsync(new[] { new SensorGlucose { Timestamp = T0, Mgdl = 100, DataSource = "dexcom", SyncIdentifier = "s-1" } }, WriteOrigin.Live, CancellationToken.None);
        await repo.BulkCreateAsync(new[] { new SensorGlucose { Timestamp = T0, Mgdl = 142, DataSource = "dexcom", SyncIdentifier = "s-1" } }, WriteOrigin.Live, CancellationToken.None);

        var rows = await _fx.QueryAsync(tenant, ctx => ctx.SensorGlucose.AsNoTracking().Where(g => g.DataSource == "dexcom").ToListAsync());
        rows.Should().HaveCount(1);
        rows[0].Mgdl.Should().Be(142);
    }

    [Fact]
    public async Task CarbIntake_WithinWindowAndTolerance_LinksIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ICarbIntakeRepository>();

        await repo.BulkCreateAsync(
            new[]
            {
                new CarbIntake { Timestamp = T0, Carbs = 30, DataSource = "aaps", LegacyId = "c-a" },
                new CarbIntake { Timestamp = T0.AddSeconds(10), Carbs = 30.5, DataSource = "loop", LegacyId = "c-b" },
            }, WriteOrigin.Live,
            CancellationToken.None);

        await AssertOneCanonicalGroupAsync(tenant, 2, "carbintake", ctx => ctx.CarbIntakes.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CarbIntake_SameSyncIdentifierTwice_UpsertsInPlace()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ICarbIntakeRepository>();

        await repo.BulkCreateAsync(new[] { new CarbIntake { Timestamp = T0, Carbs = 20, DataSource = "aaps", SyncIdentifier = "c-1" } }, WriteOrigin.Live, CancellationToken.None);
        await repo.BulkCreateAsync(new[] { new CarbIntake { Timestamp = T0, Carbs = 45, DataSource = "aaps", SyncIdentifier = "c-1" } }, WriteOrigin.Live, CancellationToken.None);

        var rows = await _fx.QueryAsync(tenant, ctx => ctx.CarbIntakes.AsNoTracking().Where(c => c.DataSource == "aaps").ToListAsync());
        rows.Should().HaveCount(1);
        rows[0].Carbs.Should().Be(45);
    }

    [Fact]
    public async Task BGCheck_WithinWindowAndTolerance_LinksIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBGCheckRepository>();

        await repo.BulkCreateAsync(
            new[]
            {
                new BGCheck { Timestamp = T0, Glucose = 95, DataSource = "manual", LegacyId = "b-a" },
                new BGCheck { Timestamp = T0.AddSeconds(10), Glucose = 95.5, DataSource = "meter", LegacyId = "b-b" },
            }, WriteOrigin.Live,
            CancellationToken.None);

        await AssertOneCanonicalGroupAsync(tenant, 2, "bgcheck", ctx => ctx.BGChecks.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task DeviceEvent_SameEventTypeWithinWindow_LinksIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IDeviceEventRepository>();

        await repo.BulkCreateAsync(
            new[]
            {
                new DeviceEvent { Timestamp = T0, EventType = DeviceEventType.SiteChange, DataSource = "aaps", LegacyId = "d-a" },
                new DeviceEvent { Timestamp = T0.AddSeconds(10), EventType = DeviceEventType.SiteChange, DataSource = "loop", LegacyId = "d-b" },
            }, WriteOrigin.Live,
            CancellationToken.None);

        await AssertOneCanonicalGroupAsync(tenant, 2, "deviceevent", ctx => ctx.DeviceEvents.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Note_WithinWindow_LinksIntoOneGroup_OnTimeAlone()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<INoteRepository>();

        // Note dedup uses an empty MatchCriteria — records match on the ±30s time window + source alone.
        await repo.BulkCreateAsync(
            new[]
            {
                new Note { Timestamp = T0, Text = "exercise", DataSource = "aaps", LegacyId = "n-a" },
                new Note { Timestamp = T0.AddSeconds(10), Text = "walk", DataSource = "loop", LegacyId = "n-b" },
            }, WriteOrigin.Live,
            CancellationToken.None);

        await AssertOneCanonicalGroupAsync(tenant, 2, "note", ctx => ctx.Notes.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task BolusCalculation_WithinWindowAndTolerance_LinksIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusCalculationRepository>();

        await repo.BulkCreateAsync(
            new[]
            {
                new BolusCalculation { Timestamp = T0, CarbInput = 40, DataSource = "aaps", LegacyId = "bc-a" },
                new BolusCalculation { Timestamp = T0.AddSeconds(10), CarbInput = 40.5, DataSource = "loop", LegacyId = "bc-b" },
            }, WriteOrigin.Live,
            CancellationToken.None);

        await AssertOneCanonicalGroupAsync(tenant, 2, "boluscalculation", ctx => ctx.BolusCalculations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task TempBasal_WithinWindowAndRateTolerance_LinksIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ITempBasalRepository>();

        // TempBasal dedups on Rate (±0.05) at StartTimestamp.
        await repo.BulkCreateAsync(
            new[]
            {
                new TempBasal { StartTimestamp = T0, Rate = 0.80, Origin = TempBasalOrigin.Algorithm, DataSource = "aaps", LegacyId = "tb-a" },
                new TempBasal { StartTimestamp = T0.AddSeconds(10), Rate = 0.82, Origin = TempBasalOrigin.Algorithm, DataSource = "loop", LegacyId = "tb-b" },
            }, WriteOrigin.Live,
            CancellationToken.None);

        await AssertOneCanonicalGroupAsync(tenant, 2, "tempbasal", ctx => ctx.TempBasals.AsNoTracking().CountAsync());
    }
}
