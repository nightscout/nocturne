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
///   - BasalInjection: single CreateAsync upserts; BULK does not → bulk throws (D1).
///   - SensorGlucose:  bulk upserts; single CreateAsync does not → single throws (D3).
///   - Bolus:          both upsert (the consistent reference).
/// D2 (SensorGlucose re-driving dedup on inserts∪updates vs Bolus inserts-only) IS an observable
/// behavioural delta — PR-C's normalization is a real re-baseline, not a dead-code cleanup. It is
/// pinned by the paired goldens below:
///   D2_SensorGlucose_UnionFeed_NewPlusUpsertedSibling_CollapseIntoOneGroup → ONE canonical group.
///   D2_Bolus_InsertsOnlyFeed_NewPlusUpsertedSibling_StayTwoGroups          → TWO canonical groups.
/// Both run the same scenario byte-for-byte: seed a SyncId-keyed row B in its own group, then a
/// second BulkCreateAsync carrying a fresh insert C (no SyncId) at B's time+value AND a SyncId-upsert
/// of B onto that same time+value. The only difference is the dedup feed:
///   - SensorGlucose unions inserts+updates (SensorGlucoseRepository.cs:287 — `entities.Concat(
///     updatedEntities)` → `dedupInputs` at :290-297), so the upserted B is in the dedup batch and
///     C collapses with B into one group.
///   - Bolus feeds inserts only (BolusRepository.cs:341 — `dedupInputs` from `entities`, the whole
///     block gated by `if (entities.Count > 0)` at :326), so B is excluded from the batch and C
///     never collapses with it — two groups persist.
/// (The earlier "two far-apart rows, upsert one near the other" attempt failed to distinguish them
/// because the upsert never refreshes B's `linked_records.SourceTimestamp`, so B's persisted row
/// stays at its old position and a new sibling can match it identically with or without the union-
/// feed. The distinguishing case is the NEW-insert-plus-upserted-sibling batch above, where whether
/// the engine receives B's updated value as input is what flips the grouping — confirmed empirically
/// against the live DeduplicationService + real Postgres.)
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
    public async Task D1_BasalInjection_Bulk_DoesNotUpsertOnSyncId_ThrowsOnDuplicate()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBasalInjectionRepository>();

        // Same (DataSource, SyncIdentifier), distinct LegacyIds so both pass LegacyId dedup. The bulk
        // path does not upsert on SyncId (the D1 bug), so the second insert hits the unique index and
        // throws — whereas the single CreateAsync path would have collapsed these (next test).
        var act = async () => await repo.BulkCreateAsync(new[]
        {
            Bi(10, "aaps", legacyId: "bi-a", syncId: "bi-sync"),
            Bi(14, "aaps", legacyId: "bi-b", syncId: "bi-sync"),
        }, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>("bulk does not upsert on SyncId today (D1)");
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
    public async Task D3_SensorGlucose_SingleCreate_DoesNotUpsertOnSyncId_ThrowsOnDuplicate()
    {
        var tenant = Guid.NewGuid();
        using var scope = await _fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        await repo.CreateAsync(new SensorGlucose { Timestamp = T0, Mgdl = 100, DataSource = "dexcom", SyncIdentifier = "sg-s" }, CancellationToken.None);
        var act = async () => await repo.CreateAsync(
            new SensorGlucose { Timestamp = T0, Mgdl = 142, DataSource = "dexcom", SyncIdentifier = "sg-s" }, CancellationToken.None);

        // SensorGlucose single CreateAsync does NOT upsert (only bulk does, D3), so the duplicate
        // SyncId hits the unique index and throws.
        await act.Should().ThrowAsync<DbUpdateException>("SensorGlucose single CreateAsync does not upsert on SyncId today (D3)");
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
    public async Task D7_CarbIntake_CountExcludesNonPrimary_ButBolusAndSensorGlucoseOverCount()
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

        // Each pair links into one canonical group. Only CarbIntake.CountAsync excludes the
        // non-primary today (D7); Bolus and SensorGlucose over-count relative to what GetAsync returns.
        (await carb.CountAsync(null, null)).Should().Be(1, "CarbIntake.CountAsync excludes non-primary (D7)");
        (await bolus.CountAsync(null, null)).Should().Be(2, "Bolus.CountAsync over-counts non-primary today (D7)");
        (await sg.CountAsync(null, null)).Should().Be(2, "SensorGlucose.CountAsync over-counts non-primary today (D7)");
    }

    // ── D2: union-feed (SensorGlucose) vs inserts-only (Bolus) IS observable ───────────────────────
    //
    // The distinguishing case: a batch carrying a fresh insert C plus a SyncId-upsert of an existing
    // row B onto C's time+value. SensorGlucose unions inserts+updates into the dedup batch, so the
    // engine sees B's UPDATED value and collapses C with B (one group). Bolus feeds inserts only, so B
    // is absent from the batch and C does not collapse with it (two groups). Confirmed empirically:
    // with the engine instrumented, the SensorGlucose matcher saw B's updated value (matched=True) and
    // the Bolus matcher did not (matched=False), for the same candidate B in the same ±30s window.
    //
    // Each scenario runs as two BulkCreateAsync calls under one tenant:
    //   1. Seed B at T0 (its own canonical group), keyed by (DataSource, SyncIdentifier).
    //   2. Batch = [ C: fresh insert at T0+10s with B's value; B: SyncId-upsert moved to T0+10s ].
    // B's linked_records.SourceTimestamp stays at T0 (never refreshed), which is inside C's ±30s
    // window — so the candidacy is identical; what flips the result is whether B's updated value
    // reaches the dedup batch as input.

    [Fact]
    public async Task D2_SensorGlucose_UnionFeed_NewPlusUpsertedSibling_CollapseIntoOneGroup()
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
    public async Task D2_Bolus_InsertsOnlyFeed_NewPlusUpsertedSibling_StayTwoGroups()
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
        //    byte the SensorGlucose scenario above. The ONLY difference is BolusRepository feeds dedup
        //    inserts-only (excludes the upserted B), so C never sees B in the dedup batch and they stay
        //    in SEPARATE canonical groups. This is the observable D2 delta.
        await repo.BulkCreateAsync(new[]
        {
            new Bolus { Timestamp = T0.AddSeconds(10), Insulin = 5.0, DataSource = "loop", LegacyId = "b-C" },
            new Bolus { Timestamp = T0.AddSeconds(10), Insulin = 5.0, DataSource = "aaps", SyncIdentifier = "b-B" },
        }, CancellationToken.None);

        (await _fx.QueryAsync(tenant, ctx => ctx.Boluses.AsNoTracking().CountAsync()))
            .Should().Be(2, "B upserts in place; C inserts — two physical rows");

        var links = await _fx.QueryAsync(tenant, ctx =>
            ctx.LinkedRecords.AsNoTracking().Where(lr => lr.RecordType == "bolus").ToListAsync());
        links.Select(l => l.CanonicalId).Distinct().Should()
            .HaveCount(2, "inserts-only feed never hands B to the dedup batch, so C stays in its own group (D2)");
    }
}
