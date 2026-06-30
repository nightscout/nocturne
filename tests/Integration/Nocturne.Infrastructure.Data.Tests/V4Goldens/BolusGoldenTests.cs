using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Golden tests pinning the CURRENT dedup behaviour of <c>BolusRepository</c> (the hardest case:
/// SyncId-upsert + DeduplicationService + value-tolerance MatchCriteria). Held identical across the
/// V4RepositoryBase refactor; intentional deltas (D1–D7) are re-baselined explicitly.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class BolusGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public BolusGoldenTests(V4GoldenFixture fx) => _fx = fx;

    [Fact]
    public async Task BulkCreate_TwoSourcesWithinWindowAndTolerance_LinkIntoOneCanonicalGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();

        var t0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        await repo.BulkCreateAsync(
            new[]
            {
                new Bolus { Timestamp = t0, Insulin = 5.0, DataSource = "aaps", LegacyId = "a1" },
                new Bolus { Timestamp = t0.AddSeconds(10), Insulin = 5.02, DataSource = "loop", LegacyId = "b1" },
            },
            CancellationToken.None);

        // Dedup links rows into canonical groups; it never deletes — both rows persist physically.
        var rowCount = await _fx.QueryAsync(tenant, ctx => ctx.Boluses.AsNoTracking().CountAsync());
        rowCount.Should().Be(2);

        // Within ±30s and within the 0.05 U insulin tolerance, so the DeduplicationService groups
        // the two under a single canonical id with exactly one primary.
        var links = await _fx.QueryAsync(tenant, ctx =>
            ctx.LinkedRecords.AsNoTracking().Where(lr => lr.RecordType == "bolus").ToListAsync());

        links.Should().HaveCount(2);
        links.Select(l => l.CanonicalId).Distinct().Should()
            .HaveCount(1, "both boluses link into one canonical group");
        links.Count(l => l.IsPrimary).Should()
            .Be(1, "exactly one primary survives per canonical group");
    }

    [Fact]
    public async Task BulkCreate_SameSyncIdentifierTwice_UpsertsInPlace_NoDuplicateRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();

        var t0 = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc);
        await repo.BulkCreateAsync(
            new[] { new Bolus { Timestamp = t0, Insulin = 4.0, DataSource = "aaps", SyncIdentifier = "sync-1" } },
            CancellationToken.None);

        // Connector replay of the same (DataSource, SyncIdentifier) with a corrected dose upserts in
        // place rather than inserting a second row.
        await repo.BulkCreateAsync(
            new[] { new Bolus { Timestamp = t0, Insulin = 4.6, DataSource = "aaps", SyncIdentifier = "sync-1" } },
            CancellationToken.None);

        var rows = await _fx.QueryAsync(tenant, ctx =>
            ctx.Boluses.AsNoTracking().Where(b => b.DataSource == "aaps").ToListAsync());

        rows.Should().HaveCount(1, "the SyncIdentifier match updates in place");
        rows[0].Insulin.Should().Be(4.6, "the replayed dose overwrites the original");
    }

    [Fact]
    public async Task GetAsync_SevenArg_ViaGenericInterface_ExcludesNonPrimaryDuplicates()
    {
        // Pins the migration's highest-risk path: the base's plain 7-arg GetAsync is overridden to
        // route through the bolus query's non-primary LinkedRecords filter, so a deduped non-primary
        // row is excluded even when GetAsync is invoked via the generic IV4Repository<Bolus> (the old
        // default-interface bridge). If the override regressed, this returns 2 instead of 1.
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();

        var t0 = new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc);
        await repo.BulkCreateAsync(
            new[]
            {
                new Bolus { Timestamp = t0, Insulin = 5.0, DataSource = "aaps", LegacyId = "p-a" },
                new Bolus { Timestamp = t0.AddSeconds(10), Insulin = 5.02, DataSource = "loop", LegacyId = "p-b" },
            },
            CancellationToken.None);

        // Two physical rows, linked into one canonical group (one primary, one non-primary).
        var physical = await _fx.QueryAsync(tenant, ctx => ctx.Boluses.AsNoTracking().CountAsync());
        physical.Should().Be(2);

        var v4 = (IV4Repository<Bolus>)repo;
        var visible = await v4.GetAsync(null, null, null, null, 100, 0, true, CancellationToken.None);

        visible.Should().HaveCount(1, "the 7-arg GetAsync excludes the non-primary deduped row");
    }
}
