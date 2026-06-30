using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Goldens pinning the CURRENT behaviour of the single-vs-bulk SyncId-upsert inconsistencies the
/// V4RepositoryBase refactor is expected to NORMALIZE (deltas D1, D3, D7). Capturing them now means
/// each lands in PR-C as a deliberate, visible re-baseline of a named test — not a silent change.
/// These assertions encode today's behaviour, INCLUDING the latent bugs.
///
/// Note: every V4 SyncId-upsert type carries a partial unique index on
/// (tenant_id, data_source, sync_identifier). So a path that fails to upsert on a duplicate
/// SyncIdentifier does not insert a second row — it throws a unique-violation. The asymmetry is:
///   - BasalInjection: single CreateAsync upserts; BULK now upserts too after the D1 fix (was: bulk
///     threw on a duplicate SyncIdentifier).
///   - SensorGlucose:  bulk upserts; single CreateAsync does not → single throws (D3).
///   - Bolus:          both upsert (the consistent reference).
/// D2 pins the new-insert-plus-upserted-sibling collapse for SensorGlucose and Bolus. After delta D4
/// moved Bolus's dedup to run post-commit, BOTH now collapse C with the upserted B into ONE canonical
/// group (the DeduplicationService runs on a separate connection and sees B's upserted physical value
/// only once committed):
///   D2_SensorGlucose_PostCommitDedup_NewPlusUpsertedSibling_CollapseIntoOneGroup     → ONE canonical group.
///   D2_Bolus_PostCommitDedup_NewPlusUpsertedSibling_CollapseIntoOneGroup       → ONE canonical group.
/// Both run the same scenario byte-for-byte: seed a SyncId-keyed row B in its own group, then a
/// second BulkCreateAsync carrying a fresh insert C (no SyncId) at B's time+value AND a SyncId-upsert
/// of B onto that same time+value. B's linked_records.SourceTimestamp stays at T0 (the upsert never
/// refreshes it), inside C's ±30s window, so B is always a candidate; the collapse turns on the dedup
/// engine value-matching against B's COMMITTED upserted row. (Pre-D4, Bolus ran dedup inside the open
/// transaction, so its separate dedup connection saw B's OLD value and the two stayed in separate
/// groups — that "inserts-only feed" framing conflated the dedup input list with this commit-
/// visibility effect; the live driver is the latter.)
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class DedupDeltaGoldenTests
{
    private readonly V4GoldenFixture _fx;

    public DedupDeltaGoldenTests(V4GoldenFixture fx) => _fx = fx;

    private static readonly DateTime T0 = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static BasalInjection Bi(double units, string source, string? legacyId = null, string? syncId = null) =>
        new()
        {
            Timestamp = T0,
            Units = units,
            DataSource = source,
            LegacyId = legacyId,
            SyncIdentifier = syncId,
            InsulinContext = new TreatmentInsulinContext { InsulinName = "test" },
        };

    // ── D1: BasalInjection — single CreateAsync upserts on SyncId, but BULK does not ──────────────

    [Fact]
    public async Task D1_BasalInjection_Bulk_IsLegacyIdOnly_NoCanonicalLinks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBasalInjectionRepository>();

        await repo.BulkCreateAsync(new[]
        {
            Bi(10, "aaps", legacyId: "bi-1"),
            Bi(11, "loop", legacyId: "bi-2"),
            Bi(10, "aaps", legacyId: "bi-1"),
        }, CancellationToken.None);

        (await _fx.QueryAsync(tenant, ctx => ctx.BasalInjections.AsNoTracking().CountAsync()))
            .Should().Be(2, "intra-batch LegacyId dedup collapses the duplicate");
        (await _fx.QueryAsync(tenant, ctx => ctx.LinkedRecords.AsNoTracking().CountAsync()))
            .Should().Be(0, "BasalInjection is not a DeduplicationService participant");
    }

    [Fact]
    public async Task D1_BasalInjection_Bulk_UpsertsOnSyncId_LatestWins()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBasalInjectionRepository>();

        // Same (DataSource, SyncIdentifier), distinct LegacyIds so both pass LegacyId dedup. After the
        // D1 fix the bulk path upserts on SyncId (intra-batch keep-last + DB-level upsert), so the two
        // collapse to a single row with the latest value — matching the single CreateAsync path and
        // never hitting the partial unique index. (Pre-D1 baseline: this threw a DbUpdateException.)
        await repo.BulkCreateAsync(new[]
        {
            Bi(10, "aaps", legacyId: "bi-a", syncId: "bi-sync"),
            Bi(14, "aaps", legacyId: "bi-b", syncId: "bi-sync"),
        }, CancellationToken.None);

        var rows = await _fx.QueryAsync(tenant, ctx => ctx.BasalInjections.AsNoTracking().ToListAsync());
        rows.Should().HaveCount(1, "bulk now upserts on SyncIdentifier (D1 fix)");
        rows[0].Units.Should().Be(14, "intra-batch keep-last makes the latest value win");
    }

    [Fact]
    public async Task D1_BasalInjection_SingleCreate_UpsertsOnSyncId()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBasalInjectionRepository>();

        await repo.CreateAsync(Bi(10, "aaps", syncId: "bi-sync"), CancellationToken.None);
        await repo.CreateAsync(Bi(14, "aaps", syncId: "bi-sync"), CancellationToken.None);

        var rows = await _fx.QueryAsync(tenant, ctx => ctx.BasalInjections.AsNoTracking().ToListAsync());
        rows.Should().HaveCount(1, "single CreateAsync upserts on SyncIdentifier");
        rows[0].Units.Should().Be(14);
    }

    // ── D3: SensorGlucose single CreateAsync does NOT upsert (bulk does); Bolus single DOES ───────

    [Fact]
    public async Task D3_SensorGlucose_SingleCreate_UpsertsOnSyncId_LatestWins()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        await repo.CreateAsync(new SensorGlucose { Timestamp = T0, Mgdl = 100, DataSource = "dexcom", SyncIdentifier = "sg-s" }, CancellationToken.None);
        await repo.CreateAsync(new SensorGlucose { Timestamp = T0, Mgdl = 142, DataSource = "dexcom", SyncIdentifier = "sg-s" }, CancellationToken.None);

        // D3 re-baseline: SensorGlucose single CreateAsync now upserts on (DataSource, SyncIdentifier)
        // in place (mirroring Bolus/CarbIntake), so the duplicate SyncId updates the existing row
        // instead of hitting the unique index — one row, latest value wins.
        var rows = await _fx.QueryAsync(tenant, ctx => ctx.SensorGlucose.AsNoTracking().ToListAsync());
        rows.Should().HaveCount(1, "SensorGlucose single CreateAsync upserts on SyncId (D3)");
        rows[0].Mgdl.Should().Be(142);
    }

    [Fact]
    public async Task D3_Bolus_SingleCreate_UpsertsOnSyncId_OneRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();

        await repo.CreateAsync(new Bolus { Timestamp = T0, Insulin = 4.0, DataSource = "aaps", SyncIdentifier = "b-s" }, CancellationToken.None);
        await repo.CreateAsync(new Bolus { Timestamp = T0, Insulin = 4.6, DataSource = "aaps", SyncIdentifier = "b-s" }, CancellationToken.None);

        var rows = await _fx.QueryAsync(tenant, ctx => ctx.Boluses.AsNoTracking().ToListAsync());
        rows.Should().HaveCount(1, "Bolus single CreateAsync upserts on SyncId (the consistent reference)");
        rows[0].Insulin.Should().Be(4.6);
    }

    // ── D7: only CarbIntake.CountAsync excludes non-primary links; Bolus/SensorGlucose over-count ──

    [Fact]
    public async Task D7_AllDedupParticipants_CountExcludesNonPrimary()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);

        var carb = scope.ServiceProvider.GetRequiredService<ICarbIntakeRepository>();
        await carb.BulkCreateAsync(new[]
        {
            new CarbIntake { Timestamp = T0, Carbs = 30, DataSource = "aaps", LegacyId = "c-a" },
            new CarbIntake { Timestamp = T0.AddSeconds(10), Carbs = 30.5, DataSource = "loop", LegacyId = "c-b" },
        }, CancellationToken.None);

        var bolus = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        await bolus.BulkCreateAsync(new[]
        {
            new Bolus { Timestamp = T0, Insulin = 5.0, DataSource = "aaps", LegacyId = "b-a" },
            new Bolus { Timestamp = T0.AddSeconds(10), Insulin = 5.02, DataSource = "loop", LegacyId = "b-b" },
        }, CancellationToken.None);

        var sg = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        await sg.BulkCreateAsync(new[]
        {
            new SensorGlucose { Timestamp = T0, Mgdl = 120, DataSource = "dexcom", LegacyId = "g-a" },
            new SensorGlucose { Timestamp = T0.AddSeconds(10), Mgdl = 120.5, DataSource = "libre", LegacyId = "g-b" },
        }, CancellationToken.None);

        // D7 re-baseline: each pair links into one canonical group, and ALL dedup participants now
        // exclude the non-primary via the base CountAsync + ApplyReadVisibility hook (was: only
        // CarbIntake excluded; Bolus and SensorGlucose over-counted at 2).
        (await carb.CountAsync(null, null)).Should().Be(1, "CarbIntake.CountAsync excludes non-primary (D7)");
        (await bolus.CountAsync(null, null)).Should().Be(1, "Bolus.CountAsync now excludes non-primary (D7)");
        (await sg.CountAsync(null, null)).Should().Be(1, "SensorGlucose.CountAsync now excludes non-primary (D7)");
    }

    // ── D2: new-insert-plus-upserted-sibling collapse ──────────────────────────────────────────────
    //
    // The scenario: a batch carrying a fresh insert C plus a SyncId-upsert of an existing row B onto
    // C's time+value. B's linked_records.SourceTimestamp stays at T0 (the upsert never refreshes it),
    // which is inside C's ±30s window — so B is always a candidate for C. Whether C collapses with B
    // turns on whether the dedup engine, when value-matching, sees B's UPSERTED physical value.
    //
    // The DeduplicationService runs on its own scoped NocturneDbContext (a separate connection from
    // the repository's BulkCreate transaction). So it sees B's upserted physical row only once that
    // row is COMMITTED:
    //   - SensorGlucose has always committed before running dedup → B's new value is visible →
    //     C collapses with B into ONE group.
    //   - Bolus, AFTER delta D4 moved dedup to run post-commit, now does the same → ONE group.
    //     (Before D4, Bolus ran dedup inside the still-open transaction, so the separate dedup
    //     connection saw B's OLD value, C did not value-match, and the two stayed in SEPARATE groups.
    //     The earlier "inserts-only feed" framing of this delta conflated the dedup input list with
    //     this commit-visibility effect; the live driver is the latter.)
    //
    // Each scenario runs as two BulkCreateAsync calls under one tenant:
    //   1. Seed B at T0 (its own canonical group), keyed by (DataSource, SyncIdentifier).
    //   2. Batch = [ C: fresh insert at T0+10s with B's value; B: SyncId-upsert moved to T0+10s ].

    [Fact]
    public async Task D2_SensorGlucose_PostCommitDedup_NewPlusUpsertedSibling_CollapseIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        // 1. Seed B (SyncId-keyed) — its own canonical group.
        await repo.BulkCreateAsync(new[]
        {
            new SensorGlucose { Timestamp = T0, Mgdl = 100, DataSource = "dexcom", SyncIdentifier = "sg-B" },
        }, CancellationToken.None);

        // 2. C is a fresh insert at T0+10s; B is SyncId-upserted onto T0+10s with C's value.
        await repo.BulkCreateAsync(new[]
        {
            new SensorGlucose { Timestamp = T0.AddSeconds(10), Mgdl = 120, DataSource = "libre", LegacyId = "sg-C" },
            new SensorGlucose { Timestamp = T0.AddSeconds(10), Mgdl = 120, DataSource = "dexcom", SyncIdentifier = "sg-B" },
        }, CancellationToken.None);

        (await _fx.QueryAsync(tenant, ctx => ctx.SensorGlucose.AsNoTracking().CountAsync()))
            .Should().Be(2, "B upserts in place; C inserts — two physical rows");

        var links = await _fx.QueryAsync(tenant, ctx =>
            ctx.LinkedRecords.AsNoTracking().Where(lr => lr.RecordType == "sensorglucose").ToListAsync());
        links.Select(l => l.CanonicalId).Distinct().Should()
            .HaveCount(1, "C links to B via B's persisted linked_records row — one group");
    }

    [Fact]
    public async Task D2_Bolus_PostCommitDedup_NewPlusUpsertedSibling_CollapseIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();

        // 1. Seed B (SyncId-keyed) — its own canonical group.
        await repo.BulkCreateAsync(new[]
        {
            new Bolus { Timestamp = T0, Insulin = 4.0, DataSource = "aaps", SyncIdentifier = "b-B" },
        }, CancellationToken.None);

        // 2. C is a fresh insert at T0+10s; B is SyncId-upserted onto T0+10s with C's value — byte-for
        //    byte the SensorGlucose scenario above. After D4 moved Bolus's dedup to run post-commit,
        //    B's upserted value is committed and therefore visible to the dedup engine's separate
        //    connection, so C value-matches B and they collapse into ONE canonical group — matching
        //    SensorGlucose.
        await repo.BulkCreateAsync(new[]
        {
            new Bolus { Timestamp = T0.AddSeconds(10), Insulin = 5.0, DataSource = "loop", LegacyId = "b-C" },
            new Bolus { Timestamp = T0.AddSeconds(10), Insulin = 5.0, DataSource = "aaps", SyncIdentifier = "b-B" },
        }, CancellationToken.None);

        (await _fx.QueryAsync(tenant, ctx => ctx.Boluses.AsNoTracking().CountAsync()))
            .Should().Be(2, "B upserts in place; C inserts — two physical rows");

        var links = await _fx.QueryAsync(tenant, ctx =>
            ctx.LinkedRecords.AsNoTracking().Where(lr => lr.RecordType == "bolus").ToListAsync());
        // D4 re-baseline (was 2 groups): dedup now runs after commit, so C sees B's committed upserted
        // value and links to it — one group.
        links.Select(l => l.CanonicalId).Distinct().Should()
            .HaveCount(1, "post-commit dedup sees B's upserted value, so C collapses with B into one group (D4)");
    }

    [Fact]
    public async Task D2_CarbIntake_PostCommitDedup_NewPlusUpsertedSibling_CollapseIntoOneGroup()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ICarbIntakeRepository>();

        // 1. Seed B (SyncId-keyed) — its own canonical group.
        await repo.BulkCreateAsync(new[]
        {
            new CarbIntake { Timestamp = T0, Carbs = 30, DataSource = "aaps", SyncIdentifier = "c-B" },
        }, CancellationToken.None);

        // 2. C is a fresh insert at T0+10s; B is SyncId-upserted onto T0+10s with C's value — byte-for
        //    byte the Bolus/SensorGlucose scenarios above. CarbIntake had the same D4 post-commit flip
        //    but no dedicated golden; B's upserted value is committed and therefore visible to the dedup
        //    engine's separate connection, so C value-matches B and they collapse into ONE group.
        await repo.BulkCreateAsync(new[]
        {
            new CarbIntake { Timestamp = T0.AddSeconds(10), Carbs = 45, DataSource = "loop", LegacyId = "c-C" },
            new CarbIntake { Timestamp = T0.AddSeconds(10), Carbs = 45, DataSource = "aaps", SyncIdentifier = "c-B" },
        }, CancellationToken.None);

        (await _fx.QueryAsync(tenant, ctx => ctx.CarbIntakes.AsNoTracking().CountAsync()))
            .Should().Be(2, "B upserts in place; C inserts — two physical rows");

        var links = await _fx.QueryAsync(tenant, ctx =>
            ctx.LinkedRecords.AsNoTracking().Where(lr => lr.RecordType == "carbintake").ToListAsync());
        links.Select(l => l.CanonicalId).Distinct().Should()
            .HaveCount(1, "post-commit dedup sees B's upserted value, so C collapses with B into one group");
    }
}
