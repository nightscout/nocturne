using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning the V4 chokepoint's legacy-<c>entries</c> projection against real Postgres: glucose-family
/// writes (sgv/mbg/cal) project to the legacy <c>Entry</c> shape and fire <c>IDataEventSink&lt;Entry&gt;</c>,
/// gated to <see cref="WriteOrigin.Live"/> (backfill stays silent), with the projected <c>Entry.Id</c> equal
/// to the source <c>LegacyId</c>. Non-glucose types (no <c>ProjectToLegacyEntry</c> override) project nothing.
/// Asserts only the captured projections (via <see cref="V4GoldenFixture.EntryCapture"/>) plus persistence
/// where it proves silence didn't mean "no write".
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class EntriesProjectionGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public EntriesProjectionGoldenTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LiveSgvCreate_ProjectsEntriesCreate()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        _fx.EntryCapture.Clear();

        await repo.BulkCreateAsync(
            new[] { new SensorGlucose { Timestamp = T0, Mgdl = 120, DataSource = "dexcom", LegacyId = "ep-sgv-1" } },
            WriteOrigin.Live, CancellationToken.None);

        var ev = _fx.EntryCapture.Snapshot().Should().ContainSingle(e => e.Kind == "created").Which;
        var entry = ev.Entries.Should().ContainSingle().Which;
        entry.Type.Should().Be("sgv");
        entry.Id.Should().Be("ep-sgv-1");
    }

    [Fact]
    public async Task LiveMbgCreate_ProjectsEntriesCreate_TypeMbg()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IMeterGlucoseRepository>();
        _fx.EntryCapture.Clear();

        await repo.BulkCreateAsync(
            new[] { new MeterGlucose { Timestamp = T0, Mgdl = 95, DataSource = "manual", LegacyId = "ep-mbg-1" } },
            WriteOrigin.Live, CancellationToken.None);

        var ev = _fx.EntryCapture.Snapshot().Should().ContainSingle(e => e.Kind == "created").Which;
        var entry = ev.Entries.Should().ContainSingle().Which;
        entry.Type.Should().Be("mbg");
        entry.Id.Should().Be("ep-mbg-1");
    }

    [Fact]
    public async Task LiveCalCreate_ProjectsEntriesCreate_TypeCal()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ICalibrationRepository>();
        _fx.EntryCapture.Clear();

        await repo.BulkCreateAsync(
            new[] { new Calibration { Timestamp = T0, Slope = 828.3, Intercept = 32456.2, DataSource = "xdrip", LegacyId = "ep-cal-1" } },
            WriteOrigin.Live, CancellationToken.None);

        var ev = _fx.EntryCapture.Snapshot().Should().ContainSingle(e => e.Kind == "created").Which;
        var entry = ev.Entries.Should().ContainSingle().Which;
        entry.Type.Should().Be("cal");
        entry.IsCalibration.Should().BeTrue();
        entry.Id.Should().Be("ep-cal-1");
    }

    [Fact]
    public async Task BackfillSgvCreate_IsSilent_ButPersists()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        _fx.EntryCapture.Clear();

        await repo.BulkCreateAsync(
            new[] { new SensorGlucose { Timestamp = T0, Mgdl = 130, DataSource = "dexcom", LegacyId = "ep-bf-1" } },
            WriteOrigin.Backfill, CancellationToken.None);

        _fx.EntryCapture.Snapshot().Should().BeEmpty("backfill imports never project legacy entries");

        // Silence must not mean "didn't write" — the row is persisted.
        var rowCount = await _fx.QueryAsync(tenant, ctx =>
            ctx.SensorGlucose.AsNoTracking().Where(g => g.LegacyId == "ep-bf-1").CountAsync());
        rowCount.Should().Be(1);
    }

    [Fact]
    public async Task LiveSgvDeleteByLegacyId_ProjectsEntriesDelete()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        await repo.BulkCreateAsync(
            new[] { new SensorGlucose { Timestamp = T0, Mgdl = 140, DataSource = "dexcom", LegacyId = "ep-del-1" } },
            WriteOrigin.Live, CancellationToken.None);

        _fx.EntryCapture.Clear();
        await repo.DeleteByLegacyIdAsync("ep-del-1", WriteOrigin.Live, CancellationToken.None);

        var ev = _fx.EntryCapture.Snapshot().Should().ContainSingle(e => e.Kind == "deleted").Which;
        var entry = ev.Entries.Should().ContainSingle().Which;
        entry.Type.Should().Be("sgv");
        entry.Id.Should().Be("ep-del-1");
    }

    [Fact]
    public async Task LiveBolusCreate_ProjectsNoEntries()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        _fx.EntryCapture.Clear();

        await repo.BulkCreateAsync(
            new[] { new Bolus { Timestamp = T0, Insulin = 4.0, DataSource = "aaps", SyncIdentifier = "ep-bolus-1" } },
            WriteOrigin.Live, CancellationToken.None);

        _fx.EntryCapture.Snapshot().Should().BeEmpty("Bolus has no ProjectToLegacyEntry override");
    }

    [Fact]
    public async Task LiveSgvSingleCreate_ProjectsEntriesCreate()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        _fx.EntryCapture.Clear();

        await repo.CreateAsync(
            new SensorGlucose { Timestamp = T0, Mgdl = 150, DataSource = "dexcom", LegacyId = "ep-single-1" },
            WriteOrigin.Live, CancellationToken.None);

        var ev = _fx.EntryCapture.Snapshot().Should().ContainSingle(e => e.Kind == "created").Which;
        var entry = ev.Entries.Should().ContainSingle().Which;
        entry.Type.Should().Be("sgv");
        entry.Id.Should().Be("ep-single-1");
    }
}
