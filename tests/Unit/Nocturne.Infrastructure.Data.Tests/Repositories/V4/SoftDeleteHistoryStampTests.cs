using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// Nightscout's v3 delete keeps the document with <c>isValid: false</c> and a new <c>srvModified</c>,
/// so a client syncing through <c>history</c> is told of it. The bulk soft delete therefore moves the
/// write stamp a history read pages on, as a tracked save would, spread over milliseconds so no one
/// millisecond holds more than a history page can take.
/// </summary>
[Trait("Category", "Unit")]
public class SoftDeleteHistoryStampTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Written = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly StatementRecorder _statements = new();

    public SoftDeleteHistoryStampTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(
            TenantId, "test", _statements, new MutationAuditInterceptor(Mock.Of<IHttpContextAccessor>()));
        _context = _db.CreateContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task BulkSoftDelete_MovesTheWriteStamp_AtMostOneGroupPerMillisecond_InOneReadAndOneUpdatePerGroup()
    {
        var group = NocturneDbContext.SystemTimestampGroupSize;
        var total = 2 * group + 1;
        var ids = await SeedAsync(total, legacyId: null);

        await using var context = _db.CreateContext();
        _statements.Clear();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.ApsSnapshots.Where(a => a.AidAlgorithm == "Loop"),
            SystemAuditContext.ForService("connector:test"),
            "scope=test");

        deleted.Should().Be(total);
        _statements.Count("SELECT").Should().Be(1, "the match set is read once, not once per group");
        _statements.Count("UPDATE").Should().Be(3);

        await using var verify = _db.CreateContext();
        var rows = await verify.ApsSnapshots.IgnoreQueryFilters().OrderBy(a => a.Id).ToListAsync();
        var deletedAt = rows[0].DeletedAt!.Value;
        rows.Should().OnlyContain(a => a.DeletedAt == deletedAt);
        rows.Take(group).Should().OnlyContain(a => a.SysUpdatedAt == deletedAt);
        rows.Skip(group).Take(group).Should().OnlyContain(a => a.SysUpdatedAt == deletedAt.AddMilliseconds(1));
        rows[^1].SysUpdatedAt.Should().Be(deletedAt.AddMilliseconds(2));
        rows.Select(a => a.Id).Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task DeleteByLegacyId_IsReadBackByTheHistory_FlaggedDeleted_AtItsDelete()
    {
        await SeedAsync(1, legacyId: "65f000000000000000000aaa");
        var repository = new ApsSnapshotRepository(
            new TestTenantDbContextFactory(_context),
            new SystemAuditContext(),
            NullLogger<ApsSnapshotRepository>.Instance);
        var cursor = new DateTimeOffset(Written, TimeSpan.Zero).ToUnixTimeMilliseconds();

        (await repository.GetModifiedSinceAsync(cursor, 1000)).Should().BeEmpty();

        (await repository.DeleteByLegacyIdAsync("65f000000000000000000aaa", WriteOrigin.Live)).Should().Be(1);

        var row = (await repository.GetModifiedSinceAsync(cursor, 1000)).Should().ContainSingle().Subject;
        row.Deleted.Should().BeTrue();
        row.Record.LegacyId.Should().Be("65f000000000000000000aaa");
        row.Record.ModifiedAt.Should().BeAfter(Written);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_user_deleting_one_copy_of_a_duplicate_deletes_every_copy_and_history_tombstones_them_all(
        bool deletePrimary)
    {
        // Two sources' copies of one note. The user deleted the event, so neither copy may be
        // promoted to keep it visible, whichever one the delete named.
        var (primary, copy) = await SeedNotesAsync();
        var repository = NotesAs(new UserAuditContext());
        var cursor = new DateTimeOffset(Written, TimeSpan.Zero).ToUnixTimeMilliseconds();

        await repository.DeleteAsync(deletePrimary ? primary : copy, WriteOrigin.Live);

        var page = await repository.GetModifiedSinceAsync(cursor, 1000);
        page.Select(r => (r.Record.Id, r.Deleted)).Should().BeEquivalentTo(new[] { (primary, true), (copy, true) });
        (await repository.GetByIdAsync(primary)).Should().BeNull();
        (await repository.GetByIdAsync(copy)).Should().BeNull();
        await using var verify = _db.CreateContext();
        (await verify.LinkedRecords.SingleAsync(l => l.IsPrimary)).RecordId.Should().Be(primary, "nothing is promoted");
        (await verify.Notes.IgnoreQueryFilters().Select(n => EF.Property<bool>(n, "DeletedByUser")).ToListAsync())
            .Should().AllSatisfy(byUser => byUser.Should().BeTrue());
        (await verify.MutationAuditLog.Where(a => a.Action == "delete").Select(a => a.EntityId).ToListAsync())
            .Should().BeEquivalentTo(new Guid?[] { primary, copy }, "each copy gets its own audit row");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restoring_either_copy_of_a_group_deleted_together_restores_the_group_with_one_live_primary(
        bool restorePrimary)
    {
        var (primary, copy) = await SeedNotesAsync();
        var repository = NotesAs(new UserAuditContext());
        await repository.DeleteAsync(primary, WriteOrigin.Live);
        var cursor = await SyncTheTombstonesAsync();

        await repository.RestoreAsync(restorePrimary ? primary : copy, WriteOrigin.Live);

        await AssertGroupRestoredAsync(repository, primary, copy, cursor);
    }

    [Fact]
    public async Task Bulk_restoring_the_non_primary_copy_restores_its_group_with_one_live_primary()
    {
        var (primary, copy) = await SeedNotesAsync();
        var repository = NotesAs(new UserAuditContext());
        await repository.DeleteAsync(copy, WriteOrigin.Live);
        var cursor = await SyncTheTombstonesAsync();

        var result = await repository.BulkRestoreAsync([copy], WriteOrigin.Live);

        result.Restored.Select(n => n.Id).Should().Equal(copy);
        result.Conflicts.Should().BeEmpty();
        await AssertGroupRestoredAsync(repository, primary, copy, cursor);
    }

    [Fact]
    public async Task Restoring_a_copy_whose_primary_another_delete_took_makes_the_restored_copy_primary()
    {
        var (primary, copy) = await SeedNotesAsync();
        var repository = NotesAs(new UserAuditContext());
        await repository.DeleteAsync(primary, WriteOrigin.Live);
        // The primary went in an earlier delete than the copy, so the two are not one delete's group.
        await _context.Notes.IgnoreQueryFilters().Where(n => n.Id == primary)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.DeletedAt, Written));
        var cursor = await SyncTheTombstonesAsync();

        await repository.RestoreAsync(copy, WriteOrigin.Live);

        (await repository.GetByIdAsync(primary)).Should().BeNull();
        (await repository.GetAsync(null, null, null, null, 100, 0, true)).Select(n => n.Id).Should().Equal(copy);
        await using var verify = _db.CreateContext();
        (await verify.LinkedRecords.SingleAsync(l => l.IsPrimary)).RecordId.Should().Be(copy);
        (await repository.GetModifiedSinceAsync(cursor, 1000))
            .Select(r => (r.Record.Id, r.Deleted)).Should().Contain((copy, false));
    }

    /// <summary>
    /// After a restore: both copies live, the original primary still the one primary, normal reads
    /// show exactly it, each restored row has a <c>restore</c> audit row, and history re-sends the
    /// primary live after <paramref name="cursor"/>.
    /// </summary>
    private async Task AssertGroupRestoredAsync(NoteRepository repository, Guid primary, Guid copy, long cursor)
    {
        await using var verify = _db.CreateContext();
        (await verify.Notes.Select(n => n.Id).ToListAsync()).Should().BeEquivalentTo([primary, copy]);
        (await verify.LinkedRecords.Where(l => l.IsPrimary).Select(l => l.RecordId).ToListAsync())
            .Should().Equal(primary);
        (await repository.GetAsync(null, null, null, null, 100, 0, true)).Select(n => n.Id).Should().Equal(primary);
        (await verify.MutationAuditLog.Where(a => a.Action == "restore").Select(a => a.EntityId).ToListAsync())
            .Should().BeEquivalentTo(new Guid?[] { primary, copy });
        (await repository.GetModifiedSinceAsync(cursor, 1000))
            .Select(r => (r.Record.Id, r.Deleted)).Should().Contain((primary, false));
    }

    /// <summary>
    /// Moves every note's stamp to one fixed millisecond, past which a client that synced the
    /// tombstones holds its cursor, and returns that cursor. Fixed, so the restore's own stamp
    /// cannot fall in the delete's millisecond.
    /// </summary>
    private async Task<long> SyncTheTombstonesAsync()
    {
        var synced = Written.AddMinutes(1);
        await _context.Notes.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(n => n.SysUpdatedAt, synced));
        return new DateTimeOffset(synced, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_user_delete_by_legacy_id_or_sync_identifier_deletes_every_copy(bool bySyncIdentifier)
    {
        var (primary, copy) = await SeedNotesAsync();
        await _context.Notes.Where(n => n.Id == primary).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.LegacyId, "65f000000000000000000bbb")
            .SetProperty(n => n.DataSource, "source-a")
            .SetProperty(n => n.SyncIdentifier, "sync-a"));
        var repository = NotesAs(new UserAuditContext());

        var deleted = bySyncIdentifier
            ? await repository.DeleteBySyncIdentifierAsync("source-a", "sync-a", WriteOrigin.Live)
            : await repository.DeleteByLegacyIdAsync("65f000000000000000000bbb", WriteOrigin.Live);

        deleted.Should().Be(2);
        await using var verify = _db.CreateContext();
        (await verify.Notes.CountAsync()).Should().Be(0);
        (await verify.MutationAuditLog.Select(a => a.EntityId).ToListAsync())
            .Should().BeEquivalentTo(new Guid?[] { primary, copy });
        (await verify.LinkedRecords.SingleAsync(l => l.IsPrimary)).RecordId.Should().Be(primary);
    }

    [Fact]
    public async Task DeletingASource_RepointsOnlyTheGroupsItLed_AndStampsThePromotedCopiesAfterTheDeletes()
    {
        // Group one is led by source A, group two by source B. Deleting everything A wrote must hand
        // group one to B's copy and leave group two, whose primary A never was, untouched.
        var ledByA = (A: Glucose("source-a", 120), B: Glucose("source-b", 121));
        var ledByB = (A: Glucose("source-a", 140, Written.AddMinutes(5)), B: Glucose("source-b", 141, Written.AddMinutes(5)));
        _context.SensorGlucose.AddRange(ledByA.A, ledByA.B, ledByB.A, ledByB.B);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        _context.LinkedRecords.AddRange(
            Link(first, ledByA.A.Id, isPrimary: true, "source-a", RecordType.SensorGlucose),
            Link(first, ledByA.B.Id, isPrimary: false, "source-b", RecordType.SensorGlucose),
            Link(second, ledByB.A.Id, isPrimary: false, "source-a", RecordType.SensorGlucose),
            Link(second, ledByB.B.Id, isPrimary: true, "source-b", RecordType.SensorGlucose));
        await _context.SaveChangesAsync();
        foreach (var row in new[] { ledByA.A, ledByA.B, ledByB.A, ledByB.B })
            row.SysUpdatedAt = Written;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await using var context = _db.CreateContext();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.SensorGlucose.FromSource("source-a"), SystemAuditContext.ForService("test"), "data_source=source-a");

        deleted.Should().Be(2);
        await using var verify = _db.CreateContext();
        (await verify.LinkedRecords.Where(l => l.IsPrimary).Select(l => l.RecordId).ToListAsync())
            .Should().BeEquivalentTo([ledByA.B.Id, ledByB.B.Id]);
        var rows = await verify.SensorGlucose.IgnoreQueryFilters().ToDictionaryAsync(g => g.Id);
        var deletedAt = rows[ledByA.A.Id].DeletedAt!.Value;
        rows[ledByA.B.Id].SysUpdatedAt.Should().Be(deletedAt.AddMilliseconds(1), "the promoted copy is stamped after the delete");
        rows[ledByB.B.Id].SysUpdatedAt.Should().Be(Written, "a group the deleted source did not lead is not touched");
    }

    [Fact]
    public async Task RepointingOnASharedContext_KeepsTheCallersPendingChanges()
    {
        var primary = Glucose("source-a", 120);
        var copy = Glucose("source-b", 121);
        _context.SensorGlucose.AddRange(primary, copy);
        var canonical = Guid.CreateVersion7();
        _context.LinkedRecords.AddRange(
            Link(canonical, primary.Id, isPrimary: true, "source-a", RecordType.SensorGlucose),
            Link(canonical, copy.Id, isPrimary: false, "source-b", RecordType.SensorGlucose));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        await _context.SensorGlucose.Where(g => g.Id == primary.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.DeletedAt, Written));

        var pending = Glucose("source-c", 150);
        _context.SensorGlucose.Add(pending);

        await DuplicateGroupPrimaries.RepointAwayFromAsync(
            _context, RecordType.SensorGlucose, [primary.Id], Written, CancellationToken.None);

        _context.Entry(pending).State.Should().Be(EntityState.Added);
        await using var verify = _db.CreateContext();
        (await verify.LinkedRecords.SingleAsync(l => l.IsPrimary)).RecordId.Should().Be(copy.Id);
    }

    [Fact]
    public async Task DeletingALargeSource_RepicksAChunkOfGroupsAtATime()
    {
        var count = 2 * DuplicateGroupPrimaries.RepickChunkSize + 1;
        var (_, copies) = await SeedGroupsAsync(count);

        await using var context = _db.CreateContext();
        _statements.Clear();
        var deleted = await context.AuditedSoftDeleteAsync(
            context.SensorGlucose.FromSource("source-a"), SystemAuditContext.ForService("test"), "data_source=source-a");

        deleted.Should().Be(count);
        _statements.Count("SELECT", "linked_records").Should().Be(1 + 3, "one lookup of the groups, then one read per chunk");
        await using var verify = _db.CreateContext();
        (await verify.LinkedRecords.Where(l => l.IsPrimary).Select(l => l.RecordId).ToListAsync())
            .Should().BeEquivalentTo(copies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_repoint_rolls_a_by_source_delete_back(bool withEntities)
    {
        var (primaries, copies) = await SeedGroupsAsync(1);

        await using var context = _db.CreateContext();
        var rows = context.SensorGlucose.FromSource("source-a");
        _statements.FailOnLinkPromote = true;
        Func<Task> act = withEntities
            ? () => context.AuditedSoftDeleteWithEntitiesAsync(rows, new UserAuditContext(), "data_source=source-a")
            : () => context.AuditedSoftDeleteAsync(rows, new UserAuditContext(), "data_source=source-a");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected*");
        _statements.LinkUpdates.Should().Be(2, "the demote ran and the promote failed, inside the delete's transaction");
        await AssertUntouchedAsync<SensorGlucoseEntity>(primaries[0], copies[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_write_rolls_a_whole_group_user_delete_back(bool bulk)
    {
        var (primary, copy) = await SeedNotesAsync();
        await _context.Notes.Where(n => n.Id == primary)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.LegacyId, "65f000000000000000000ccc"));
        var repository = NotesAs(new UserAuditContext());
        _statements.FailOn = bulk ? "UPDATE \"notes\"" : "INSERT INTO \"mutation_audit_log\"";

        Func<Task> act = bulk
            ? () => repository.DeleteByLegacyIdAsync("65f000000000000000000ccc", WriteOrigin.Live)
            : () => repository.DeleteAsync(primary, WriteOrigin.Live);

        (await act.Should().ThrowAsync<Exception>())
            .Which.ToString().Should().Contain("injected write failure");
        await AssertUntouchedAsync<NoteEntity>(primary, copy);
        await using var verify = _db.CreateContext();
        (await verify.Notes.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingAStateSpan_DeletesEveryCopyInItsGroup(bool activity)
    {
        var category = (activity ? StateSpanCategory.Exercise : StateSpanCategory.Override).ToString();
        StateSpanEntity Span(string source) => new()
        {
            Id = Guid.CreateVersion7(), TenantId = TenantId, Category = category, State = "Active",
            StartTimestamp = Written, Source = source,
        };
        var primary = Span("source-a");
        var copy = Span("source-b");
        _context.StateSpans.AddRange(primary, copy);
        var canonical = Guid.CreateVersion7();
        _context.LinkedRecords.AddRange(
            Link(canonical, primary.Id, isPrimary: true, "source-a", RecordType.StateSpan),
            Link(canonical, copy.Id, isPrimary: false, "source-b", RecordType.StateSpan));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var repository = new StateSpanRepository(
            _context, Mock.Of<Core.Contracts.Infrastructure.IDeduplicationService>(),
            new SystemAuditContext(), NullLogger<StateSpanRepository>.Instance);

        var deleted = activity
            ? await repository.DeleteActivityStateSpanAsync(primary.Id.ToString())
            : await repository.DeleteStateSpanAsync(primary.Id.ToString());

        deleted.Select(s => s.Id).Should().Equal(primary.Id.ToString(), copy.Id.ToString());
        await using var verify = _db.CreateContext();
        (await verify.LinkedRecords.SingleAsync(l => l.IsPrimary)).RecordId.Should().Be(primary.Id);
        (await verify.StateSpans.IgnoreQueryFilters().ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(s => s.DeletedAt != null);
    }

    /// <summary>
    /// What a rolled-back delete leaves: the primary live at its old stamp, no audit row, every
    /// link's primary flag as it was and the other copy unstamped.
    /// </summary>
    private async Task AssertUntouchedAsync<TEntity>(Guid primary, Guid copy)
        where TEntity : class, IIdentified, ISoftDeletable, ISystemTimestamped
    {
        await using var verify = _db.CreateContext();
        var rows = await verify.Set<TEntity>().IgnoreQueryFilters().ToDictionaryAsync(e => e.Id);
        rows[primary].DeletedAt.Should().BeNull();
        rows[primary].SysUpdatedAt.Should().Be(Written);
        rows[copy].SysUpdatedAt.Should().Be(Written);
        (await verify.MutationAuditLog.CountAsync()).Should().Be(0);
        (await verify.LinkedRecords.ToDictionaryAsync(l => l.RecordId, l => l.IsPrimary))
            .Should().Equal(new Dictionary<Guid, bool> { [primary] = true, [copy] = false });
    }

    private NoteRepository NotesAs(IAuditContext audit) => new(
        new AuditedFactory(_db, audit), Mock.Of<Core.Contracts.Infrastructure.IDeduplicationService>(),
        audit, NullLogger<NoteRepository>.Instance);

    /// <summary>One group per row: source A's reading leads, source B's is its duplicate.</summary>
    private async Task<(List<Guid> Primaries, List<Guid> Copies)> SeedGroupsAsync(int count)
    {
        var primaries = new List<Guid>();
        var copies = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var at = Written.AddMinutes(-5 * i);
            var primary = Glucose("source-a", 120, at);
            var copy = Glucose("source-b", 121, at);
            _context.SensorGlucose.AddRange(primary, copy);
            var canonical = Guid.CreateVersion7();
            _context.LinkedRecords.AddRange(
                Link(canonical, primary.Id, isPrimary: true, "source-a", RecordType.SensorGlucose),
                Link(canonical, copy.Id, isPrimary: false, "source-b", RecordType.SensorGlucose));
            primaries.Add(primary.Id);
            copies.Add(copy.Id);
        }
        await _context.SaveChangesAsync();
        await _context.SensorGlucose.ExecuteUpdateAsync(s => s.SetProperty(g => g.SysUpdatedAt, Written));
        _context.ChangeTracker.Clear();
        return (primaries, copies);
    }

    private async Task<(Guid Primary, Guid Copy)> SeedNotesAsync()
    {
        var canonical = Guid.CreateVersion7();
        var primary = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Written, Text = "a" };
        var copy = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = Written, Text = "b" };
        _context.Notes.AddRange(primary, copy);
        _context.LinkedRecords.AddRange(
            Link(canonical, primary.Id, isPrimary: true, "source-a"),
            Link(canonical, copy.Id, isPrimary: false, "source-b"));
        await _context.SaveChangesAsync();
        await _context.Notes.ExecuteUpdateAsync(s => s.SetProperty(n => n.SysUpdatedAt, Written));
        _context.ChangeTracker.Clear();
        return (primary.Id, copy.Id);
    }

    /// <summary>Hands out fresh contexts carrying a user's audit context, as a request's factory does.</summary>
    private sealed class AuditedFactory(SqliteTestDatabase db, IAuditContext audit) : ITenantDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
        {
            var ctx = db.CreateContext();
            ctx.AuditContext = audit;
            return ValueTask.FromResult(ctx);
        }
    }

    private sealed class UserAuditContext : IAuditContext
    {
        public Guid? SubjectId { get; } = Guid.CreateVersion7();
        public string? SubjectName => "tester";
        public string? AuthType => "SessionCookie";
        public string? IpAddress => null;
        public Guid? TokenId => null;
        public string? TraceId => null;
        public string? Endpoint => "DELETE /api/v4/test";
        public bool IsSystem => false;
    }

    private static SensorGlucoseEntity Glucose(string source, double mgdl, DateTime? at = null) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = at ?? Written, Mgdl = mgdl, DataSource = source,
    };

    private static LinkedRecordEntity Link(
        Guid canonical, Guid recordId, bool isPrimary, string source, RecordType type = RecordType.Note) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = TenantId, CanonicalId = canonical,
        RecordType = RecordTypeKeys.Key(type), RecordId = recordId, DataSource = source, IsPrimary = isPrimary,
    };

    /// <summary>
    /// Counts the statements the connection runs, by leading keyword. Armed, it fails the second
    /// <c>UPDATE</c> of <c>linked_records</c> (a repoint's promote, after its demote), or the first
    /// statement starting with <see cref="FailOn"/>.
    /// </summary>
    private sealed class StatementRecorder : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        public bool FailOnLinkPromote { get; set; }
        public string? FailOn { get; set; }
        public int LinkUpdates { get; private set; }

        public void Clear() => _statements.Clear();

        public int Count(string keyword) =>
            _statements.Count(s => s.StartsWith(keyword, StringComparison.OrdinalIgnoreCase));

        public int Count(string keyword, string table) =>
            _statements.Count(s => s.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && s.Contains($"FROM \"{table}\"", StringComparison.Ordinal));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            var text = command.CommandText.TrimStart();
            _statements.Add(text);
            if (FailOn is { } prefix && text.Contains(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("injected write failure");
            if (!text.StartsWith("UPDATE \"linked_records\"", StringComparison.Ordinal))
                return;
            LinkUpdates++;
            if (FailOnLinkPromote && LinkUpdates == 2)
                throw new InvalidOperationException("injected repoint failure");
        }
    }

    private async Task<List<Guid>> SeedAsync(int count, string? legacyId)
    {
        var entities = Enumerable.Range(0, count).Select(i => new ApsSnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TenantId,
            LegacyId = legacyId,
            Timestamp = Written.AddMinutes(-i),
            AidAlgorithm = "Loop",
        }).ToList();

        _context.ApsSnapshots.AddRange(entities);
        await _context.SaveChangesAsync();

        foreach (var entity in entities)
            entity.SysUpdatedAt = Written;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return entities.Select(e => e.Id).ToList();
    }
}
