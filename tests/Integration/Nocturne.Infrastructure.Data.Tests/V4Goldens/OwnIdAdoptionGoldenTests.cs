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
}
