using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning the V4 repository chokepoint's broadcast firing behaviour against real Postgres: the
/// origin gate (Live broadcasts, Backfill stays silent), inserts vs material upserts, the
/// no-material-change silence on identical re-upload, and delete-by-id fan-out. Asserts only the
/// captured broadcasts (via <see cref="V4GoldenFixture.Capture"/>) plus persistence where it proves
/// silence didn't mean "no write".
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class BroadcastGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public BroadcastGoldenTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LiveBulkCreate_BroadcastsCreated()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        _fx.Capture.Clear();

        await repo.BulkCreateAsync(
            new[]
            {
                new SensorGlucose { Timestamp = T0, Mgdl = 120, DataSource = "dexcom", LegacyId = "bc-a" },
                new SensorGlucose { Timestamp = T0.AddMinutes(5), Mgdl = 122, DataSource = "dexcom", LegacyId = "bc-b" },
            }, WriteOrigin.Live, CancellationToken.None);

        var entries = _fx.Capture.Snapshot();
        entries.Should().ContainSingle(e => e.Kind == "created" && e.ModelType == typeof(SensorGlucose))
            .Which.Count.Should().Be(2, "both inserted rows broadcast as created");
        entries.Should().NotContain(e => e.Kind == "updated");
    }

    [Fact]
    public async Task BackfillBulkCreate_IsSilent_ButPersists()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        _fx.Capture.Clear();

        await repo.BulkCreateAsync(
            new[]
            {
                new SensorGlucose { Timestamp = T0, Mgdl = 130, DataSource = "dexcom", LegacyId = "bf-a" },
                new SensorGlucose { Timestamp = T0.AddMinutes(5), Mgdl = 131, DataSource = "dexcom", LegacyId = "bf-b" },
            }, WriteOrigin.Backfill, CancellationToken.None);

        _fx.Capture.Snapshot().Should().BeEmpty("backfill imports never broadcast");

        // Silence must not mean "didn't write" — the rows are persisted.
        var rowCount = await _fx.QueryAsync(tenant, ctx =>
            ctx.SensorGlucose.AsNoTracking().Where(g => g.LegacyId == "bf-a" || g.LegacyId == "bf-b").CountAsync());
        rowCount.Should().Be(2);
    }

    [Fact]
    public async Task MaterialUpsert_BroadcastsUpdated_IdenticalReuploadDoesNot()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();

        await repo.BulkCreateAsync(
            new[] { new Bolus { Timestamp = T0, Insulin = 4.0, DataSource = "aaps", SyncIdentifier = "u-1" } },
            WriteOrigin.Live, CancellationToken.None);

        // Live re-upload of the SAME sync id with a CHANGED dose upserts in place → broadcasts updated.
        _fx.Capture.Clear();
        await repo.BulkCreateAsync(
            new[] { new Bolus { Timestamp = T0, Insulin = 4.6, DataSource = "aaps", SyncIdentifier = "u-1" } },
            WriteOrigin.Live, CancellationToken.None);

        var afterChange = _fx.Capture.Snapshot();
        afterChange.Should().ContainSingle(e => e.Kind == "updated" && e.ModelType == typeof(Bolus))
            .Which.Count.Should().Be(1);
        afterChange.Should().NotContain(e => e.Kind == "created");

        // Live re-upload of the SAME sync id with IDENTICAL fields → no material change → silent.
        _fx.Capture.Clear();
        await repo.BulkCreateAsync(
            new[] { new Bolus { Timestamp = T0, Insulin = 4.6, DataSource = "aaps", SyncIdentifier = "u-1" } },
            WriteOrigin.Live, CancellationToken.None);

        var afterIdentical = _fx.Capture.Snapshot();
        afterIdentical.Should().NotContain(e => e.Kind == "updated", "an identical re-upload changes nothing");
        afterIdentical.Should().NotContain(e => e.Kind == "created");
    }

    [Fact]
    public async Task DeleteByLegacyId_BroadcastsDeletedIds()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        await repo.BulkCreateAsync(
            new[] { new SensorGlucose { Timestamp = T0, Mgdl = 140, DataSource = "dexcom", LegacyId = "del-1" } },
            WriteOrigin.Live, CancellationToken.None);

        var rowId = await _fx.QueryAsync(tenant, ctx =>
            ctx.SensorGlucose.AsNoTracking().Where(g => g.LegacyId == "del-1").Select(g => g.Id).FirstAsync());

        _fx.Capture.Clear();
        await repo.DeleteByLegacyIdAsync("del-1", WriteOrigin.Live, CancellationToken.None);

        var entry = _fx.Capture.Snapshot()
            .Should().ContainSingle(e => e.Kind == "deleted" && e.ModelType == typeof(SensorGlucose)).Which;
        entry.Ids.Should().Contain(rowId);
    }

    [Fact]
    public async Task LiveSingleCreate_BroadcastsCreated()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        _fx.Capture.Clear();

        await repo.CreateAsync(
            new SensorGlucose { Timestamp = T0, Mgdl = 150, DataSource = "dexcom", LegacyId = "single-1" },
            WriteOrigin.Live, CancellationToken.None);

        _fx.Capture.Snapshot()
            .Should().ContainSingle(e => e.Kind == "created" && e.ModelType == typeof(SensorGlucose))
            .Which.Count.Should().Be(1);
    }
}
