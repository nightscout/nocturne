using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// <see cref="ILegacyKeyedRepository{TRecord}.AdoptOwnIdsAsync"/> against Postgres: a stored record
/// with no legacy id takes the id it goes out on the wire under (its uuid, or that uuid's 24-hex
/// prefix, resolved through Postgres's byte-wise uuid order), live or deleted, so the legacy-id
/// upsert that follows matches it. Nightscout write-back sends such a record upstream under that id
/// and the connector pulls the copy back (#1804).
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class OwnIdAdoptionGoldenTests(V4GoldenFixture fx)
{
    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private static SensorGlucose Reading(string? legacyId, int minute = 0) =>
        new() { Timestamp = T0.AddMinutes(minute), Mgdl = 120, DataSource = "dexcom-connector", LegacyId = legacyId };

    private static SensorGlucose Echo(string legacyId, int minute = 0) =>
        new() { Timestamp = T0.AddMinutes(minute), Mgdl = 120, DataSource = "nightscout-connector", LegacyId = legacyId };

    private Task<List<(Guid Id, string? LegacyId, bool Deleted)>> ReadingsAsync(Guid tenant) =>
        fx.QueryAsync(tenant, ctx => ctx.SensorGlucose.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => new ValueTuple<Guid, string?, bool>(e.Id, e.LegacyId, e.DeletedAt != null))
            .ToListAsync());

    private Task DeleteByUserAsync(Guid tenant, Guid id) =>
        fx.QueryAsync(tenant, ctx => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sensor_glucose SET deleted_at = now(), deleted_by_user = true WHERE id = {id}"));

    public static TheoryData<string> WireForms => new() { "uuid", "prefix" };

    private static string WireId(Guid id, string form) => form == "uuid" ? id.ToString() : MongoObjectId.FromGuid(id);

    [Theory]
    [MemberData(nameof(WireForms))]
    public async Task ALiveRecord_TakesItsWireId_AndItsEchoUpdatesItInPlace(string form)
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var stored = await repo.CreateAsync(Reading(null), WriteOrigin.Live, CancellationToken.None);
        var wireId = WireId(stored.Id, form);

        var adopted = (await repo.AdoptOwnIdsAsync([wireId, "dexcom_unrelated"], CancellationToken.None)).ToList();
        var written = await repo.BulkUpsertAsync([Echo(wireId)], WriteOrigin.Live, CancellationToken.None);

        adopted.Should().ContainSingle().Which.Should().Match<SensorGlucose>(r => r.Id == stored.Id && r.LegacyId == wireId);
        written.Updated.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
        (await ReadingsAsync(tenant)).Should().Equal((stored.Id, wireId, false));
    }

    [Theory]
    [MemberData(nameof(WireForms))]
    public async Task ARecordTheUserDeleted_TakesItsWireId_AndItsEchoStaysDeleted(string form)
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var stored = await repo.CreateAsync(Reading(null), WriteOrigin.Live, CancellationToken.None);
        await DeleteByUserAsync(tenant, stored.Id);
        var wireId = WireId(stored.Id, form);

        var adopted = await repo.AdoptOwnIdsAsync([wireId], CancellationToken.None);
        var written = await repo.BulkUpsertAsync([Echo(wireId)], WriteOrigin.Live, CancellationToken.None);

        adopted.Should().ContainSingle();
        written.Should().BeEmpty();
        written.SkippedDeleted.Should().Be(1);
        (await ReadingsAsync(tenant)).Should().Equal((stored.Id, wireId, true));
    }

    [Fact]
    public async Task AnIdALiveRecordAlreadyHolds_IsLeftToThatRecord()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var unkeyed = await repo.CreateAsync(Reading(null), WriteOrigin.Live, CancellationToken.None);
        var wireId = unkeyed.Id.ToString();
        var holder = await repo.CreateAsync(Reading(wireId, minute: 5), WriteOrigin.Live, CancellationToken.None);

        var adopted = await repo.AdoptOwnIdsAsync([wireId], CancellationToken.None);

        adopted.Should().BeEmpty();
        (await ReadingsAsync(tenant)).Should().BeEquivalentTo(new[] { (unkeyed.Id, (string?)null, false), (holder.Id, wireId, false) });
    }

    [Fact]
    public async Task ARecordWithALegacyId_KeepsIt_EvenWhenTheIdIsAUuid()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var uuidLegacyId = Guid.CreateVersion7().ToString();
        var stored = await repo.CreateAsync(Reading(uuidLegacyId), WriteOrigin.Live, CancellationToken.None);

        var adopted = await repo.AdoptOwnIdsAsync([stored.Id.ToString(), uuidLegacyId], CancellationToken.None);
        var written = await repo.BulkUpsertAsync([Echo(uuidLegacyId)], WriteOrigin.Live, CancellationToken.None);

        adopted.Should().BeEmpty();
        written.Updated.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
        (await ReadingsAsync(tenant)).Should().Equal((stored.Id, uuidLegacyId, false));
    }

    [Fact]
    public async Task AnIdThatNamesNoStoredRecord_AdoptsNothing()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var stored = await repo.CreateAsync(Reading(null), WriteOrigin.Live, CancellationToken.None);

        var adopted = await repo.AdoptOwnIdsAsync(
            [Guid.CreateVersion7().ToString(), "ffffffffffff7fff8fffffff", stored.Id.ToString().ToUpperInvariant()],
            CancellationToken.None);

        adopted.Should().BeEmpty();
        (await ReadingsAsync(tenant)).Should().Equal((stored.Id, (string?)null, false));
    }

    [Fact]
    public async Task ManyIds_ResolveAcrossQueryChunks()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var stored = (await repo.BulkCreateAsync(
            Enumerable.Range(0, 250).Select(i => Reading(null, minute: i * 5)).ToList(),
            WriteOrigin.Backfill, CancellationToken.None)).ToList();

        var adopted = await repo.AdoptOwnIdsAsync(stored.Select(r => MongoObjectId.FromGuid(r.Id)).ToList(), CancellationToken.None);

        adopted.Select(r => r.LegacyId).Should().BeEquivalentTo(stored.Select(r => MongoObjectId.FromGuid(r.Id)));
    }

    [Fact]
    public async Task SiblingsTakeTheAnchorsIdThroughTheirCorrelationId_OneLiveRowPerTable()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var aps = scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>();
        var pump = scope.ServiceProvider.GetRequiredService<IPumpSnapshotRepository>();
        var correlationId = Guid.CreateVersion7();
        var anchor = await aps.CreateAsync(
            new ApsSnapshot { Timestamp = T0, Device = "openaps://rig", CorrelationId = correlationId, AidAlgorithm = AidAlgorithm.OpenAps },
            WriteOrigin.Live, CancellationToken.None);
        var pumpA = await pump.CreateAsync(
            new PumpSnapshot { Timestamp = T0, Device = "openaps://rig", CorrelationId = correlationId }, WriteOrigin.Live, CancellationToken.None);
        var pumpB = await pump.CreateAsync(
            new PumpSnapshot { Timestamp = T0.AddSeconds(1), Device = "openaps://rig", CorrelationId = correlationId }, WriteOrigin.Live, CancellationToken.None);
        var unrelated = await pump.CreateAsync(
            new PumpSnapshot { Timestamp = T0.AddMinutes(5), Device = "openaps://rig", CorrelationId = Guid.CreateVersion7() }, WriteOrigin.Live, CancellationToken.None);

        var wireId = anchor.Id.ToString();
        var adoptedAnchor = (await aps.AdoptOwnIdsAsync([wireId], CancellationToken.None)).Single();
        var siblings = await pump.AdoptLegacyIdsByCorrelationAsync(
            new Dictionary<Guid, string> { [adoptedAnchor.CorrelationId!.Value] = adoptedAnchor.LegacyId! }, CancellationToken.None);

        siblings.Should().Be(1, "the unique legacy-id index admits one live row per id");
        new[] { pumpA.Id, pumpB.Id }.Should().Contain((await pump.GetByLegacyIdAsync(wireId, CancellationToken.None))!.Id);
        (await pump.GetByIdAsync(unrelated.Id, CancellationToken.None))!.LegacyId.Should().BeNull();
        (await pump.AdoptLegacyIdsByCorrelationAsync(new Dictionary<Guid, string>(), CancellationToken.None)).Should().Be(0);
    }

    /// <summary>
    /// Write-back sends a uuid-shaped legacy id upstream as its 24-hex prefix, in whatever spelling
    /// the uploader gave it. The pull-back resolves that prefix to the legacy id, live or deleted,
    /// through the partial expression index, and the legacy-id upsert then updates the record, or
    /// is withheld by the user's deletion.
    /// </summary>
    [Theory]
    [InlineData("0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f")]
    [InlineData("0198C2A4-1F3B-7C2D-9E55-6A1B2C3D4E5F")]
    [InlineData("{0198c2a4-1f3b-7c2d-9e55-6a1b2c3d4e5f}")]
    [InlineData("0198c2a41f3b7c2d9e556a1b2c3d4e5f")]
    public async Task AUuidShapedLegacyId_IsResolvedFromItsPrefix_AndItsEchoUpdatesTheRecord(string legacyId)
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var stored = await repo.CreateAsync(Reading(legacyId), WriteOrigin.Live, CancellationToken.None);
        await repo.CreateAsync(Reading("dexcom_0198c2a41f3b7c2d9e556a1b", minute: 5), WriteOrigin.Live, CancellationToken.None);
        const string prefix = "0198c2a41f3b7c2d9e556a1b";

        var resolved = (await repo.ResolveUuidLegacyIdsAsync([prefix, "0198c2a41f3b7c2d9e55ffff", "dexcom_x"], CancellationToken.None)).ToList();
        var written = await repo.BulkUpsertAsync([Echo(resolved.Single().LegacyId)], WriteOrigin.Live, CancellationToken.None);

        resolved.Should().Equal(new WireLegacyId(prefix, legacyId));
        written.Updated.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
    }

    [Fact]
    public async Task AUuidShapedLegacyIdTheUserDeleted_IsStillResolved_SoItsEchoStaysDeleted()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var legacyId = Guid.CreateVersion7().ToString();
        var stored = await repo.CreateAsync(Reading(legacyId), WriteOrigin.Live, CancellationToken.None);
        await DeleteByUserAsync(tenant, stored.Id);
        var prefix = MongoObjectId.FromGuid(Guid.Parse(legacyId));

        var resolved = (await repo.ResolveUuidLegacyIdsAsync([prefix], CancellationToken.None)).ToList();
        var written = await repo.BulkUpsertAsync([Echo(resolved.Single().LegacyId)], WriteOrigin.Live, CancellationToken.None);

        written.Should().BeEmpty();
        written.SkippedDeleted.Should().Be(1);
        (await ReadingsAsync(tenant)).Should().Equal((stored.Id, legacyId, true));
    }

    [Fact]
    public async Task ResolvingIsTenantScoped_AndSkipsIdsThatAreNotUuidPrefixes()
    {
        var owner = Guid.NewGuid();
        var legacyId = Guid.CreateVersion7().ToString();
        using (var scope = await fx.BeginTenantScopeAsync(owner))
            await scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>()
                .CreateAsync(Reading(legacyId), WriteOrigin.Live, CancellationToken.None);

        using var other = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = other.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        (await repo.ResolveUuidLegacyIdsAsync([MongoObjectId.FromGuid(Guid.Parse(legacyId))], CancellationToken.None))
            .Should().BeEmpty();
        (await repo.ResolveUuidLegacyIdsAsync([legacyId, MongoObjectId.NewObjectId()], CancellationToken.None))
            .Should().BeEmpty();
    }

    /// <summary>
    /// A v4-native treatment is written back under its record's uuid prefix. Its pull-back adopts
    /// the id on the record it names and on the records sharing that record's correlation id (a
    /// meal's bolus and carbs), live or deleted, across the treatment tables.
    /// </summary>
    [Fact]
    public async Task ATreatmentsRecords_TakeTheWireIdAcrossTables_LiveOrDeleted()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var boluses = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        var carbs = scope.ServiceProvider.GetRequiredService<ICarbIntakeRepository>();
        var correlationId = Guid.CreateVersion7();
        var bolus = await boluses.CreateAsync(
            new Bolus { Timestamp = T0, Insulin = 2.5, CorrelationId = correlationId }, WriteOrigin.Live, CancellationToken.None);
        var carb = await carbs.CreateAsync(
            new CarbIntake { Timestamp = T0, Carbs = 40, CorrelationId = correlationId }, WriteOrigin.Live, CancellationToken.None);
        await fx.QueryAsync(tenant, ctx => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE carb_intakes SET deleted_at = now(), deleted_by_user = true WHERE id = {carb.Id}"));
        var wireId = MongoObjectId.FromGuid(bolus.Id);

        var adopted = (await boluses.AdoptOwnIdsAsync([wireId], CancellationToken.None)).Single();
        var siblings = await carbs.AdoptLegacyIdsByCorrelationAsync(
            new Dictionary<Guid, string> { [adopted.CorrelationId!.Value] = wireId }, CancellationToken.None);
        var carbEcho = await carbs.BulkUpsertAsync(
            [new CarbIntake { Timestamp = T0, Carbs = 40, LegacyId = wireId }], WriteOrigin.Live, CancellationToken.None);

        adopted.LegacyId.Should().Be(wireId);
        siblings.Should().Be(1);
        (await boluses.GetByLegacyIdAsync(wireId, CancellationToken.None))!.Id.Should().Be(bolus.Id);
        carbEcho.SkippedDeleted.Should().Be(1, "the carbs the user deleted hold the adopted id");
    }

    /// <summary>
    /// <see cref="ILegacyKeyedRepository{TRecord}.GetLegacyIdsWriteBackMaySendAsync"/> is how a
    /// pull tells a write-back echo from the upstream's own record. Only a row a live write has
    /// touched can have been written back, and never the connector's own. A live row decides over the
    /// user's deletion of the same id, the latest of several deletions decides over the rest, a
    /// user's deletion holds on its own, a system sweep does not, and a row with no source counts as
    /// another source.
    /// </summary>
    [Fact]
    public async Task LegacyIdsWriteBackMaySend_AreDecidedByTheRowThatGovernsTheId()
    {
        using (var otherScope = await fx.BeginTenantScopeAsync(Guid.NewGuid()))
            await otherScope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>()
                .CreateAsync(Reading("other-tenant"), WriteOrigin.Live, CancellationToken.None);
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        await repo.CreateAsync(Reading("dexcom-live"), WriteOrigin.Live, CancellationToken.None);
        await repo.CreateAsync(Echo("connector-live", minute: 5), WriteOrigin.Live, CancellationToken.None);
        var deleted = await repo.CreateAsync(Reading("dexcom-deleted", minute: 10), WriteOrigin.Live, CancellationToken.None);
        await DeleteByUserAsync(tenant, deleted.Id);
        var replaced = await repo.CreateAsync(Reading("connector-over-tombstone", minute: 15), WriteOrigin.Live, CancellationToken.None);
        await DeleteByUserAsync(tenant, replaced.Id);
        var liveOverTombstone = await repo.CreateAsync(Echo("connector-pending", minute: 20), WriteOrigin.Live, CancellationToken.None);
        await fx.QueryAsync(tenant, ctx => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sensor_glucose SET legacy_id = 'connector-over-tombstone' WHERE id = {liveOverTombstone.Id}"));
        var swept = await repo.CreateAsync(Reading("dexcom-swept", minute: 25), WriteOrigin.Live, CancellationToken.None);
        await fx.QueryAsync(tenant, ctx => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sensor_glucose SET deleted_at = now(), deleted_by_user = false WHERE id = {swept.Id}"));
        await repo.CreateAsync(
            new SensorGlucose { Timestamp = T0.AddMinutes(30), Mgdl = 120, LegacyId = "unsourced" }, WriteOrigin.Live, CancellationToken.None);
        await repo.CreateAsync(Reading("imported", minute: 35), WriteOrigin.Backfill, CancellationToken.None);
        var importedThenEdited = await repo.CreateAsync(Reading("imported-then-edited", minute: 40), WriteOrigin.Backfill, CancellationToken.None);
        importedThenEdited.Mgdl = 140;
        await repo.UpdateAsync(importedThenEdited.Id, importedThenEdited, WriteOrigin.Live, CancellationToken.None);
        var olderTombstone = await repo.CreateAsync(Reading("two-tombstones", minute: 45), WriteOrigin.Live, CancellationToken.None);
        await DeleteByUserAsync(tenant, olderTombstone.Id);
        var newerTombstone = await repo.CreateAsync(Reading("two-tombstones-import", minute: 50), WriteOrigin.Backfill, CancellationToken.None);
        await fx.QueryAsync(tenant, ctx => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sensor_glucose SET legacy_id = 'two-tombstones', deleted_at = now() + interval '1 minute', deleted_by_user = true WHERE id = {newerTombstone.Id}"));

        var sendable = await repo.GetLegacyIdsWriteBackMaySendAsync(
            ["dexcom-live", "connector-live", "dexcom-deleted", "connector-over-tombstone", "dexcom-swept", "unsourced",
                "other-tenant", "absent", "imported", "imported-then-edited", "two-tombstones"],
            "nightscout-connector", CancellationToken.None);

        sendable.Should().BeEquivalentTo("dexcom-live", "dexcom-deleted", "unsourced", "imported-then-edited");
    }

    private Task<bool> WrittenLiveAsync(Guid tenant, Guid id) =>
        fx.QueryAsync(tenant, ctx => ctx.SensorGlucose.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
            .Where(e => e.Id == id).Select(e => e.WrittenLive).SingleAsync());

    /// <summary>
    /// A live write marks the rows it inserts or changes, through every write path; an import marks
    /// nothing, and a live write that changes nothing leaves a row as it was.
    /// </summary>
    [Fact]
    public async Task WrittenLive_IsSetByLiveWritesOnly()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        var live = await repo.CreateAsync(Reading("live"), WriteOrigin.Live, CancellationToken.None);
        var imported = await repo.CreateAsync(Reading("imported", minute: 5), WriteOrigin.Backfill, CancellationToken.None);
        var bulkImported = (await repo.BulkUpsertAsync([Reading("bulk-imported", minute: 10)], WriteOrigin.Backfill, CancellationToken.None)).Single();
        var bulkLive = (await repo.BulkUpsertAsync([Reading("bulk-live", minute: 15)], WriteOrigin.Live, CancellationToken.None)).Single();
        var reimported = (await repo.BulkUpsertAsync([new SensorGlucose { Timestamp = T0.AddMinutes(10), Mgdl = 150, DataSource = "dexcom-connector", LegacyId = "bulk-imported" }], WriteOrigin.Live, CancellationToken.None)).Single();

        (await WrittenLiveAsync(tenant, live.Id)).Should().BeTrue();
        (await WrittenLiveAsync(tenant, imported.Id)).Should().BeFalse();
        (await WrittenLiveAsync(tenant, bulkLive.Id)).Should().BeTrue();
        reimported.Id.Should().Be(bulkImported.Id);
        (await WrittenLiveAsync(tenant, bulkImported.Id)).Should().BeTrue("a live write changed it");
    }

    /// <summary>
    /// Treatment write-back sends a legacy id that is neither an ObjectId nor a uuid as the hash
    /// <see cref="MongoObjectId.Coerce"/> gives it. Postgres computes the same hash through the
    /// migration's <c>legacy_id_wire_hash</c>, live rows and deleted alike, tenant-scoped, and a uuid
    /// or an ObjectId never answers for one.
    /// </summary>
    [Fact]
    public async Task HashedLegacyIds_ResolveToTheLegacyIdTheirHashNames()
    {
        var other = Guid.NewGuid();
        using (var otherScope = await fx.BeginTenantScopeAsync(other))
            await otherScope.ServiceProvider.GetRequiredService<IBolusRepository>()
                .CreateAsync(new Bolus { Timestamp = T0, Insulin = 1, LegacyId = "other-tenant-bolus" }, WriteOrigin.Live, CancellationToken.None);
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        string[] legacyIds = ["syn-3a7c0e9f1b2d4c6e", "5F1A2B3C4D5E6F7A8B9C0D1E", "café-ü-1", "65a1b2c3d4e5f60718293a4b", "4f1c1d2e-3a4b-4c5d-8e6f-7a8b9c0d1e2f"];
        foreach (var (legacyId, i) in legacyIds.Select((l, i) => (l, i)))
            await repo.CreateAsync(new Bolus { Timestamp = T0.AddMinutes(i * 10), Insulin = 1, LegacyId = legacyId }, WriteOrigin.Live, CancellationToken.None);
        var deleted = await repo.CreateAsync(new Bolus { Timestamp = T0.AddHours(2), Insulin = 1, LegacyId = "deleted-bolus" }, WriteOrigin.Live, CancellationToken.None);
        await fx.QueryAsync(tenant, ctx => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE boluses SET deleted_at = now(), deleted_by_user = true WHERE id = {deleted.Id}"));

        var resolved = await repo.ResolveHashedLegacyIdsAsync(
            [.. legacyIds.Select(l => MongoObjectId.Coerce(l)!), MongoObjectId.Coerce("deleted-bolus")!, MongoObjectId.Coerce("other-tenant-bolus")!],
            CancellationToken.None);

        resolved.Should().BeEquivalentTo(new[]
        {
            new WireLegacyId(MongoObjectId.Coerce("syn-3a7c0e9f1b2d4c6e")!, "syn-3a7c0e9f1b2d4c6e"),
            new WireLegacyId(MongoObjectId.Coerce("5F1A2B3C4D5E6F7A8B9C0D1E")!, "5F1A2B3C4D5E6F7A8B9C0D1E"),
            new WireLegacyId(MongoObjectId.Coerce("café-ü-1")!, "café-ü-1"),
            new WireLegacyId(MongoObjectId.Coerce("deleted-bolus")!, "deleted-bolus"),
        });
    }

    /// <summary>
    /// The v1 and v3 reads serve a treatment under its own uuid's prefix whatever its legacy id, and
    /// an earlier write-back sent an edit under that id.
    /// </summary>
    [Fact]
    public async Task KeyedOwnIds_ResolveToTheLegacyIdOfTheRecordTheyName()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        var keyed = await repo.CreateAsync(new Bolus { Timestamp = T0, Insulin = 1, LegacyId = "65a1b2c3d4e5f60718293a4b" }, WriteOrigin.Live, CancellationToken.None);
        var unkeyed = await repo.CreateAsync(new Bolus { Timestamp = T0.AddMinutes(10), Insulin = 1 }, WriteOrigin.Live, CancellationToken.None);

        var resolved = await repo.ResolveKeyedOwnIdsAsync(
            [MongoObjectId.FromGuid(keyed.Id), MongoObjectId.FromGuid(unkeyed.Id), keyed.Id.ToString()], CancellationToken.None);

        resolved.Should().Equal(new WireLegacyId(MongoObjectId.FromGuid(keyed.Id), "65a1b2c3d4e5f60718293a4b"));
    }

    /// <summary>
    /// The read-only half of adoption: which ids name a record with no legacy id, and whether
    /// write-back may have sent that record. It writes nothing.
    /// </summary>
    [Fact]
    public async Task UnkeyedOwnIds_AreFoundWithoutAdoptingThem()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var live = await repo.CreateAsync(Reading(null), WriteOrigin.Live, CancellationToken.None);
        var imported = await repo.CreateAsync(Reading(null, minute: 5), WriteOrigin.Backfill, CancellationToken.None);
        var connector = await repo.CreateAsync(new SensorGlucose { Timestamp = T0.AddMinutes(10), Mgdl = 120, DataSource = "nightscout-connector" }, WriteOrigin.Live, CancellationToken.None);
        var shadowed = await repo.CreateAsync(Reading(null, minute: 15), WriteOrigin.Live, CancellationToken.None);
        await repo.CreateAsync(Reading(shadowed.Id.ToString(), minute: 20), WriteOrigin.Live, CancellationToken.None);

        var found = await repo.FindUnkeyedOwnIdsAsync(
            [MongoObjectId.FromGuid(live.Id), imported.Id.ToString(), MongoObjectId.FromGuid(connector.Id), shadowed.Id.ToString()],
            "nightscout-connector", CancellationToken.None);

        found.Should().BeEquivalentTo(new[]
        {
            new UnkeyedOwnId(MongoObjectId.FromGuid(live.Id), WriteBackMaySend: true),
            new UnkeyedOwnId(imported.Id.ToString(), WriteBackMaySend: false),
            new UnkeyedOwnId(MongoObjectId.FromGuid(connector.Id), WriteBackMaySend: false),
        });
        (await ReadingsAsync(tenant)).Where(r => r.LegacyId is not null).Should().ContainSingle();
    }
}
