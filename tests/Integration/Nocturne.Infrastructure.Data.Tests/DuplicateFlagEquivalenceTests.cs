using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <c>is_duplicate</c> agrees with <c>linked_records</c> on PostgreSQL under the runtime role and
/// Row Level Security, whatever the deduplication module does: after every step of a random run of
/// its operations, every linked record table flags exactly the rows a non-primary link names, which
/// <see cref="ReadVisibilityFilter"/> hides.
/// </summary>
[Trait("Category", "Integration")]
public class DuplicateFlagEquivalenceTests(DuplicateFlagEquivalenceTests.Database database)
    : IClassFixture<DuplicateFlagEquivalenceTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;
        public ServiceProvider Provider { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("duplicate_flag_equivalence");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
            Provider = TenantReaderData.BuildProvider(AppConnectionString);
        }

        public async Task DisposeAsync() => await Provider.DisposeAsync();
    }

    private static readonly (string Table, RecordType Type)[] Tables =
    [
        ("sensor_glucose", RecordType.SensorGlucose),
        ("state_spans", RecordType.StateSpan),
        ("temp_basals", RecordType.TempBasal),
        ("boluses", RecordType.Bolus),
        ("carb_intakes", RecordType.CarbIntake),
        ("bg_checks", RecordType.BGCheck),
        ("device_events", RecordType.DeviceEvent),
        ("notes", RecordType.Note),
        ("bolus_calculations", RecordType.BolusCalculation),
    ];

    private static readonly string[] Sources = ["mylife-connector", "glooko-connector", "nightscout-connector"];

    private static readonly IAuditContext Audit = SystemAuditContext.ForService("test");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EveryStepLeavesTheFlagsEqualToTheAntiJoin(int seed)
    {
        var tenant = await SeedTenantAsync();
        var run = new Run(this, tenant, new Random(seed));

        for (var step = 0; step < 150; step++)
        {
            var operation = await run.StepAsync();
            (await MismatchesAsync(tenant)).Should().BeEmpty($"step {step} ({operation}) of seed {seed}");
            (await GroupsWithTwoPrimariesAsync(tenant)).Should().BeEmpty($"step {step} ({operation}) of seed {seed}");
        }

        run.Exercised.Should().Contain(["duplicate linked", "merged", "promoted on delete", "restored"],
            "the run must reach every writer that moves a primary");
    }

    /// <summary>
    /// A history-clamped member deletes the primary of a group whose only other copy ended before its
    /// window. The repick promotes that hidden copy, whose flag the clamp would otherwise leave set.
    /// </summary>
    [Fact]
    public async Task AClampedCallersDeleteUnflagsAPromotedCopyOutsideItsWindow()
    {
        var tenant = await SeedTenantAsync();
        var start = DateTime.UtcNow.AddDays(-4);
        var hidden = Span(tenant, start, end: DateTime.UtcNow.AddDays(-2), "mylife-connector");
        var open = Span(tenant, start.AddSeconds(10), end: null, "glooko-connector");
        await IngestAsync(tenant, RecordType.StateSpan, [hidden, open]);
        (await FlaggedAsync(tenant, "state_spans")).Should().Equal(hidden.Id);

        await using (var scope = Scope(tenant))
        {
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
            await using var db = await Factory(scope).CreateAsync();
            await db.AuditedSoftDeleteAsync(
                db.StateSpans.Where(s => s.Id == open.Id), Audit, "test", default, DuplicateDelete.PromoteSurvivor);
        }

        (await FlaggedAsync(tenant, "state_spans")).Should().Equal(open.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// A writer whose planned promotion is already in place changes no link, yet a second writer that
    /// demotes the same record must still wait for it: otherwise the first, resuming after the second
    /// commits, writes the flag of a link it no longer describes.
    /// </summary>
    [Fact]
    public async Task AWriterWhosePlanIsAlreadyInPlaceStillHoldsTheLinkAgainstAConcurrentDemotion()
    {
        var tenant = await SeedTenantAsync();
        var at = DateTime.UtcNow.AddHours(-1);
        CarbIntakeEntity Carb(string source, DateTime when) => new()
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = when, DataSource = source, Carbs = 40,
        };
        var primary = Carb("mylife-connector", at);
        var copy = Carb("glooko-connector", at.AddSeconds(10));
        await IngestAsync(tenant, RecordType.CarbIntake, [primary, copy]);
        await SetPrimariesAsync(Context(tenant), [new(copy.Id, true), new(primary.Id, false)]);

        var pause = new PauseBeforeFlagWrite();
        var planned = Task.Run(() => SetPrimariesAsync(Context(tenant, pause), [new(copy.Id, true)]));
        await pause.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        var demoting = Task.Run(() => SetPrimariesAsync(Context(tenant), [new(copy.Id, false), new(primary.Id, true)]));
        (await Task.WhenAny(demoting, Task.Delay(TimeSpan.FromSeconds(1)))).Should().NotBe(demoting,
            "the first writer holds the copy's link until it commits");

        pause.Release();
        await Task.WhenAll(planned, demoting);

        (await FlaggedAsync(tenant, "carb_intakes")).Should().Equal(copy.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    private static async Task SetPrimariesAsync(NocturneDbContext db, PrimaryChange[] changes)
    {
        await using (db)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await DuplicateGroupPrimaries.SetPrimariesAsync(db, RecordType.CarbIntake, changes, default);
            await transaction.CommitAsync();
        }
    }

    private NocturneDbContext Context(Guid tenant, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors([new TenantConnectionInterceptor(), .. interceptors])
            .Options) { TenantId = tenant };

    /// <summary>Holds the first flag statement until <see cref="Release"/>, its links written.</summary>
    private sealed class PauseBeforeFlagWrite : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("is_duplicate", StringComparison.Ordinal)
                && _reached.TrySetResult())
            {
                await _released.Task;
            }

            return result;
        }
    }

    [Fact]
    public async Task ReSendingADuplicateThroughTheV4WritePathsKeepsItFlagged()
    {
        var tenant = await SeedTenantAsync();
        var at = DateTime.UtcNow.AddHours(-2);
        await using var scope = Scope(tenant);
        var repository = new CarbIntakeRepository(
            Factory(scope), scope.ServiceProvider.GetRequiredService<IDeduplicationService>(), Audit,
            NullLogger<CarbIntakeRepository>.Instance);

        await repository.BulkCreateAsync(
            [Carb("mylife-connector", "a", at, 50), Carb("glooko-connector", "b", at.AddSeconds(10), 50)], WriteOrigin.Live);
        var flagged = await FlaggedAsync(tenant, "carb_intakes");
        flagged.Should().ContainSingle();

        await repository.BulkUpsertAsync([Carb("glooko-connector", "b", at.AddSeconds(10), 51)], WriteOrigin.Live);
        (await FlaggedAsync(tenant, "carb_intakes")).Should().Equal(flagged, "a legacy-id re-send");

        await repository.BulkCreateAsync([Carb("glooko-connector", "b", at.AddSeconds(10), 52)], WriteOrigin.Live);
        (await FlaggedAsync(tenant, "carb_intakes")).Should().Equal(flagged, "a sync-identifier re-send");

        var stored = await repository.GetByLegacyIdAsync("b");
        stored!.Carbs = 53;
        await repository.UpdateAsync(stored.Id, stored, WriteOrigin.Live);
        (await FlaggedAsync(tenant, "carb_intakes")).Should().Equal(flagged, "an update by id");
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReSendingADuplicateStateSpanKeepsItFlagged()
    {
        var tenant = await SeedTenantAsync();
        var start = DateTime.UtcNow.AddHours(-2);
        await using var scope = Scope(tenant);
        var repository = new StateSpanRepository(
            Factory(scope), scope.ServiceProvider.GetRequiredService<IDeduplicationService>(), Audit,
            NullLogger<StateSpanRepository>.Instance);

        await repository.BulkUpsertAsync(
        [
            SpanModel("mylife-connector", "a", start, end: null),
            SpanModel("glooko-connector", "b", start.AddSeconds(10), end: null),
        ]);
        var flagged = await FlaggedAsync(tenant, "state_spans");
        flagged.Should().ContainSingle();

        var copy = await LegacySpanAsync(tenant, flagged[0]);
        await repository.BulkUpsertAsync([SpanModel(copy.Source!, copy.OriginalId!, copy.StartTimestamp, end: start.AddHours(1))]);
        (await FlaggedAsync(tenant, "state_spans")).Should().Equal(flagged, "a re-send by original id");

        await repository.UpdateStateSpanAsync(copy.OriginalId!, SpanModel(copy.Source!, copy.OriginalId!, copy.StartTimestamp, end: start.AddHours(2)));
        (await FlaggedAsync(tenant, "state_spans")).Should().Equal(flagged, "an update by id");
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>One random run of the module's operations over one tenant.</summary>
    private sealed class Run(DuplicateFlagEquivalenceTests test, Guid tenant, Random random)
    {
        private static readonly DateTime Base = DateTime.UtcNow.Date.AddDays(-3);

        public HashSet<string> Exercised { get; } = [];

        public async Task<string> StepAsync()
        {
            var type = Pick(RecordType.CarbIntake, RecordType.SensorGlucose, RecordType.StateSpan);
            var roll = random.Next(100);
            return roll switch
            {
                < 35 => await IngestAsync(type),
                < 42 => await EditAsync(),
                < 52 => await MergeAsync(type),
                < 55 => await RepairAsync(),
                < 65 => await DeleteAsync(type, DuplicateDelete.PromoteSurvivor),
                < 70 => await DeleteAsync(type, DuplicateDelete.EveryCopy),
                < 80 => await RestoreAsync(type),
                < 85 => await PurgeAsync(type),
                < 88 => await RetentionPurgeAsync(type),
                < 94 => await PruneAsync(),
                _ => await SweepOrphansAsync(type),
            };
        }

        private async Task<string> IngestAsync(RecordType type)
        {
            var count = random.Next(1, 5);
            var rows = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var slot = random.Next(6);
                var at = Base.AddMinutes(5 * slot).AddSeconds(random.Next(20));
                var source = Pick(Sources);
                var value = random.Next(5) == 0 ? 99 : 20 + slot % 3;
                rows.Add(type switch
                {
                    RecordType.CarbIntake => new CarbIntakeEntity
                    {
                        Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, DataSource = source, Carbs = value,
                    },
                    RecordType.SensorGlucose => new SensorGlucoseEntity
                    {
                        Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, DataSource = source, Mgdl = 100 + value,
                    },
                    _ => Span(tenant, at, random.Next(2) == 0 ? null : at.AddMinutes(30), source,
                        value == 99 ? "Manual" : "Automatic"),
                });
            }

            var result = await test.IngestAsync(tenant, type, rows);
            if (result.DuplicateGroups > 0)
                Exercised.Add("duplicate linked");
            return $"ingest {count} {type}";
        }

        private async Task<string> EditAsync()
        {
            await using var scope = test.Scope(tenant);
            await using var db = await Factory(scope).CreateAsync();
            var carbs = await db.CarbIntakes.ToListAsync();
            if (carbs.Count == 0)
                return "edit (none)";
            var carb = carbs[random.Next(carbs.Count)];
            carb.Carbs = carb.Carbs == 99 ? 20 : 99;
            await db.SaveChangesAsync();
            return "edit carb";
        }

        private async Task<string> MergeAsync(RecordType type)
        {
            await using var scope = test.Scope(tenant);
            var merged = await Dedup(scope).MergeDuplicateGroupsAsync(type, null, default);
            if (merged > 0)
                Exercised.Add("merged");
            return $"merge {type}: {merged}";
        }

        private async Task<string> RepairAsync()
        {
            await using var scope = test.Scope(tenant);
            return $"repair spans: {await Dedup(scope).RepairStateSpanPrimariesAsync()}";
        }

        private async Task<string> DeleteAsync(RecordType type, DuplicateDelete mode)
        {
            await using var scope = test.Scope(tenant);
            await using var db = await Factory(scope).CreateAsync();
            var ids = await SomeAsync(random.Next(2) == 0 ? LiveIds(db, type) : LiveGroupPrimaries(db, type));
            var (deleted, promoted) = type switch
            {
                RecordType.CarbIntake => await SoftDeleteAsync(db, db.CarbIntakes.Where(e => ids.Contains(e.Id)), mode),
                RecordType.SensorGlucose => await SoftDeleteAsync(db, db.SensorGlucose.Where(e => ids.Contains(e.Id)), mode),
                _ => await SoftDeleteAsync(db, db.StateSpans.Where(e => ids.Contains(e.Id)), mode),
            };
            if (promoted > 0)
                Exercised.Add("promoted on delete");
            return $"delete {mode} {deleted} {type}";
        }

        private static async Task<(int Deleted, int Promoted)> SoftDeleteAsync<T>(
            NocturneDbContext db, IQueryable<T> rows, DuplicateDelete mode) where T : class, IAuditable, ISoftDeletable
        {
            var result = await db.AuditedSoftDeleteWithIdsAsync(rows, Audit, "test", default, mode);
            return (result.Count, result.Promoted.Count);
        }

        private async Task<string> RestoreAsync(RecordType type)
        {
            await using var scope = test.Scope(tenant);
            await using var db = await Factory(scope).CreateAsync();
            var deleted = await SomeAsync(DeletedIds(db, type), 1);
            if (deleted.Length == 0)
                return $"restore (none) {type}";
            try
            {
                var restored = type switch
                {
                    RecordType.CarbIntake => (await db.RestoreDeletedGroupAsync<CarbIntakeEntity>(deleted[0], "carb")).Promoted.Count,
                    RecordType.SensorGlucose => (await db.RestoreDeletedGroupAsync<SensorGlucoseEntity>(deleted[0], "glucose")).Promoted.Count,
                    _ => (await db.RestoreDeletedGroupAsync<StateSpanEntity>(deleted[0], "span")).Promoted.Count,
                };
                Exercised.Add("restored");
                return $"restore {type}, {restored} promoted";
            }
            catch (RecreationBlockedException)
            {
                return $"restore blocked {type}";
            }
        }

        private async Task<string> PurgeAsync(RecordType type)
        {
            await using var scope = test.Scope(tenant);
            await using var db = await Factory(scope).CreateAsync();
            var ids = await SomeAsync(LiveIds(db, type));
            var purged = type switch
            {
                RecordType.CarbIntake => await db.CarbIntakes.PurgeAsync(e => ids.Contains(e.Id)),
                RecordType.SensorGlucose => await db.SensorGlucose.PurgeAsync(e => ids.Contains(e.Id)),
                _ => await db.StateSpans.PurgeAsync(e => ids.Contains(e.Id)),
            };
            return $"purge {purged} {type}";
        }

        private async Task<string> RetentionPurgeAsync(RecordType type)
        {
            await using var scope = test.Scope(tenant);
            var (table, column) = type switch
            {
                RecordType.CarbIntake => ("carb_intakes", "timestamp"),
                RecordType.SensorGlucose => ("sensor_glucose", "timestamp"),
                _ => ("state_spans", "start_timestamp"),
            };
            var cutoff = Base.AddMinutes(5 * random.Next(1, 4));
            var purged = await Factory(scope).PurgeOlderThanAsync(
                tenant, table, column, DateTime.UtcNow - cutoff, linkedRecordType: RecordTypeKeys.Key(type));
            return $"retention purge {purged} {type}";
        }

        /// <summary>Ages every link and record past the singleton retention, then prunes.</summary>
        private async Task<string> PruneAsync()
        {
            await test.ExecuteAsync(tenant, """
                UPDATE linked_records SET sys_created_at = sys_created_at - interval '2 days';
                UPDATE carb_intakes SET sys_created_at = sys_created_at - interval '2 days';
                UPDATE sensor_glucose SET sys_created_at = sys_created_at - interval '2 days';
                UPDATE state_spans SET created_at = created_at - interval '2 days';
                """);
            await using var scope = test.Scope(tenant);
            await using (var db = await Factory(scope).CreateAsync())
                await DeduplicationService.SetCursorAsync(db, new ReconcileCursor(DateTime.UtcNow, Guid.Empty), default);
            return $"prune {await Dedup(scope).PruneSingletonLinksAsync(1000, 10)}";
        }

        private async Task<string> SweepOrphansAsync(RecordType type)
        {
            await using var scope = test.Scope(tenant);
            await using var db = await Factory(scope).CreateAsync();
            var ids = await SomeAsync(LiveIds(db, type), 1);
            var removed = type switch
            {
                RecordType.CarbIntake => await db.CarbIntakes.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(),
                RecordType.SensorGlucose => await db.SensorGlucose.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(),
                _ => await db.StateSpans.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(),
            };
            return $"orphan sweep after removing {removed} {type}: {await DeduplicationService.DeleteOrphanedLinksAsync(db)}";
        }

        private static IQueryable<Guid> LiveIds(NocturneDbContext db, RecordType type) => type switch
        {
            RecordType.CarbIntake => db.CarbIntakes.Select(e => e.Id),
            RecordType.SensorGlucose => db.SensorGlucose.Select(e => e.Id),
            _ => db.StateSpans.Select(e => e.Id),
        };

        /// <summary>Live rows that are the primary of a group with another member.</summary>
        private static IQueryable<Guid> LiveGroupPrimaries(NocturneDbContext db, RecordType type)
        {
            var key = RecordTypeKeys.Key(type);
            var live = LiveIds(db, type);
            return db.LinkedRecords
                .Where(lr => lr.RecordType == key && lr.IsPrimary && live.Contains(lr.RecordId)
                    && db.LinkedRecords.Any(o => o.CanonicalId == lr.CanonicalId && o.Id != lr.Id))
                .Select(lr => lr.RecordId);
        }

        private static IQueryable<Guid> DeletedIds(NocturneDbContext db, RecordType type) => type switch
        {
            RecordType.CarbIntake => db.CarbIntakes.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
                .Where(e => e.DeletedAt != null).Select(e => e.Id),
            RecordType.SensorGlucose => db.SensorGlucose.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
                .Where(e => e.DeletedAt != null).Select(e => e.Id),
            _ => db.StateSpans.IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
                .Where(e => e.DeletedAt != null).Select(e => e.Id),
        };

        private async Task<Guid[]> SomeAsync(IQueryable<Guid> ids, int max = 3)
        {
            var all = await ids.OrderBy(id => id).ToListAsync();
            return all.OrderBy(_ => random.Next()).Take(random.Next(1, max + 1)).ToArray();
        }

        private T Pick<T>(params T[] options) => options[random.Next(options.Length)];
    }

    private async Task<DeduplicationBatchResult> IngestAsync(Guid tenant, RecordType type, IReadOnlyList<object> rows)
    {
        await using var scope = Scope(tenant);
        await using (var db = await Factory(scope).CreateAsync())
        {
            db.AddRange(rows);
            await db.SaveChangesAsync();
        }

        var inputs = rows.Select(row => row switch
        {
            CarbIntakeEntity c => new DeduplicationInput(c.Id, c.Timestamp.ToMills(), c.DataSource!, MatchCriteriaMapper.From(c)),
            SensorGlucoseEntity g => new DeduplicationInput(g.Id, g.Timestamp.ToMills(), g.DataSource!, MatchCriteriaMapper.From(g)),
            StateSpanEntity s => new DeduplicationInput(s.Id, s.StartTimestamp.ToMills(), s.Source!, MatchCriteriaMapper.From(s)),
            _ => throw new ArgumentOutOfRangeException(nameof(rows)),
        }).ToList();
        return await Dedup(scope).DeduplicateBatchAsync(type, inputs);
    }

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = database.Provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, tenant);
        return scope;
    }

    private static INocturneDbContextFactory Factory(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

    private static DeduplicationService Dedup(AsyncServiceScope scope) =>
        (DeduplicationService)scope.ServiceProvider.GetRequiredService<IDeduplicationService>();

    private static StateSpanEntity Span(Guid tenant, DateTime start, DateTime? end, string source, string state = "Automatic") => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Category = "PumpMode", State = state,
        StartTimestamp = start, EndTimestamp = end, Source = source,
    };

    private static StateSpan SpanModel(string source, string originalId, DateTime start, DateTime? end) => new()
    {
        Category = StateSpanCategory.PumpMode, State = "Automatic", StartTimestamp = start, EndTimestamp = end,
        Source = source, OriginalId = originalId,
    };

    private static CarbIntake Carb(string source, string key, DateTime at, double carbs) => new()
    {
        Timestamp = at, DataSource = source, LegacyId = key, SyncIdentifier = key, Carbs = carbs,
    };

    private async Task<StateSpanEntity> LegacySpanAsync(Guid tenant, Guid id)
    {
        await using var scope = Scope(tenant);
        await using var db = await Factory(scope).CreateAsync();
        return await db.StateSpans.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    /// <summary>Every row, soft-deleted included, whose flag disagrees with the anti-join.</summary>
    private async Task<List<string>> MismatchesAsync(Guid tenant)
    {
        var sql = string.Join("\nUNION ALL\n", Tables.Select(t => $"""
            SELECT '{t.Table}:' || r.id::text || ':' || r.is_duplicate::text FROM {t.Table} r
            WHERE r.is_duplicate IS DISTINCT FROM EXISTS (
                SELECT 1 FROM linked_records lr
                WHERE lr.tenant_id = r.tenant_id AND lr.record_type = '{RecordTypeKeys.Key(t.Type)}'
                  AND NOT lr.is_primary AND lr.record_id = r.id)
            """));
        return await QueryAsync(tenant, sql);
    }

    /// <summary>A group shows through its one primary, so a change that leaves two has missed a demotion.</summary>
    private Task<List<string>> GroupsWithTwoPrimariesAsync(Guid tenant) => QueryAsync(tenant,
        "SELECT canonical_id::text FROM linked_records WHERE is_primary GROUP BY canonical_id HAVING count(*) > 1");

    private async Task<List<Guid>> FlaggedAsync(Guid tenant, string table) =>
        (await QueryAsync(tenant, $"SELECT id::text FROM {table} WHERE is_duplicate ORDER BY id")).Select(Guid.Parse).ToList();

    private async Task<List<string>> QueryAsync(Guid tenant, string sql)
    {
        await using var conn = new NpgsqlConnection(database.AppConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await SetTenantAsync(conn, tenant);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
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

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'duplicate-flag', true, now(), now())
            """;
        cmd.Parameters.AddWithValue("id", tenant);
        cmd.Parameters.AddWithValue("slug", $"dup-flag-{tenant:N}");
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }
}
