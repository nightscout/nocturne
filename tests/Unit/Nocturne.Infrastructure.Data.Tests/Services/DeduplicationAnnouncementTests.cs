using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// A reconcile batch and each slice of a full run announce, once their changes commit, the record types
/// whose shown copy they changed, naming no record; a batch or slice that changed nothing, or rolled
/// back, announces nothing (<see cref="RepickAnnouncement"/>).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
public sealed class DeduplicationAnnouncementTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // Shared because EF builds an internal service provider per distinct interceptor set. Tests within a
    // class run one at a time.
    private static readonly FailingReads Failing = new();

    private static readonly DateTime At = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId, "test", Failing);
    private readonly RecordingRecordChanges _changes = new();

    public void Dispose()
    {
        Failing.FailWhen = null;
        _db.Dispose();
    }

    private INocturneDbContextFactory Contexts() => _db.CreateContext(TenantId).AsFactory();

    private DuplicateReconciler Reconciler() =>
        DeduplicationModules.Reconciler(Contexts(), changes: _changes);

    private Task<ReconcileResult> ReconcileAsync(int batchSize = 5000) =>
        Reconciler().ReconcileNewLinksAsync(batchSize, 10, skipBatchAfterFailures: null, CancellationToken.None);

    [Fact]
    public async Task A_batch_that_merges_site_changes_announces_their_type_once_and_names_no_record()
    {
        var (_, later) = await SplitSiteChangeAsync(At, linkedAt: At);
        await AddLinkedNoteAsync(At.AddDays(-3), linkedAt: At);

        (await ReconcileAsync()).GroupsMerged.Should().Be(1);

        var batch = _changes.Batches.Should().ContainSingle().Subject;
        var change = batch.Changes.Should().ContainSingle().Subject;
        change.RecordType.Should().Be(typeof(DeviceEvent), "the note's batch merged nothing of its own type");
        change.HasRecords.Should().BeFalse();
        change.Origin.Should().Be(WriteOrigin.Backfill);
        (await IsDuplicateAsync(later)).Should().BeTrue("the merge hid the later copy, which reads showed");
    }

    [Fact]
    public async Task A_batch_that_merges_nothing_announces_nothing()
    {
        await AddLinkedNoteAsync(At, linkedAt: At);
        await AddLinkedSiteChangeAsync(At.AddDays(-2), "mylife-connector", linkedAt: At);

        var result = await ReconcileAsync();

        result.GroupsMerged.Should().Be(0);
        result.CaughtUp.Should().BeTrue();
        _changes.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_batch_that_rolls_back_after_its_merge_announces_nothing()
    {
        var (_, later) = await SplitSiteChangeAsync(At, linkedAt: At);
        var stateReads = 0;
        Failing.FailWhen = sql => sql.Contains("dedup_reconcile_state") && ++stateReads == 2;

        var reconcile = () => ReconcileAsync();

        await reconcile.Should().ThrowAsync<InvalidOperationException>("the cursor read after the merge failed");
        _changes.Batches.Should().BeEmpty();
        (await IsDuplicateAsync(later)).Should().BeFalse("the merge rolled back with its batch");
    }

    /// <summary>
    /// A reconcile run announces at most once per committed batch, and only for a batch that merged.
    /// </summary>
    [Fact]
    public async Task A_run_announces_once_per_batch_that_merged()
    {
        await SplitSiteChangeAsync(At, linkedAt: At);
        await SplitSiteChangeAsync(At.AddDays(-10), linkedAt: At.AddMinutes(1));
        await AddLinkedNoteAsync(At.AddDays(-20), linkedAt: At.AddMinutes(2));
        await AddLinkedNoteAsync(At.AddDays(-25), linkedAt: At.AddMinutes(2));
        await SplitSiteChangeAsync(At.AddDays(-30), linkedAt: At.AddMinutes(3));

        var result = await ReconcileAsync(batchSize: 2);

        result.GroupsMerged.Should().Be(3);
        _changes.Batches.Should().HaveCount(3, "four batches committed and three of them merged");
        _changes.Changes.Should().OnlyContain(c => c.RecordType == typeof(DeviceEvent) && !c.HasRecords);
    }

    /// <summary>
    /// A run over many pages announces once for its slice, so a long history starts one evaluation per
    /// slice, not one per page.
    /// </summary>
    [Fact]
    public async Task A_full_run_over_many_pages_announces_once_per_slice()
    {
        const int pairs = 300;
        await SeedUnlinkedSiteChangePairsAsync(pairs);
        (2 * pairs).Should().BeGreaterThan(LinkedRecordTypes.PageSize, "the site changes span two pages");

        var result = await DeduplicationModules.Admin(Contexts(), TenantId, _changes).RunFullAsync();

        result.RecordsLinked.Should().Be(2 * pairs);
        result.CanonicalGroupsCreated.Should().Be(pairs);
        var change = _changes.Batches.Should().ContainSingle().Subject.Changes.Should().ContainSingle().Subject;
        change.RecordType.Should().Be(typeof(DeviceEvent));
        change.HasRecords.Should().BeFalse();
    }

    [Fact]
    public async Task Each_slice_of_a_run_announces_its_own_changes()
    {
        await SeedUnlinkedSiteChangePairsAsync(300);

        await Sliced(DeduplicationModules.Linker(Contexts())).RunToEndAsync();

        _changes.Batches.Should().HaveCount(2, "the run ends a slice after each of its two pages");
        _changes.Changes.Should().OnlyContain(c => c.RecordType == typeof(DeviceEvent) && !c.HasRecords);
    }

    [Fact]
    public async Task A_full_run_that_joins_nothing_announces_nothing()
    {
        await using (var db = _db.CreateContext(TenantId))
        {
            for (var i = 0; i < 5; i++)
                db.DeviceEvents.Add(SiteChange(At.AddDays(-i), "mylife-connector"));
            await db.SaveChangesAsync();
        }

        var result = await DeduplicationModules.Admin(Contexts(), TenantId, _changes).RunFullAsync();

        result.CanonicalGroupsCreated.Should().Be(5);
        _changes.Batches.Should().BeEmpty();
    }

    /// <summary>
    /// A page of one chunk is one transaction, so a slice that fails on it announces only the pages
    /// before it.
    /// </summary>
    [Fact]
    public async Task A_failed_slice_announces_only_the_types_its_committed_pages_changed()
    {
        await using (var db = _db.CreateContext(TenantId))
        {
            db.Boluses.Add(new BolusEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = At, Insulin = 2, DataSource = "mylife-connector" });
            db.CarbIntakes.Add(new CarbIntakeEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = At, Carbs = 40, DataSource = "mylife-connector" });
            await db.SaveChangesAsync();
        }

        var linker = new Mock<IDuplicateLinker>();
        linker
            .Setup(l => l.DeduplicateBatchAsync(RecordType.Bolus, It.IsAny<IReadOnlyList<DeduplicationInput>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeduplicationBatchResult(Processed: 2, GroupsCreated: 1, RecordsLinked: 2, DuplicateGroups: 1));
        linker
            .Setup(l => l.DeduplicateBatchAsync(RecordType.CarbIntake, It.IsAny<IReadOnlyList<DeduplicationInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated link failure"));
        var status = await Sliced(linker.Object).RunToEndAsync();

        status.State.Should().Be(DeduplicationJobState.Failed);
        _changes.Changes.Select(c => c.RecordType).Should().Equal([typeof(Bolus)],
            "the failed carb page committed nothing to announce");
    }

    private DeduplicationAdmin Sliced(IDuplicateLinker linker) =>
        new(Contexts(), linker, DeduplicationModules.Reconciler(Contexts()), NullLogger<DeduplicationAdmin>.Instance,
            new DeduplicationModules.FixedTenantAccessor(new TenantContext(TenantId, "test", "Test", true, false)),
            new CategoryReadContext(), _changes) { SliceLength = TimeSpan.Zero };

    /// <summary>
    /// Pairs of one site change reported by two connectors seconds apart, each pair a week from the
    /// next, none linked yet.
    /// </summary>
    private async Task SeedUnlinkedSiteChangePairsAsync(int pairs)
    {
        await using var db = _db.CreateContext(TenantId);
        for (var i = 0; i < pairs; i++)
        {
            var at = At.AddDays(-7 * i);
            db.DeviceEvents.AddRange(SiteChange(at, "mylife-connector"), SiteChange(at.AddSeconds(20), "glooko-connector"));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One site change reported by two connectors 20 seconds apart, each linked as a group of its own,
    /// as a pair insert-time linking left apart. Returns the two record ids, earlier first.
    /// </summary>
    private async Task<(Guid Earlier, Guid Later)> SplitSiteChangeAsync(DateTime at, DateTime linkedAt) =>
        (await AddLinkedSiteChangeAsync(at, "mylife-connector", linkedAt),
         await AddLinkedSiteChangeAsync(at.AddSeconds(20), "glooko-connector", linkedAt));

    private async Task<Guid> AddLinkedSiteChangeAsync(DateTime at, string source, DateTime linkedAt)
    {
        var record = SiteChange(at, source);
        await using var db = _db.CreateContext(TenantId);
        db.DeviceEvents.Add(record);
        await db.SaveChangesAsync();
        await AddPrimaryLinkAsync(RecordType.DeviceEvent, record.Id, at, source, linkedAt);
        return record.Id;
    }

    private async Task AddLinkedNoteAsync(DateTime at, DateTime linkedAt)
    {
        var note = new NoteEntity { Id = Guid.CreateVersion7(), TenantId = TenantId, Timestamp = at, Text = "note", DataSource = "mylife-connector" };
        await using var db = _db.CreateContext(TenantId);
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        await AddPrimaryLinkAsync(RecordType.Note, note.Id, at, "mylife-connector", linkedAt);
    }

    /// <summary>A primary link of a group of its own, created at <paramref name="linkedAt"/>.</summary>
    private async Task AddPrimaryLinkAsync(RecordType recordType, Guid recordId, DateTime at, string source, DateTime linkedAt)
    {
        await using var db = _db.CreateContext(TenantId);
        var link = new LinkedRecordEntity
        {
            Id = Guid.CreateVersion7(),
            CanonicalId = Guid.CreateVersion7(),
            RecordType = RecordTypeKeys.Key(recordType),
            RecordId = recordId,
            SourceTimestamp = at.ToMills(),
            DataSource = source,
            IsPrimary = true,
        };
        db.LinkedRecords.Add(link);
        await db.SaveChangesAsync();
        link.SysCreatedAt = linkedAt;
        await db.SaveChangesAsync();
    }

    private static DeviceEventEntity SiteChange(DateTime at, string source) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = TenantId,
        EventType = nameof(DeviceEventType.SiteChange),
        Timestamp = at,
        DataSource = source,
    };

    private async Task<bool> IsDuplicateAsync(Guid deviceEventId)
    {
        await using var db = _db.CreateContext(TenantId);
        return await db.DeviceEvents.AsNoTracking().Where(e => e.Id == deviceEventId).Select(e => e.IsDuplicate).SingleAsync();
    }

    private sealed class FailingReads : DbCommandInterceptor
    {
        public Func<string, bool>? FailWhen { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            FailWhen?.Invoke(command.CommandText) == true
                ? throw new InvalidOperationException("Simulated read failure")
                : ValueTask.FromResult(result);
    }
}
