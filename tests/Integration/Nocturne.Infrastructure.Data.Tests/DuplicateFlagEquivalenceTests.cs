using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Services.Deduplication;
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

        foreach (var type in new[] { RecordType.CarbIntake, RecordType.SensorGlucose, RecordType.StateSpan })
        {
            var operation = await run.SplitThenMergeAsync(type);
            (await MismatchesAsync(tenant)).Should().BeEmpty($"{operation} of seed {seed}");
            (await GroupsWithTwoPrimariesAsync(tenant)).Should().BeEmpty($"{operation} of seed {seed}");
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

        await ClampedDeleteAsync(tenant, RecordType.StateSpan, open.Id);

        (await FlaggedAsync(tenant, "state_spans")).Should().Equal(open.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// A history-clamped member deletes the primary of a group whose one live copy is older than its
    /// window. A deleted copy sorts first, so a repick that reads the old copy as dead makes the deleted
    /// one primary and the group shows nothing.
    /// </summary>
    [Theory]
    [InlineData(RecordType.CarbIntake)]
    [InlineData(RecordType.StateSpan)]
    public async Task AClampedCallersDeletePromotesALiveCopyOutsideItsWindow(RecordType type)
    {
        var tenant = await SeedTenantAsync();
        var (primary, old) = await GroupWithACopyOutsideTheWindowAsync(tenant, type);

        await ClampedDeleteAsync(tenant, type, primary);

        (await QueryAsync(tenant, "SELECT record_id::text FROM linked_records WHERE is_primary"))
            .Should().Equal(old.ToString());
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task AClampedCallersDeleteStampsAPromotedCopyOutsideItsWindow()
    {
        var tenant = await SeedTenantAsync();
        var (primary, old) = await GroupWithACopyOutsideTheWindowAsync(tenant, RecordType.CarbIntake);
        var before = await CarbUpdatedAtAsync(tenant, old);

        await ClampedDeleteAsync(tenant, RecordType.CarbIntake, primary);

        (await CarbUpdatedAtAsync(tenant, old)).Should().BeAfter(before, "a v3 history client is sent the promoted copy");
    }

    [Fact]
    public async Task AClampedUploadLinksToADuplicateOutsideItsWindow()
    {
        var tenant = await SeedTenantAsync();
        var at = DateTime.UtcNow.AddDays(-3);
        await IngestAsync(tenant, RecordType.CarbIntake, [Carb(tenant, "mylife-connector", at)]);
        var upload = Carb(tenant, "glooko-connector", at.AddSeconds(10));

        (await IngestAsync(tenant, RecordType.CarbIntake, [upload], clamped: true)).DuplicateGroups.Should().Be(1);

        (await FlaggedAsync(tenant, "carb_intakes")).Should().Equal(upload.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    [Fact]
    public async Task AClampedUploadLinksToAPrunedSingletonOutsideItsWindow()
    {
        var tenant = await SeedTenantAsync();
        var at = DateTime.UtcNow.AddDays(-3);
        await IngestAsync(tenant, RecordType.CarbIntake, [Carb(tenant, "mylife-connector", at)]);
        await ExecuteAsync(tenant, """
            UPDATE carb_intakes SET sys_created_at = sys_created_at - interval '2 days';
            DELETE FROM linked_records;
            """);
        var upload = Carb(tenant, "glooko-connector", at.AddSeconds(10));

        (await IngestAsync(tenant, RecordType.CarbIntake, [upload], clamped: true)).DuplicateGroups.Should().Be(1);

        (await FlaggedAsync(tenant, "carb_intakes")).Should().Equal(upload.Id);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// A three-copy group of one event: an in-window primary, a copy outside the window, and a deleted
    /// copy that sorts first.
    /// </summary>
    private async Task<(Guid Primary, Guid Old)> GroupWithACopyOutsideTheWindowAsync(Guid tenant, RecordType type)
    {
        if (type == RecordType.StateSpan)
        {
            var start = DateTime.UtcNow.AddDays(-4);
            var ended = DateTime.UtcNow.AddDays(-2);
            var primarySpan = Span(tenant, start, end: null, "mylife-connector");
            var deletedSpan = Span(tenant, start.AddSeconds(-5), ended, "glooko-connector");
            var oldSpan = Span(tenant, start.AddSeconds(5), ended, "nightscout-connector");
            await IngestAsync(tenant, type, [primarySpan, deletedSpan, oldSpan]);
            await ExecuteAsync(tenant, $"UPDATE state_spans SET deleted_at = now() WHERE id = '{deletedSpan.Id}'");
            return (primarySpan.Id, oldSpan.Id);
        }

        var at = DateTime.UtcNow.AddHours(-1);
        var primary = Carb(tenant, "mylife-connector", at);
        var deleted = Carb(tenant, "glooko-connector", at.AddSeconds(-5));
        var old = Carb(tenant, "nightscout-connector", at.AddSeconds(5));
        await IngestAsync(tenant, type, [primary, deleted, old]);
        await ExecuteAsync(tenant, $"""
            UPDATE carb_intakes SET deleted_at = now() WHERE id = '{deleted.Id}';
            UPDATE carb_intakes SET timestamp = timestamp - interval '3 days' WHERE id = '{old.Id}';
            """);
        return (primary.Id, old.Id);
    }

    private async Task ClampedDeleteAsync(Guid tenant, RecordType type, Guid id)
    {
        await using var scope = Scope(tenant);
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        await using var db = await Factory(scope).CreateAsync();
        var deleted = type == RecordType.StateSpan
            ? await db.AuditedSoftDeleteAsync(
                db.StateSpans.Where(s => s.Id == id), Audit, "test", default, DuplicateDelete.PromoteSurvivor)
            : await db.AuditedSoftDeleteAsync(
                db.CarbIntakes.Where(c => c.Id == id), Audit, "test", default, DuplicateDelete.PromoteSurvivor);
        deleted.Should().Be(1, "the primary is inside the clamped caller's window");
    }

    private async Task<DateTime> CarbUpdatedAtAsync(Guid tenant, Guid id)
    {
        await using var scope = Scope(tenant);
        await using var db = await Factory(scope).CreateAsync();
        return await db.CarbIntakes.Where(c => c.Id == id).Select(c => c.SysUpdatedAt).SingleAsync();
    }

    private static CarbIntakeEntity Carb(Guid tenant, string source, DateTime at) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, DataSource = source, Carbs = 40,
    };

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
        var primary = Carb(tenant, "mylife-connector", at);
        var copy = Carb(tenant, "glooko-connector", at.AddSeconds(10));
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

    /// <summary>
    /// A merge of two groups while a soft delete is promoting a copy in one of them. Read before the
    /// delete commits, the merge would keep the deleted primary's group's old plan and leave the
    /// promoted copy primary beside the merge's survivor; locked first, it reads what the delete left.
    /// </summary>
    [Fact]
    public async Task AMergeWaitsForASoftDeleteMovingAPrimaryInOneOfItsGroups()
    {
        var tenant = await SeedTenantAsync();
        var (earlier, later) = await SplitCarbGroupsAsync(tenant);

        await using var delete = Context(tenant);
        await using var transaction = await delete.Database.BeginTransactionAsync();
        var deleted = await delete.AuditedSoftDeleteWithIdsAsync(
            delete.CarbIntakes.Where(e => e.Id == later.Primary), Audit, "test", default, DuplicateDelete.PromoteSurvivor);
        deleted.Promoted.Should().Equal(later.Copy);

        await using var scope = Scope(tenant);
        var merge = Task.Run(() => Dedup(scope).MergeDuplicateGroupsAsync(RecordType.CarbIntake, null, default));
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, merge);

        await transaction.CommitAsync();
        (await merge.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(1);

        (await GroupsWithTwoPrimariesAsync(tenant)).Should().BeEmpty();
        (await MismatchesAsync(tenant)).Should().BeEmpty();
        await using var check = Context(tenant);
        (await check.LinkedRecords.Where(l => l.IsPrimary).Select(l => l.RecordId).ToListAsync())
            .Should().Equal(earlier.Primary);
    }

    /// <summary>
    /// A soft delete that promotes a copy takes its groups' links before it writes its rows, as a merge
    /// does, so a merge holding the links and about to rewrite the rows' flags does not deadlock with it.
    /// </summary>
    [Fact]
    public async Task ASoftDeleteWaitsForTheLinksBeforeItWritesItsRows()
    {
        var tenant = await SeedTenantAsync();
        var (_, later) = await SplitCarbGroupsAsync(tenant);

        await using var merge = Context(tenant);
        await using var transaction = await merge.Database.BeginTransactionAsync();
        var canonical = await merge.LinkedRecords.Where(l => l.RecordId == later.Primary).Select(l => l.CanonicalId).SingleAsync();
        await DuplicateGroupPrimaries.LockGroupsAsync(merge, RecordType.CarbIntake, [canonical], default);

        var delete = Task.Run(async () =>
        {
            await using var db = Context(tenant);
            return await db.AuditedSoftDeleteWithIdsAsync(
                db.CarbIntakes.Where(e => e.Id == later.Primary), Audit, "test", default, DuplicateDelete.PromoteSurvivor);
        });
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, delete);

        var rowWrite = merge.Database.ExecuteSqlAsync(
            $"UPDATE carb_intakes SET is_duplicate = is_duplicate WHERE id = {later.Primary}");
        (await Task.WhenAny(rowWrite, Task.Delay(TimeSpan.FromSeconds(10)))).Should().Be(rowWrite,
            "the waiting delete holds none of the rows yet");
        await rowWrite;
        await transaction.CommitAsync();

        (await delete.WaitAsync(TimeSpan.FromSeconds(30))).Promoted.Should().Equal(later.Copy);
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// A repick of a group whose primary is gone, while a soft delete of its earliest live copy is open.
    /// Read before the delete commits, the repick would promote the deleted copy and the group would
    /// show nothing.
    /// </summary>
    [Fact]
    public async Task ARepickWaitsForASoftDeleteOfTheCopyItWouldPromote()
    {
        var tenant = await SeedTenantAsync();
        var at = DateTime.UtcNow.AddHours(-1);
        var primary = Carb(tenant, "mylife-connector", at);
        var earliest = Carb(tenant, "glooko-connector", at.AddSeconds(2));
        var next = Carb(tenant, "nightscout-connector", at.AddSeconds(4));
        await IngestAsync(tenant, RecordType.CarbIntake, [primary, earliest, next]);
        await ExecuteAsync(tenant, $"UPDATE carb_intakes SET deleted_at = now() WHERE id = '{primary.Id}'");

        await using var delete = Context(tenant);
        await using var transaction = await delete.Database.BeginTransactionAsync();
        await delete.AuditedSoftDeleteWithIdsAsync(
            delete.CarbIntakes.Where(e => e.Id == earliest.Id), Audit, "test", default, DuplicateDelete.PromoteSurvivor);

        var repick = Task.Run(async () =>
        {
            await using var db = Context(tenant);
            await using var repickTransaction = await db.Database.BeginTransactionAsync();
            var canonical = await db.LinkedRecords.Where(l => l.RecordId == primary.Id).Select(l => l.CanonicalId).SingleAsync();
            var promoted = await DuplicateGroupPrimaries.RepickAsync(db, RecordType.CarbIntake, [canonical], default);
            await repickTransaction.CommitAsync();
            return promoted;
        });
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, repick);

        await transaction.CommitAsync();

        (await repick.WaitAsync(TimeSpan.FromSeconds(30))).Should().Equal(next.Id);
        (await QueryAsync(tenant, "SELECT record_id::text FROM linked_records WHERE is_primary"))
            .Should().Equal(next.Id.ToString());
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// A glucose restore while a delete of its group's primary is open. A glucose delete locks no link
    /// before it writes its rows (<see cref="GlucoseStreamFlags"/>), so a restore that read the
    /// primary as live before the delete committed would keep it, and the group would show nothing.
    /// </summary>
    [Fact]
    public async Task AGlucoseRestoreWaitsForADeleteOfItsGroupsPrimary()
    {
        var tenant = await SeedTenantAsync();
        var at = DateTime.UtcNow.AddHours(-1);
        SensorGlucoseEntity Glucose(string source, DateTime when) => new()
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = when, DataSource = source, Mgdl = 120,
        };
        var primary = Glucose("mylife-connector", at);
        var copy = Glucose("glooko-connector", at.AddSeconds(2));
        await IngestAsync(tenant, RecordType.SensorGlucose, [primary, copy]);
        await ExecuteAsync(tenant, $"UPDATE sensor_glucose SET deleted_at = now() WHERE id = '{copy.Id}'");

        await using var delete = Context(tenant);
        await using var transaction = await delete.Database.BeginTransactionAsync();
        await delete.AuditedSoftDeleteWithIdsAsync(
            delete.SensorGlucose.Where(e => e.Id == primary.Id), Audit, "test", default, DuplicateDelete.PromoteSurvivor);

        var restore = Task.Run(async () =>
        {
            await using var db = Context(tenant);
            return await db.RestoreDeletedGroupAsync<SensorGlucoseEntity>(copy.Id, "glucose");
        });
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, restore);

        await transaction.CommitAsync();

        (await restore.WaitAsync(TimeSpan.FromSeconds(30))).Promoted.Should().Equal(copy.Id);
        (await QueryAsync(tenant, "SELECT record_id::text FROM linked_records WHERE is_primary"))
            .Should().Equal(copy.Id.ToString());
        (await MismatchesAsync(tenant)).Should().BeEmpty();
    }

    /// <summary>
    /// Two two-source groups of one meal, kept apart by a value the later group then has corrected to
    /// the earlier's, so a merge joins them. Four sources, as a group takes one record per source.
    /// </summary>
    private async Task<((Guid Primary, Guid Copy) Earlier, (Guid Primary, Guid Copy) Later)> SplitCarbGroupsAsync(Guid tenant)
    {
        var at = DateTime.UtcNow.AddHours(-1);
        CarbIntakeEntity Carb(string source, DateTime when, double carbs) => new()
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = when, DataSource = source, Carbs = carbs,
        };
        var earlier = (Primary: Carb("a-connector", at, 40), Copy: Carb("b-connector", at.AddSeconds(2), 40));
        var later = (Primary: Carb("c-connector", at.AddSeconds(10), 90), Copy: Carb("d-connector", at.AddSeconds(12), 90));
        await IngestAsync(tenant, RecordType.CarbIntake, [earlier.Primary, earlier.Copy]);
        await IngestAsync(tenant, RecordType.CarbIntake, [later.Primary, later.Copy]);
        await ExecuteAsync(tenant,
            $"UPDATE carb_intakes SET carbs = 40 WHERE id IN ('{later.Primary.Id}', '{later.Copy.Id}')");
        return ((earlier.Primary.Id, earlier.Copy.Id), (later.Primary.Id, later.Copy.Id));
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
            Factory(scope), scope.ServiceProvider.GetRequiredService<IDuplicateLinker>(), Audit,
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
            Factory(scope), scope.ServiceProvider.GetRequiredService<IDuplicateLinker>(), Audit,
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

        /// <summary>
        /// Splits one event of <paramref name="type"/> across two groups and merges them: two connectors'
        /// copies ingested with different values, the second then corrected to the first's. Whether a
        /// random merge finds a split depends on ids drawn from the clock, so every run ends on one that
        /// must, on a day no random step writes to.
        /// </summary>
        public async Task<string> SplitThenMergeAsync(RecordType type)
        {
            var at = Base.AddDays(1).AddHours(12).AddMinutes(10 * (int)type);
            var (first, second, correct) = type switch
            {
                RecordType.CarbIntake => Pair(
                    new CarbIntakeEntity { Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, DataSource = Sources[0], Carbs = 20 },
                    new CarbIntakeEntity { Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at.AddSeconds(5), DataSource = Sources[1], Carbs = 99 },
                    id => $"UPDATE carb_intakes SET carbs = 20 WHERE id = '{id}'"),
                RecordType.SensorGlucose => Pair(
                    new SensorGlucoseEntity { Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at, DataSource = Sources[0], Mgdl = 120 },
                    new SensorGlucoseEntity { Id = Guid.CreateVersion7(), TenantId = tenant, Timestamp = at.AddSeconds(5), DataSource = Sources[1], Mgdl = 199 },
                    id => $"UPDATE sensor_glucose SET mgdl = 120 WHERE id = '{id}'"),
                _ => Pair(
                    Span(tenant, at, null, Sources[0]),
                    Span(tenant, at.AddSeconds(5), null, Sources[1], "Manual"),
                    id => $"UPDATE state_spans SET state = 'Automatic' WHERE id = '{id}'"),
            };

            await test.IngestAsync(tenant, type, [first.Row]);
            await test.IngestAsync(tenant, type, [second.Row]);
            await test.ExecuteAsync(tenant, correct(second.Id));

            await using var scope = test.Scope(tenant);
            var merged = await Dedup(scope).MergeDuplicateGroupsAsync(type, null, default);
            merged.Should().BePositive($"the corrected copy of {type} matches the first");
            Exercised.Add("merged");
            return $"split then merge {type}: {merged}";
        }

        private static ((object Row, Guid Id) First, (object Row, Guid Id) Second, Func<Guid, string> Correct) Pair<T>(
            T first, T second, Func<Guid, string> correct) where T : class, IIdentified =>
            ((first, first.Id), (second, second.Id), correct);

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
                await DuplicateReconciler.SetCursorAsync(db, new ReconcileCursor(DateTime.UtcNow, Guid.Empty), default);
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
            return $"orphan sweep after removing {removed} {type}: {await LinkedRecordTypes.DeleteOrphanedLinksAsync(db)}";
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

    private async Task<DeduplicationBatchResult> IngestAsync(
        Guid tenant, RecordType type, IReadOnlyList<object> rows, bool clamped = false)
    {
        await using var scope = Scope(tenant);
        if (clamped)
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
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
        return await scope.ServiceProvider.GetRequiredService<IDuplicateLinker>().DeduplicateBatchAsync(type, inputs);
    }

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = database.Provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, tenant);
        return scope;
    }

    private static INocturneDbContextFactory Factory(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>();

    private static DuplicateReconciler Dedup(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<DuplicateReconciler>();

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
