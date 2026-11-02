using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <c>sensor_glucose.is_secondary_stream</c> on PostgreSQL under the runtime role and Row Level
/// Security: after every step of a random run of the writes that move a bucket's winner, every
/// visible reading's flag equals <see cref="CanonicalGlucoseStream.Select"/> over the whole of its
/// bucket's visible readings and the tenant's devices. A device or attribution change requests the
/// tenant's history walk, which the run then performs, as the job would.
/// </summary>
[Trait("Category", "Integration")]
public class GlucoseStreamFlagEquivalenceTests(GlucoseStreamFlagEquivalenceTests.Database database)
    : IClassFixture<GlucoseStreamFlagEquivalenceTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;
        public ServiceProvider Provider { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("glucose_stream_flags");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
            Provider = TenantReaderData.BuildProvider(AppConnectionString);
        }

        public async Task DisposeAsync() => await Provider.DisposeAsync();
    }

    private static readonly IAuditContext Audit = SystemAuditContext.ForService("test");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EveryStepLeavesTheFlagsEqualToTheRuleOverWholeBuckets(int seed)
    {
        var tenant = await SeedTenantAsync();
        var run = await Run.StartAsync(this, tenant, new Random(seed));

        for (var step = 0; step < 120; step++)
        {
            var operation = await run.StepAsync();
            (await MismatchesAsync(tenant)).Should().BeEmpty($"step {step} ({operation}) of seed {seed}");
        }

        run.Exercised.Should().Contain(
            ["written", "moved", "deleted", "deleted by range", "re-sent in bulk", "re-sent singly", "restored",
             "linked", "repicked", "reranked", "back-stamped", "deduplicated"],
            "the run must reach every write that moves a bucket's winner");
    }

    /// <summary>
    /// Two uploaders of different streams write into one bucket at once. Neither sees the other's
    /// uncommitted reading, so without the stream lock both would keep their own.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritersOfTwoStreamsIntoOneBucket_LeaveOneStreamShown()
    {
        var tenant = await SeedTenantAsync();
        var at = Bucket(DateTime.UtcNow.AddHours(-1));
        var first = Reading(tenant, at, "a-cgm");
        var second = Reading(tenant, at.AddMinutes(1), "b-cgm");

        await using var a = Context(tenant);
        await using var aTransaction = await a.Database.BeginTransactionAsync();
        a.SensorGlucose.Add(first);
        await a.SaveChangesAsync();
        await GlucoseStreamFlags.RecomputeAsync(a, [first.Timestamp], default);

        var writer = Task.Run(async () =>
        {
            await using var b = Context(tenant);
            await using var bTransaction = await b.Database.BeginTransactionAsync();
            b.SensorGlucose.Add(second);
            await b.SaveChangesAsync();
            await GlucoseStreamFlags.RecomputeAsync(b, [second.Timestamp], default);
            await bTransaction.CommitAsync();
        });
        (await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(1)))).Should().NotBe(writer,
            "the first writer holds the tenant's stream lock until it commits");

        await aTransaction.CommitAsync();
        await writer;

        (await ShownAsync(tenant)).Should().Equal(first.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// The daily duplicate audit repairs a copy's flag that drift left clear. Hiding the copy changes
    /// which streams its bucket holds, so the repair settles the bucket's stream flags with it.
    /// </summary>
    [Fact]
    public async Task AnAuditRepairOfAGlucoseDuplicate_LeavesTheStreamFlagsRight()
    {
        var tenant = await SeedTenantAsync();
        var at = Bucket(DateTime.UtcNow.AddHours(-2));
        var copy = Reading(tenant, at, "a-cgm");
        var primary = Reading(tenant, at.AddMinutes(1), "b-cgm");
        await using (var db = Context(tenant))
        {
            db.SensorGlucose.AddRange(copy, primary);
            await db.SaveChangesAsync();
            await DuplicateGroups.LinkAsync(db, RecordType.SensorGlucose, primary.Id, copy.Id);
        }

        (await ShownAsync(tenant)).Should().Equal(primary.Id);

        // Drift: the copy reads as visible, and the stream flags agree with that (it wins the bucket).
        await ExecuteAsync(tenant,
            $"UPDATE sensor_glucose SET is_duplicate = false WHERE id = '{copy.Id}'; " +
            $"UPDATE sensor_glucose SET is_secondary_stream = (id = '{primary.Id}');");
        (await ShownAsync(tenant)).Should().Equal(copy.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();

        await using (var scope = Scope(tenant))
        {
            var drift = await scope.ServiceProvider.GetRequiredService<IDeduplicationService>().AuditDuplicateFlagsAsync();
            drift.Should().ContainSingle(d => d.RecordType == RecordType.SensorGlucose).Which.Repaired.Should().Be(1);
        }

        (await ShownAsync(tenant)).Should().Equal(primary.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// A source delete wider than the inline limit hands its streams to the walk: its repoint
    /// promotes every copy chunk by chunk, and none of those chunks recomputes a bucket or takes the
    /// stream lock the tenant's ingest waits on.
    /// </summary>
    [Fact]
    public async Task AWideDeleteRecomputesNothingInline_AndTheWalkSettlesIt()
    {
        var tenant = await SeedTenantAsync();
        var start = Bucket(DateTime.UtcNow.AddDays(-60));
        var pairs = GlucoseStreamFlags.InlineRowLimit + 1;
        await ExecuteAsync(tenant, $"""
            INSERT INTO sensor_glucose (id, tenant_id, timestamp, mgdl, data_source, device, is_duplicate, sys_created_at, sys_updated_at)
            SELECT md5('{tenant}' || g || v.src)::uuid, '{tenant}', '{start:O}'::timestamptz + make_interval(mins => g * 5),
                   120, v.src, 'sensor', v.src = 'xdrip', now(), now()
            FROM generate_series(1, {pairs}) g, (VALUES ('dexcom'), ('xdrip')) v(src);
            INSERT INTO linked_records (id, tenant_id, canonical_id, record_type, record_id, source_timestamp, data_source, is_primary, sys_created_at)
            SELECT gen_random_uuid(), '{tenant}', md5('{tenant}' || g)::uuid, '{RecordTypeKeys.Key(RecordType.SensorGlucose)}',
                   md5('{tenant}' || g || v.src)::uuid, 0, v.src, v.src = 'dexcom', now()
            FROM generate_series(1, {pairs}) g, (VALUES ('dexcom'), ('xdrip')) v(src);
            """);

        var statements = new StreamStatementCounter();
        await using (var db = Context(tenant, statements))
        {
            var deleted = await db.AuditedSoftDeleteWithEntitiesAsync(
                db.SensorGlucose.Where(e => e.DataSource == "dexcom"), Audit, "test", default, DuplicateDelete.PromoteSurvivor);
            deleted.Count.Should().Be(pairs);
            deleted.Promoted.Should().HaveCount(pairs);
        }

        statements.Count.Should().Be(0, "a delete over the inline limit leaves every bucket to the walk");
        (await WalkRequestedAsync(tenant)).Should().BeTrue();

        await using (var scope = Scope(tenant))
            await Repository(scope).RecomputeStreamsAsync();
        (await MismatchesAsync(tenant)).Should().BeEmpty();
        (await ShownAsync(tenant)).Should().HaveCount(pairs);
    }

    /// <summary>Counts the statements a stream recompute makes.</summary>
    private sealed class StreamStatementCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Seen(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Seen(command);
            return ValueTask.FromResult(result);
        }

        /// <summary>A stream lock, or a write of the flag: every recompute takes the one and may make the other.</summary>
        private void Seen(DbCommand command)
        {
            var streamLock = command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is int lockClass && lockClass == GlucoseStreamFlags.StreamLockClass);
            var flagWrite = command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("is_secondary_stream =", StringComparison.Ordinal);
            if (streamLock || flagWrite)
                Interlocked.Increment(ref _count);
        }
    }

    /// <summary>
    /// A merge of glucose groups takes the stream lock before it re-points any link, the order a
    /// soft delete's repoint takes them in: while it waits for the lock, it holds none of the links.
    /// </summary>
    [Fact]
    public async Task AGlucoseMergeWaitsForTheStreamLockBeforeItLocksALink()
    {
        var tenant = await SeedTenantAsync();
        var at = Bucket(DateTime.UtcNow.AddHours(-3));
        var first = Reading(tenant, at, "xdrip");
        var second = Reading(tenant, at.AddSeconds(10), "dexcom-connector");
        await using (var db = Context(tenant))
        {
            db.SensorGlucose.AddRange(first, second);
            foreach (var reading in new[] { first, second })
            {
                db.LinkedRecords.Add(new()
                {
                    Id = Guid.CreateVersion7(), TenantId = tenant, CanonicalId = Guid.CreateVersion7(),
                    RecordType = RecordTypeKeys.Key(RecordType.SensorGlucose), RecordId = reading.Id,
                    SourceTimestamp = reading.Timestamp.ToMills(), DataSource = reading.DataSource!, IsPrimary = true,
                    SysCreatedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        await using var holder = Context(tenant);
        await using var held = await holder.Database.BeginTransactionAsync();
        await GlucoseStreamFlags.LockAsync(holder, default);

        await using var scope = Scope(tenant);
        var dedup = (DeduplicationService)scope.ServiceProvider.GetRequiredService<IDeduplicationService>();
        var merge = Task.Run(() => dedup.MergeDuplicateGroupsAsync(RecordType.SensorGlucose, null, default));
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, merge);

        (await LinksFreeAsync(tenant, first.Id, second.Id)).Should().BeTrue("the merge has written no link yet");

        await held.CommitAsync();
        (await merge.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(1);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>Whether another transaction can lock the records' links at once.</summary>
    private async Task<bool> LinksFreeAsync(Guid tenant, params Guid[] records)
    {
        await using var conn = new NpgsqlConnection(database.AppConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM linked_records WHERE record_id = ANY(@ids) FOR UPDATE NOWAIT", conn);
        cmd.Parameters.AddWithValue("ids", records);
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return false;
        }
    }

    /// <summary>
    /// One recompute over thousands of scattered bucket runs, as a v1 upload of readings hours apart
    /// makes: the runs go to the database as bound arrays, so the statement is the same whatever
    /// their number, and every bucket is decided.
    /// </summary>
    [Fact]
    public async Task ARecomputeOverThousandsOfScatteredRuns_DecidesEveryBucket()
    {
        var tenant = await SeedTenantAsync();
        var start = Bucket(DateTime.UtcNow.AddDays(-400));
        const int buckets = 2_100;
        var readings = Enumerable.Range(0, buckets).SelectMany(i => new[]
        {
            Model(start.AddHours(2 * i), "a-cgm", "sensor", 110),
            Model(start.AddHours(2 * i).AddMinutes(1), "b-cgm", "sensor", 180),
        }).ToList();

        await using (var scope = Scope(tenant))
            await Repository(scope).BulkCreateAsync(readings, WriteOrigin.Backfill);

        (await MismatchesAsync(tenant)).Should().BeEmpty();
        (await ShownAsync(tenant)).Should().HaveCount(buckets);
    }

    /// <summary>
    /// A connector re-sends a bucket's winning reading under its sync key with a corrected time in
    /// another bucket. The bucket it left is decided again, so the reading it outranked there shows.
    /// </summary>
    [Fact]
    public async Task ABulkReSendThatMovesAWinner_DecidesTheBucketItLeft()
    {
        var tenant = await SeedTenantAsync();
        var at = Bucket(DateTime.UtcNow.AddHours(-3));
        await using var scope = Scope(tenant);
        var repository = Repository(scope);
        var winner = Model(at, "a-cgm", "sensor", 110);
        winner.SyncIdentifier = "a-1";
        var loser = Model(at.AddMinutes(1), "b-cgm", "sensor", 180);
        await repository.BulkCreateAsync([winner, loser], WriteOrigin.Live);
        (await ShownAsync(tenant)).Should().Equal(winner.Id);

        var moved = Model(at.AddMinutes(30), "a-cgm", "sensor", 110);
        moved.SyncIdentifier = "a-1";
        await repository.BulkCreateAsync([moved], WriteOrigin.Live);

        (await ShownAsync(tenant)).Should().BeEquivalentTo([winner.Id, loser.Id]);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// Outside demo mode a single-stream client is shown no demo or test reading, in the query
    /// itself; each ephemeral source still wins its own buckets for a canonical read.
    /// </summary>
    [Fact]
    public async Task ACanonicalReadWithoutEphemeralSources_LeavesThemOutInTheQuery()
    {
        var tenant = await SeedTenantAsync();
        var at = Bucket(DateTime.UtcNow.AddHours(-3));
        await using var scope = Scope(tenant);
        var repository = Repository(scope);
        var real = Model(at, "xdrip", "phone", 110);
        var demo = Model(at.AddMinutes(1), Core.Constants.DataSources.DemoService, "demo", 150);
        var testing = Model(at.AddMinutes(10), Core.Constants.DataSources.Testing, "test", 170);
        var unnamed = Model(at.AddMinutes(20), null!, "phone", 130);
        await repository.BulkCreateAsync([real, demo, testing, unnamed], WriteOrigin.Live);

        async Task<IEnumerable<Guid>> Read(GlucoseStreams streams) =>
            (await repository.GetAsync(at, at.AddHours(1), null, null, 100, descending: false, streams: streams)).Select(r => r.Id);

        (await Read(GlucoseStreams.CanonicalExcludingEphemeral)).Should().Equal(real.Id, unnamed.Id);
        (await Read(GlucoseStreams.Canonical)).Should().Equal(real.Id, demo.Id, testing.Id, unnamed.Id);
    }

    [Fact]
    public async Task AChangeWiderThanTheInlineLimit_RequestsTheWalk_AndWritesNothing()
    {
        var tenant = await SeedTenantAsync();
        await using var db = Context(tenant);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var ids = Enumerable.Range(0, GlucoseStreamFlags.InlineRowLimit + 1).Select(_ => Guid.NewGuid()).ToList();
            await GlucoseStreamFlags.AfterRowsChangedAsync(db, ids, default);
            await transaction.CommitAsync();
        }

        (await WalkRequestedAsync(tenant)).Should().BeTrue();
    }

    /// <summary>
    /// The walk pages by rows but decides whole buckets: a page ends before the bucket it would cut,
    /// and a bucket wider than a page is read whole.
    /// </summary>
    [Fact]
    public async Task TheHistoryWalkRepairsEveryFlag_AcrossPagesAndBucketsWiderThanAPage()
    {
        var tenant = await SeedTenantAsync();
        var start = Bucket(DateTime.UtcNow.AddDays(-1));
        var rows = new List<SensorGlucoseEntity>();
        for (var bucket = 0; bucket < 6; bucket++)
        {
            foreach (var (source, count) in new[] { ("a-cgm", 1), ("b-cgm", bucket == 2 ? 7 : 2) })
            {
                for (var i = 0; i < count; i++)
                    rows.Add(Reading(tenant, start.AddMinutes(bucket * 5).AddSeconds(i * 20 + (source == "a-cgm" ? 0 : 5)), source));
            }
        }

        await using (var db = Context(tenant))
        {
            db.SensorGlucose.AddRange(rows);
            await db.SaveChangesAsync();
        }

        await ExecuteAsync(tenant, "UPDATE sensor_glucose SET is_secondary_stream = NOT (data_source = 'b-cgm')");
        (await MismatchesAsync(tenant)).Should().NotBeEmpty();

        await using (var db = Context(tenant))
        {
            var changed = await GlucoseStreamFlags.RecomputeHistoryAsync(db, pageSize: 3, default);
            changed.Should().Be(rows.Count);
            (await GlucoseStreamFlags.RecomputeHistoryAsync(db, pageSize: 3, default)).Should().Be(0);
        }

        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARankingEditRequestsTheWalk_AndAnEditOfAnythingElseDoesNot()
    {
        var tenant = await SeedTenantAsync();
        await using var scope = Scope(tenant);
        var devices = new PatientDeviceRepository(Factory(scope), NullLogger<PatientDeviceRepository>.Instance);
        var cgm = await devices.CreateAsync(Cgm(rank: 1), WriteOrigin.Live);
        var pump = await devices.CreateAsync(
            new PatientDevice { DeviceCategory = DeviceCategory.InsulinPump, Manufacturer = "Test", Model = "Pump" },
            WriteOrigin.Live);
        await ClearWalkRequestAsync(tenant);

        cgm.Notes = "renamed";
        await devices.UpdateAsync(cgm.Id, cgm, WriteOrigin.Live);
        await devices.ReorderAsync([(pump.Id, 5)], WriteOrigin.Live);
        (await WalkRequestedAsync(tenant)).Should().BeFalse("a note or a pump's rank decides no glucose stream");

        await devices.ReorderAsync([(cgm.Id, 2)], WriteOrigin.Live);
        (await WalkRequestedAsync(tenant)).Should().BeTrue();
    }

    /// <summary>
    /// A clamped member's late upload lands in a bucket older than its window; the bucket is decided
    /// over every reading in it, not over the part the clamp shows.
    /// </summary>
    [Fact]
    public async Task AClampedMembersOldWriteIsDecidedOverTheWholeBucket()
    {
        var tenant = await SeedTenantAsync();
        var at = Bucket(DateTime.UtcNow.AddDays(-3));
        await using (var db = Context(tenant))
        {
            db.SensorGlucose.Add(Reading(tenant, at, "a-cgm"));
            await db.SaveChangesAsync();
        }

        await using (var scope = Scope(tenant))
        {
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
            await Repository(scope).CreateAsync(Model(at.AddMinutes(1), "b-cgm", device: "sensor", mgdl: 180), WriteOrigin.Live);
        }

        (await MismatchesAsync(tenant)).Should().BeEmpty();
        (await ShownAsync(tenant)).Should().ContainSingle();
    }

    /// <summary>
    /// The legacy realtime feed shows a live write as a canonical read does: a copy dedup hides and a
    /// reading another stream outranks in its bucket are not broadcast.
    /// </summary>
    [Fact]
    public async Task TheLegacyFeedLeavesOutDuplicateCopiesAndLosingStreams()
    {
        var tenant = await SeedTenantAsync();
        var sink = new CapturingSink();
        var at = Bucket(DateTime.UtcNow.AddMinutes(-30));
        await using var scope = Scope(tenant);
        var devices = new PatientDeviceRepository(Factory(scope), NullLogger<PatientDeviceRepository>.Instance);
        var cgm = await devices.CreateAsync(Cgm(rank: 1), WriteOrigin.Live);
        var repository = Repository(scope, sink);

        // One reading from the registered CGM, uploaded by two apps: dedup hides one copy.
        await repository.BulkCreateAsync(
        [
            Model(at, "xdrip", "phone", 110, cgm.Id),
            Model(at.AddSeconds(5), "dexcom-connector", "share", 110, cgm.Id),
        ], WriteOrigin.Live);
        sink.Created.Should().ContainSingle("the second copy is a duplicate");

        // An unregistered stream's reading in the same bucket loses to the registered CGM.
        sink.Created.Clear();
        await repository.CreateAsync(Model(at.AddMinutes(1), "libre-connector", "linkup", 200), WriteOrigin.Live);
        sink.Created.Should().BeEmpty("the registered CGM wins the bucket");
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    private sealed class CapturingSink : IDataEventSink<Entry>
    {
        public List<Entry> Created { get; } = [];

        public Task OnCreatedAsync(IReadOnlyList<Entry> items, CancellationToken ct = default)
        {
            Created.AddRange(items);
            return Task.CompletedTask;
        }
    }

    private sealed class Run
    {
        private static readonly double[] Values = [100, 120, 140];

        private readonly GlucoseStreamFlagEquivalenceTests _test;
        private readonly Guid _tenant;
        private readonly Random _random;
        private readonly DateTime _base = Bucket(DateTime.UtcNow.AddDays(-2));
        private readonly List<(string Source, string Device, Func<Guid?> PatientDevice)> _streams = [];
        private PatientDevice _first = null!;
        private PatientDevice _second = null!;

        public HashSet<string> Exercised { get; } = [];

        private Run(GlucoseStreamFlagEquivalenceTests test, Guid tenant, Random random)
        {
            _test = test;
            _tenant = tenant;
            _random = random;
        }

        public static async Task<Run> StartAsync(GlucoseStreamFlagEquivalenceTests test, Guid tenant, Random random)
        {
            var run = new Run(test, tenant, random);
            await using var scope = test.Scope(tenant);
            var devices = Devices(scope);
            run._first = await devices.CreateAsync(Cgm(rank: 1), WriteOrigin.Live);
            run._second = await devices.CreateAsync(Cgm(rank: 2), WriteOrigin.Live);
            run._streams.AddRange(
            [
                ("xdrip", "phone", () => run._first.Id),
                ("dexcom-connector", "share", () => run._second.Id),
                ("xdrip", "phone", () => null),
                ("libre-connector", "linkup", () => null),
                (Core.Constants.DataSources.DemoService, "demo", () => null),
            ]);
            return run;
        }

        public async Task<string> StepAsync()
        {
            var roll = _random.Next(100);
            return roll switch
            {
                < 25 => await WriteAsync(),
                < 31 => await MoveAsync(),
                < 37 => await ResendAsync(),
                < 44 => await DeleteAsync(),
                < 50 => await DeleteRangeAsync(),
                < 57 => await RestoreAsync(),
                < 66 => await LinkAsync(),
                < 74 => await RepickAsync(),
                < 82 => await RerankAsync(),
                < 90 => await BackStampAsync(),
                _ => await WriteAsync(),
            };
        }

        /// <summary>A new reading, with a sync key half the time so a later step can re-send it.</summary>
        private SensorGlucose NewReading()
        {
            var (source, device, deviceId) = _streams[_random.Next(_streams.Count)];
            var reading = Model(_base.AddSeconds(_random.Next(0, 45 * 60)), source, device, Values[_random.Next(Values.Length)], deviceId());
            if (_random.Next(2) == 0)
                reading.SyncIdentifier = $"sync-{reading.Id:N}";
            return reading;
        }

        /// <summary>
        /// A source re-sends a stored reading under its sync key with a moved timestamp, which the
        /// write applies to the stored row: through the bulk path or the single upsert.
        /// </summary>
        private async Task<string> ResendAsync()
        {
            if (await _test.SomeAsync(_tenant,
                    "SELECT id::text FROM sensor_glucose WHERE deleted_at IS NULL AND sync_identifier IS NOT NULL",
                    max: int.MaxValue) is not { Count: > 0 } keyed)
                return await WriteAsync();

            await using var scope = _test.Scope(_tenant);
            var repository = Repository(scope);
            var resent = (await repository.GetByIdAsync(keyed[_random.Next(keyed.Count)]))!;
            resent.Timestamp = _base.AddSeconds(_random.Next(0, 45 * 60));
            if (_random.Next(2) == 0)
            {
                await repository.BulkCreateAsync([resent], WriteOrigin.Live);
                return Done("re-sent in bulk");
            }

            await repository.CreateAsync(resent, WriteOrigin.Live);
            return Done("re-sent singly");
        }

        /// <summary>
        /// A time-range delete, as a v1 entries delete makes one: the copies of the deleted primaries
        /// are promoted, which the same transaction settles.
        /// </summary>
        private async Task<string> DeleteRangeAsync()
        {
            var from = _base.AddMinutes(_random.Next(0, 45));
            await using var scope = _test.Scope(_tenant);
            await Repository(scope).DeleteByTimeRangeAsync(from, from.AddMinutes(_random.Next(1, 8)), WriteOrigin.Live);
            return Done("deleted by range");
        }

        private async Task<string> WriteAsync()
        {
            await using var scope = _test.Scope(_tenant);
            var repository = Repository(scope);
            var linksBefore = await _test.CountAsync(_tenant, "SELECT count(*) FROM linked_records");
            if (_random.Next(3) == 0)
                await repository.CreateAsync(NewReading(), WriteOrigin.Live);
            else
                await repository.BulkCreateAsync(Enumerable.Range(0, _random.Next(1, 7)).Select(_ => NewReading()).ToList(), WriteOrigin.Live);
            if (await _test.CountAsync(_tenant, "SELECT count(*) FROM linked_records") > linksBefore)
                Exercised.Add("deduplicated");
            return Done("written");
        }

        private async Task<string> MoveAsync()
        {
            if (await _test.SomeAsync(_tenant, "SELECT id::text FROM sensor_glucose WHERE deleted_at IS NULL") is not [var id, ..])
                return await WriteAsync();

            await using var scope = _test.Scope(_tenant);
            var repository = Repository(scope);
            var stored = (await repository.GetByIdAsync(id))!;
            var moved = NewReading();
            if (_random.Next(2) == 0)
                stored.Timestamp = moved.Timestamp;
            else
                (stored.DataSource, stored.Device, stored.PatientDeviceId) = (moved.DataSource, moved.Device, moved.PatientDeviceId);
            await repository.UpdateAsync(id, stored, WriteOrigin.Live);
            return Done("moved");
        }

        private async Task<string> DeleteAsync()
        {
            if (await _test.SomeAsync(_tenant, "SELECT id::text FROM sensor_glucose WHERE deleted_at IS NULL") is not [var id, ..])
                return await WriteAsync();

            await using var scope = _test.Scope(_tenant);
            await Repository(scope).DeleteAsync(id, WriteOrigin.Live);
            return Done("deleted");
        }

        private async Task<string> RestoreAsync()
        {
            if (await _test.SomeAsync(_tenant, "SELECT id::text FROM sensor_glucose WHERE deleted_at IS NOT NULL") is not [var id, ..])
                return await DeleteAsync();

            await using var scope = _test.Scope(_tenant);
            await Repository(scope).RestoreAsync(id, WriteOrigin.Live);
            return Done("restored");
        }

        /// <summary>
        /// Joins one live reading's group of one to another's as its copy, as a merge of two groups
        /// dedup formed apart would: every inserted reading is already linked, as the primary of a
        /// group of its own when nothing matched it.
        /// </summary>
        private async Task<string> LinkAsync()
        {
            var alone = await _test.SomeAsync(_tenant, """
                SELECT l.record_id::text FROM linked_records l
                JOIN sensor_glucose g ON g.id = l.record_id
                WHERE g.deleted_at IS NULL AND l.is_primary
                  AND NOT EXISTS (SELECT 1 FROM linked_records o WHERE o.canonical_id = l.canonical_id AND o.id <> l.id)
                """, max: 2);
            if (alone.Count < 2)
                return await WriteAsync();

            var (primary, copy) = _random.Next(2) == 0 ? (alone[0], alone[1]) : (alone[1], alone[0]);
            await _test.ExecuteAsync(_tenant, $"""
                UPDATE linked_records SET canonical_id = (SELECT canonical_id FROM linked_records WHERE record_id = '{primary}')
                WHERE record_id = '{copy}'
                """);
            await using var scope = _test.Scope(_tenant);
            await using var db = await Factory(scope).CreateAsync();
            await db.ExecuteInTransactionAsync(token => DuplicateGroupPrimaries.SetPrimariesAsync(
                db, RecordType.SensorGlucose, [new(copy, false)], token));
            return Done("linked");
        }

        private async Task<string> RepickAsync()
        {
            var copies = await _test.SomeAsync(_tenant, "SELECT record_id::text FROM linked_records WHERE NOT is_primary", max: 1);
            if (copies is not [var copy])
                return await LinkAsync();

            var primaries = await _test.SomeAsync(_tenant, $"""
                SELECT p.record_id::text FROM linked_records p
                JOIN linked_records c ON c.canonical_id = p.canonical_id
                WHERE p.is_primary AND c.record_id = '{copy}'
                """, max: 1);
            await using var scope = _test.Scope(_tenant);
            await using var db = await Factory(scope).CreateAsync();
            await db.ExecuteInTransactionAsync(token => DuplicateGroupPrimaries.SetPrimariesAsync(
                db, RecordType.SensorGlucose, [new(copy, true), .. primaries.Select(p => new PrimaryChange(p, false))], token));
            return Done("repicked");
        }

        private async Task<string> RerankAsync()
        {
            await using (var scope = _test.Scope(_tenant))
            {
                (_first.Rank, _second.Rank) = (_second.Rank, _first.Rank);
                await Devices(scope).ReorderAsync([(_first.Id, _first.Rank!.Value), (_second.Id, _second.Rank!.Value)], WriteOrigin.Live);
            }

            await WalkAsync();
            return Done("reranked");
        }

        private async Task<string> BackStampAsync()
        {
            var unattributed = await _test.SomeAsync(_tenant,
                "SELECT id::text FROM sensor_glucose WHERE deleted_at IS NULL AND patient_device_id IS NULL");
            if (unattributed.Count == 0)
                return await WriteAsync();

            await using (var scope = _test.Scope(_tenant))
            {
                var device = _random.Next(2) == 0 ? _first : _second;
                await Repository(scope).SetPatientDeviceIdsAsync(unattributed.ToDictionary(id => id, _ => device.Id));
            }

            await WalkAsync();
            return Done("back-stamped");
        }

        /// <summary>The requested history walk, run as the job runs it.</summary>
        private async Task WalkAsync()
        {
            (await _test.WalkRequestedAsync(_tenant)).Should().BeTrue("the change requests the tenant's walk");
            await _test.ClearWalkRequestAsync(_tenant);
            await using var scope = _test.Scope(_tenant);
            await Repository(scope).RecomputeStreamsAsync();
        }

        private string Done(string operation)
        {
            Exercised.Add(operation);
            return operation;
        }
    }

    /// <summary>
    /// Every visible reading whose stored flag differs from the rule over the tenant's visible
    /// readings and live devices. Selection over all of them at once is selection per whole bucket.
    /// </summary>
    private async Task<List<string>> MismatchesAsync(Guid tenant)
    {
        await using var scope = Scope(tenant);
        await using var db = await Factory(scope).CreateAsync();
        var rows = await db.SensorGlucose.AsNoTracking().ExcludeDuplicates().ToListAsync();
        var devices = (await db.PatientDevices.AsNoTracking().ToListAsync()).Select(PatientDeviceMapper.ToDomainModel).ToList();
        var kept = CanonicalGlucoseStream.Select(rows.Select(SensorGlucoseMapper.ToDomainModel).ToList(), devices)
            .Select(r => r.Id)
            .ToHashSet();
        return rows
            .Where(r => r.IsSecondaryStream == kept.Contains(r.Id))
            .Select(r => $"{r.Id} {r.Timestamp:O} {r.DataSource}/{r.Device}/{r.PatientDeviceId} secondary={r.IsSecondaryStream}")
            .ToList();
    }

    private async Task<List<Guid>> ShownAsync(Guid tenant) =>
        [.. (await SomeAsync(tenant, "SELECT id::text FROM sensor_glucose WHERE deleted_at IS NULL AND NOT is_duplicate AND NOT is_secondary_stream", max: int.MaxValue))];

    /// <summary>
    /// Whether the tenant's walk is due: a request is stamped from the API's clock, which the
    /// database's <c>now()</c> need not agree with, and the row is cleared after each check.
    /// </summary>
    private async Task<bool> WalkRequestedAsync(Guid tenant) =>
        await CountAsync(tenant, $"""
            SELECT count(*) FROM job_ledger
            WHERE job = '{ISensorGlucoseRepository.RecomputeStreamsJob}' AND tenant_id = '{tenant}' AND next_due_at IS NOT NULL
            """) > 0;

    private async Task ClearWalkRequestAsync(Guid tenant)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM job_ledger WHERE job = '{ISensorGlucoseRepository.RecomputeStreamsJob}' AND tenant_id = '{tenant}'", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<Guid>> SomeAsync(Guid tenant, string sql, int max = 3)
    {
        var all = new List<Guid>();
        await using var conn = new NpgsqlConnection(database.AppConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            all.Add(Guid.Parse(reader.GetString(0)));
        return all.Order().Take(max).ToList();
    }

    private async Task<long> CountAsync(Guid tenant, string sql)
    {
        await using var conn = new NpgsqlConnection(database.AppConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(Guid tenant, string sql)
    {
        await using var conn = new NpgsqlConnection(database.AppConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tenant);
        await using (var cmd = new NpgsqlCommand(sql, conn))
            await cmd.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task SetTenantAsync(NpgsqlConnection conn, Guid tenant)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.current_tenant_id', @t, true)", conn);
        cmd.Parameters.AddWithValue("t", tenant.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private NocturneDbContext Context(Guid tenant, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors([new TenantConnectionInterceptor(), .. interceptors])
            .Options) { TenantId = tenant };

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = database.Provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, tenant);
        return scope;
    }

    private static INocturneDbContextFactory Factory(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

    private static SensorGlucoseRepository Repository(AsyncServiceScope scope, IDataEventSink<Entry>? sink = null) =>
        new(Factory(scope), scope.ServiceProvider.GetRequiredService<IDeduplicationService>(), Audit,
            NullLogger<SensorGlucoseRepository>.Instance, entrySink: sink);

    private static PatientDeviceRepository Devices(AsyncServiceScope scope) =>
        new(Factory(scope), NullLogger<PatientDeviceRepository>.Instance);

    private static PatientDevice Cgm(int rank) => new()
    {
        DeviceCategory = DeviceCategory.CGM, Manufacturer = "Test", Model = $"CGM {rank}", Rank = rank,
    };

    private static SensorGlucose Model(DateTime at, string source, string device, double mgdl, Guid? patientDeviceId = null) => new()
    {
        Id = Guid.CreateVersion7(), Timestamp = at, Mgdl = mgdl, DataSource = source, Device = device,
        PatientDeviceId = patientDeviceId,
    };

    private static SensorGlucoseEntity Reading(Guid tenant, DateTime at, string source) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, Mgdl = 120, DataSource = source, Device = "sensor",
    };

    private static DateTime Bucket(DateTime at) =>
        new(at.Ticks - at.Ticks % CanonicalGlucoseStream.BucketSize.Ticks, DateTimeKind.Utc);

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'glucose-streams', true, now(), now())
            """;
        cmd.Parameters.AddWithValue("id", tenant);
        cmd.Parameters.AddWithValue("slug", $"streams-{tenant:N}");
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }
}
