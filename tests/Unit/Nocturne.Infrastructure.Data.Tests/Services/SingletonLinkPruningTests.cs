using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// <see cref="DeduplicationService.PruneSingletonLinksAsync"/> deletes links that are alone in their
/// group, and every pass reads an old record with no link as the group it was. So pruning must be
/// invisible: each scenario builds one history in two databases, prunes only one, applies the same
/// next event to both, and requires the same duplicate groups with the same primaries.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
public class SingletonLinkPruningTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Event = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Prune_DeletesOnlyOldReconciledSingletonPrimaries()
    {
        using var world = new World();
        var single = await world.IngestBolus(Guid.CreateVersion7(), Event, "mylife-connector", 2.5);
        var pairA = await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(1), "mylife-connector", 1.0);
        var pairB = await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(1).AddSeconds(5), "glooko-connector", 1.0);
        var loneNonPrimary = await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(2), "mylife-connector", 3.0);
        await world.Age();
        await world.Ctx.LinkedRecords.Where(l => l.RecordId == loneNonPrimary)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPrimary, false));
        var fresh = await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(3), "mylife-connector", 4.0);

        var pruned = await world.Service.PruneSingletonLinksAsync(100, 10);

        pruned.Should().Be(1);
        var linked = await world.Ctx.LinkedRecords.Select(l => l.RecordId).ToListAsync();
        linked.Should().NotContain(single);
        linked.Should().Contain([pairA, pairB, loneNonPrimary, fresh],
            "a group of two, a lone non-primary that hides its record, and a link a day has not aged are kept");
    }

    [Fact]
    public async Task Prune_KeepsLinksTheReconcileCursorHasNotPassed()
    {
        using var world = new World();
        var single = await world.IngestBolus(Guid.CreateVersion7(), Event, "mylife-connector", 2.5);
        await world.Age(cursorAt: DateTime.UtcNow.AddDays(-3));

        (await world.Service.PruneSingletonLinksAsync(100, 10)).Should().Be(0);
        (await world.Ctx.LinkedRecords.AnyAsync(l => l.RecordId == single)).Should().BeTrue();
    }

    [Fact]
    public async Task Prune_WithoutAReconcileCursor_DeletesNothing()
    {
        using var world = new World();
        await world.IngestBolus(Guid.CreateVersion7(), Event, "mylife-connector", 2.5);
        await world.Age(withCursor: false);

        (await world.Service.PruneSingletonLinksAsync(100, 10)).Should().Be(0);
    }

    [Fact]
    public async Task Prune_StopsAfterMaxBatches()
    {
        using var world = new World();
        for (var i = 0; i < 5; i++)
            await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(i), "mylife-connector", 1 + i);
        await world.Age();

        (await world.Service.PruneSingletonLinksAsync(2, 2)).Should().Be(4);
        (await world.Ctx.LinkedRecords.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Prune_DeletesTheSingletonsOnItsPage_AndAdvancesTheCursorToThePageEnd()
    {
        using var world = new World();
        var first = await world.IngestBolus(Guid.CreateVersion7(), Event, "mylife-connector", 2.5);
        var pairA = await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(1), "mylife-connector", 1.0);
        var pairB = await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(1).AddSeconds(5), "glooko-connector", 1.0);
        var second = await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(2), "mylife-connector", 3.0);
        var beyond = await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(3), "mylife-connector", 4.0);
        await world.Age();
        await world.StampLinks([first, pairA, pairB, second, beyond], DateTime.UtcNow.AddDays(-2));
        var secondLink = await world.Link(second);

        (await world.Service.PruneSingletonLinksAsync(3, 1)).Should().Be(2);

        var linked = await world.Ctx.LinkedRecords.Select(l => l.RecordId).ToListAsync();
        linked.Should().BeEquivalentTo([pairA, pairB, beyond],
            "the page holds the pair's primary and both singles before it, and the single after it waits");
        var state = await world.PruneState();
        state.PruneCursorCreatedAt.Should().Be(secondLink.SysCreatedAt);
        state.PruneCursorId.Should().Be(secondLink.Id);
    }

    [Fact]
    public async Task Prune_OnATenantWhoseCursorIsAtTheEnd_DeletesNothingBehindIt()
    {
        using var world = new World();
        var pairA = await world.IngestBolus(Guid.CreateVersion7(), Event, "mylife-connector", 1.0);
        var pairB = await world.IngestBolus(Guid.CreateVersion7(), Event.AddSeconds(5), "glooko-connector", 1.0);
        await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(1), "mylife-connector", 2.0);
        await world.Age();
        (await world.Service.PruneSingletonLinksAsync(100, 10)).Should().Be(1);
        var before = await world.PruneState();

        await world.Ctx.LinkedRecords.Where(l => !l.IsPrimary).ExecuteDeleteAsync();

        (await world.Service.PruneSingletonLinksAsync(100, 10)).Should().Be(0,
            "the pair's primary became a singleton after the cursor passed it");
        (await world.Ctx.LinkedRecords.Select(l => l.RecordId).ToListAsync()).Should().ContainSingle()
            .And.BeSubsetOf([pairA, pairB]);
        (await world.PruneState()).Should().BeEquivalentTo(before, "a tick with nothing past the cursor writes nothing");
    }

    [Fact]
    public async Task Prune_RestartsOncePerRetention_AndDeletesALateSingletonOnThatPass()
    {
        using var world = new World();
        await world.IngestBolus(Guid.CreateVersion7(), Event, "mylife-connector", 1.0);
        await world.IngestBolus(Guid.CreateVersion7(), Event.AddSeconds(5), "glooko-connector", 1.0);
        await world.IngestBolus(Guid.CreateVersion7(), Event.AddHours(1), "mylife-connector", 2.0);
        await world.Age();
        (await world.Service.PruneSingletonLinksAsync(100, 10)).Should().Be(1);
        await world.Ctx.LinkedRecords.Where(l => !l.IsPrimary).ExecuteDeleteAsync();

        var lastReset = DateTime.UtcNow - DeduplicationService.SingletonLinkRetention - TimeSpan.FromMinutes(1);
        await world.Ctx.DedupReconcileState.ExecuteUpdateAsync(s => s.SetProperty(e => e.PruneCursorResetAt, lastReset));
        var restartedAfter = DateTime.UtcNow;

        (await world.Service.PruneSingletonLinksAsync(100, 10)).Should().Be(1);
        (await world.Ctx.LinkedRecords.AnyAsync()).Should().BeFalse();
        (await world.PruneState()).PruneCursorResetAt.Should().BeOnOrAfter(restartedAfter);
    }

    [Fact]
    public void SingletonsAmong_OnPostgres_BindsThePageAsOneArray()
    {
        using var context = OfflineDbContext.Create();

        var sql = DeduplicationService.SingletonsAmong(context, [Guid.NewGuid(), Guid.NewGuid()]).ToQueryString();

        sql.Should().MatchRegex(@"l\.id = ANY \(@\w+\)", "one uuid[] parameter keeps one statement for every page size");
        sql.Should().Contain("NOT EXISTS (");
    }

    [Fact]
    public async Task Ingest_ASecondSourceJoinsAPrunedReading_WhichStaysPrimary()
    {
        var reading = Guid.CreateVersion7();
        var copy = Guid.CreateVersion7();

        var groups = await AssertPruningIsInvisible(
            w => w.IngestGlucose(reading, Event, "dexcom-connector", 120),
            w => w.IngestGlucose(copy, Event.AddSeconds(10), "glooko-connector", 121));

        groups.Should().ContainSingle().Which.Should().Be(Group(reading, copy));
    }

    [Fact]
    public async Task Ingest_ASameSourceDoseBesideAPrunedDose_StaysApart()
    {
        var groups = await AssertPruningIsInvisible(
            w => w.IngestBolus(Guid.CreateVersion7(), Event, "mylife-connector", 2.0),
            w => w.IngestBolus(Guid.CreateVersion7(), Event.AddSeconds(5), "mylife-connector", 2.0));

        groups.Should().BeEmpty("two same-amount doses from one source are two doses");
    }

    [Fact]
    public async Task Ingest_ADriftedCopyWideJoinsAPrunedDose()
    {
        var dose = Guid.CreateVersion7();
        var copy = Guid.CreateVersion7();

        var groups = await AssertPruningIsInvisible(
            w => w.IngestBolus(dose, Event, "mylife-connector", 2.0),
            w => w.IngestBolus(copy, Event.AddMinutes(-4), "glooko-connector", 2.0));

        groups.Should().ContainSingle().Which.Should().Be(Group(copy, dose),
            "a wide join re-derives the primary, so the earlier copy leads");
    }

    [Fact]
    public async Task Ingest_TwoPrunedSameValueDoses_MakeADriftedCopyAmbiguous()
    {
        var groups = await AssertPruningIsInvisible(
            async w =>
            {
                await w.IngestBolus(Guid.CreateVersion7(), Event, "mylife-connector", 2.0);
                await w.IngestBolus(Guid.CreateVersion7(), Event.AddMinutes(6), "mylife-connector", 2.0);
            },
            w => w.IngestBolus(Guid.CreateVersion7(), Event.AddMinutes(3), "glooko-connector", 2.0));

        groups.Should().BeEmpty("a copy with two candidate doses cannot be attributed to either");
    }

    [Fact]
    public async Task Reconcile_MergesAFreshLinkWithAPrunedNeighbour()
    {
        var reading = Guid.CreateVersion7();
        var copy = Guid.CreateVersion7();

        var groups = await AssertPruningIsInvisible(
            w => w.IngestGlucose(reading, Event, "dexcom-connector", 120),
            async w =>
            {
                // A copy whose own insert-time pass missed the reading, as a concurrent ingest can.
                await w.AddGlucose(copy, Event.AddSeconds(10), "glooko-connector", 120);
                await w.AddSingletonLink(RecordType.SensorGlucose, copy, Event.AddSeconds(10), "glooko-connector");
                await w.Service.ReconcileNewLinksAsync(100, 10);
            });

        groups.Should().ContainSingle().Which.Should().Be(Group(reading, copy));
    }

    [Fact]
    public async Task Reconcile_APrunedSameValueDose_StillMakesAWidePairAmbiguous()
    {
        var mylife = Guid.CreateVersion7();
        var tandem = Guid.CreateVersion7();
        var copy = Guid.CreateVersion7();

        var groups = await AssertPruningIsInvisible(
            async w =>
            {
                // A stored group that is never pruned, and beside it a same-value dose that is.
                await w.IngestBolus(mylife, Event, "mylife-connector", 2.0);
                await w.IngestBolus(tandem, Event.AddSeconds(5), "tandem-connector", 2.0);
                await w.IngestBolus(Guid.CreateVersion7(), Event.AddMinutes(8), "mylife-connector", 2.0);
            },
            async w =>
            {
                await w.AddBolus(copy, Event.AddMinutes(4), "glooko-connector", 2.0);
                await w.AddSingletonLink(RecordType.Bolus, copy, Event.AddMinutes(4), "glooko-connector");
                await w.Service.ReconcileNewLinksAsync(100, 10);
            });

        groups.Should().ContainSingle().Which.Should().Be(Group(mylife, tandem),
            "the copy has two same-value candidates, so neither merge is safe");
    }

    [Fact]
    public async Task FullJob_LinksADriftedPairOfPrunedDoses()
    {
        var dose = Guid.CreateVersion7();
        var copy = Guid.CreateVersion7();

        var groups = await AssertPruningIsInvisible(
            async w =>
            {
                // Both arrived as singletons, the drifted copy only after the dose's pass had run.
                await w.AddBolus(dose, Event, "mylife-connector", 2.0);
                await w.AddSingletonLink(RecordType.Bolus, dose, Event, "mylife-connector");
                await w.AddBolus(copy, Event.AddMinutes(5), "glooko-connector", 2.0);
                await w.AddSingletonLink(RecordType.Bolus, copy, Event.AddMinutes(5), "glooko-connector");
            },
            async w => (await w.Service.DeduplicateAllAsync()).Success.Should().BeTrue());

        groups.Should().ContainSingle().Which.Should().Be(Group(dose, copy));
    }

    [Fact]
    public async Task Reconcile_ASoftDeletedPrunedReading_JoinsAsTheDuplicateOfALiveCopy()
    {
        var deleted = Guid.CreateVersion7();
        var live = Guid.CreateVersion7();

        var groups = await AssertPruningIsInvisible(
            async w =>
            {
                await w.IngestGlucose(deleted, Event, "dexcom-connector", 120);
                await w.Ctx.SensorGlucose.Where(s => s.Id == deleted)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.DeletedAt, DateTime.UtcNow));
            },
            async w =>
            {
                await w.AddGlucose(live, Event.AddSeconds(10), "glooko-connector", 120);
                await w.AddSingletonLink(RecordType.SensorGlucose, live, Event.AddSeconds(10), "glooko-connector");
                await w.Service.ReconcileNewLinksAsync(100, 10);
            });

        groups.Should().ContainSingle().Which.Should().Be(Group(live, deleted),
            "a deleted record is no survivor, so the live copy leads");
    }

    /// <summary>
    /// Runs <paramref name="history"/> in two databases, ages both past
    /// <see cref="DeduplicationService.SingletonLinkRetention"/> and the reconcile cursor, prunes one,
    /// runs <paramref name="next"/> in both and requires the same groups.
    /// </summary>
    /// <returns>The groups of more than one record, as <see cref="Group"/> renders them.</returns>
    private static async Task<List<string>> AssertPruningIsInvisible(Func<World, Task> history, Func<World, Task> next)
    {
        using var kept = new World();
        using var pruned = new World();
        foreach (var world in new[] { kept, pruned })
        {
            await history(world);
            await world.Age();
        }

        (await pruned.Service.PruneSingletonLinksAsync(1000, 10)).Should().BePositive(
            "a scenario whose history leaves nothing to prune proves nothing");

        foreach (var world in new[] { kept, pruned })
        {
            await next(world);
            world.Ctx.ChangeTracker.Clear();
        }

        var expected = await kept.Groups();
        (await pruned.Groups()).Should().BeEquivalentTo(expected);
        return expected;
    }

    /// <summary>A group rendered as its primary, then its other records in id order.</summary>
    private static string Group(Guid primary, params Guid[] others) =>
        string.Join(",", [primary, .. others.Order()]);

    private sealed class World : IDisposable
    {
        private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

        public World()
        {
            Ctx = _db.CreateContext();
            Ctx.TenantId = TenantId;
            Service = new DeduplicationService(
                Ctx.AsFactory(), new Mock<IServiceScopeFactory>().Object, NullLogger<DeduplicationService>.Instance, TimeProvider.System);
        }

        public NocturneDbContext Ctx { get; }

        public DeduplicationService Service { get; }

        public async Task<Guid> IngestGlucose(Guid id, DateTime at, string source, double mgdl)
        {
            var entity = await AddGlucose(id, at, source, mgdl);
            await Service.DeduplicateBatchAsync(
                RecordType.SensorGlucose, [new DeduplicationInput(id, Mills(at), source, MatchCriteriaMapper.From(entity))]);
            return id;
        }

        public async Task<Guid> IngestBolus(Guid id, DateTime at, string source, double insulin)
        {
            var entity = await AddBolus(id, at, source, insulin);
            await Service.DeduplicateBatchAsync(
                RecordType.Bolus, [new DeduplicationInput(id, Mills(at), source, MatchCriteriaMapper.From(entity))]);
            return id;
        }

        public async Task<SensorGlucoseEntity> AddGlucose(Guid id, DateTime at, string source, double mgdl)
        {
            var entity = new SensorGlucoseEntity
            {
                Id = id, TenantId = TenantId, Timestamp = at, DataSource = source, Mgdl = mgdl,
            };
            Ctx.SensorGlucose.Add(entity);
            await Ctx.SaveChangesAsync();
            return entity;
        }

        public async Task<BolusEntity> AddBolus(Guid id, DateTime at, string source, double insulin)
        {
            var entity = new BolusEntity
            {
                Id = id, TenantId = TenantId, Timestamp = at, DataSource = source, Insulin = insulin,
            };
            Ctx.Boluses.Add(entity);
            await Ctx.SaveChangesAsync();
            return entity;
        }

        /// <summary>
        /// A record's own singleton link, stamped three minutes ago: past the reconcile cursor
        /// <see cref="Age"/> sets and old enough for its commit lag.
        /// </summary>
        public async Task AddSingletonLink(RecordType recordType, Guid recordId, DateTime at, string source)
        {
            var link = new LinkedRecordEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = TenantId,
                CanonicalId = Guid.CreateVersion7(),
                RecordType = RecordTypeKeys.Key(recordType),
                RecordId = recordId,
                SourceTimestamp = Mills(at),
                DataSource = source,
                IsPrimary = true,
            };
            Ctx.LinkedRecords.Add(link);
            await Ctx.SaveChangesAsync();
            await Ctx.LinkedRecords.Where(l => l.Id == link.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.SysCreatedAt, DateTime.UtcNow.AddMinutes(-3)));
        }

        /// <summary>
        /// Stamps every record and link two days old and, unless <paramref name="withCursor"/> is
        /// false, puts the reconcile cursor at <paramref name="cursorAt"/>, an hour ago by default.
        /// </summary>
        public async Task Age(bool withCursor = true, DateTime? cursorAt = null)
        {
            var old = DateTime.UtcNow.AddDays(-2);
            await Ctx.SensorGlucose.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(e => e.SysCreatedAt, old));
            await Ctx.Boluses.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(e => e.SysCreatedAt, old));
            await Ctx.LinkedRecords.ExecuteUpdateAsync(s => s.SetProperty(l => l.SysCreatedAt, old));

            if (withCursor)
            {
                await DeduplicationService.SetCursorAsync(
                    Ctx, new ReconcileCursor(cursorAt ?? DateTime.UtcNow.AddHours(-1), Guid.Empty), CancellationToken.None);
            }
            Ctx.ChangeTracker.Clear();
        }

        /// <summary>Stamps the links of <paramref name="recordIds"/> a second apart from <paramref name="from"/>, in order.</summary>
        public async Task StampLinks(IReadOnlyList<Guid> recordIds, DateTime from)
        {
            for (var i = 0; i < recordIds.Count; i++)
            {
                var recordId = recordIds[i];
                await Ctx.LinkedRecords.Where(l => l.RecordId == recordId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.SysCreatedAt, from.AddSeconds(i)));
            }
        }

        public Task<LinkedRecordEntity> Link(Guid recordId) =>
            Ctx.LinkedRecords.AsNoTracking().SingleAsync(l => l.RecordId == recordId);

        public Task<DedupReconcileStateEntity> PruneState() =>
            Ctx.DedupReconcileState.AsNoTracking().SingleAsync();

        public async Task<List<string>> Groups()
        {
            var links = await Ctx.LinkedRecords.AsNoTracking().ToListAsync();
            return links.GroupBy(l => l.CanonicalId)
                .Where(g => g.Count() > 1)
                .Select(g => Group(
                    g.Single(l => l.IsPrimary).RecordId,
                    [.. g.Where(l => !l.IsPrimary).Select(l => l.RecordId)]))
                .ToList();
        }

        public void Dispose()
        {
            Ctx.Dispose();
            _db.Dispose();
        }

        private static long Mills(DateTime at) => new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }
}
