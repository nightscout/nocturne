using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Services;

/// <summary>
/// The full deduplication run through <see cref="DeduplicationAdmin"/>: who may start one, the run's
/// stored status, cancellation before and during the run, and slices that resume where the last one
/// ended, including a slice run twice from one cursor as when its process dies before the job ledger
/// records where it ended.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
public class DeduplicationAdminTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static readonly DateTime At = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DeduplicationModules.FixedTenantAccessor Tenant =
        new(new TenantContext(TestTenantId, "test", "Test", true, false));

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StartRunAsync_ThrowsWhenTheHostHasNoTenantAccessor()
    {
        var act = () => Admin(tenantAccessor: null).StartRunAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*without a resolved tenant*");
    }

    [Fact]
    public async Task StartRunAsync_ThrowsWhenNoTenantIsResolved()
    {
        var act = () => Admin(new DeduplicationModules.FixedTenantAccessor(null)).StartRunAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*without a resolved tenant*");
    }

    [Fact]
    public async Task StartRunAsync_ThrowsWithoutTheCallersReadContext()
    {
        var act = () => Admin(readContext: null).StartRunAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*read context*");
    }

    [Fact]
    public async Task StartRunAsync_RefusesAHistoryClampedCallerAndStoresNoRun()
    {
        var clamped = new CategoryReadContext();
        clamped.ClampMemberHistory();

        var act = () => Admin(readContext: clamped).StartRunAsync();

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await using var db = _db.CreateContext();
        (await db.DedupReconcileState.AnyAsync(s => s.Run != null)).Should().BeFalse("no run is requested");
    }

    [Fact]
    public async Task StartRunAsync_ReturnsTheRunStillPendingRatherThanASecondOne()
    {
        var admin = Admin();

        var first = await admin.StartRunAsync();
        var second = await admin.StartRunAsync();

        second.Should().Be(first);
        (await admin.GetRunStatusAsync(first))!.State.Should().Be(DeduplicationJobState.Pending);
    }

    [Fact]
    public async Task GetRunStatusAsync_HidesTheRunFromAnotherTenant()
    {
        var runId = await Admin().StartRunAsync();

        var stranger = DeduplicationModules.Admin(_db.CreateContext(OtherTenantId).AsFactory(), OtherTenantId);

        (await stranger.GetRunStatusAsync(runId)).Should().BeNull();
        (await stranger.CancelRunAsync(runId)).Should().BeFalse();
        (await Admin().GetRunStatusAsync(runId)).Should().NotBeNull();
    }

    [Fact]
    public async Task ARunLinksTheHistoryAndReportsWhatItDid()
    {
        await SeedAsync();
        var admin = Admin();

        var status = await admin.RunToEndAsync();

        status.State.Should().Be(DeduplicationJobState.Completed);
        status.CompletedAt.Should().NotBeNull();
        status.Result!.Success.Should().BeTrue();
        status.Result.TotalRecordsProcessed.Should().Be(4);
        status.Result.CarbIntakesProcessed.Should().Be(2);
        status.Result.BolusesProcessed.Should().Be(2);
        status.Result.DuplicateGroupsFound.Should().Be(2);
        status.Progress!.TotalRecords.Should().Be(4);

        (await admin.StartRunAsync()).Should().NotBe(status.JobId, "an ended run makes way for a new one");
    }

    [Fact]
    public async Task ARunInSlicesResumesWhereTheLastSliceEnded()
    {
        await SeedAsync();
        var sliced = Sliced();

        var runId = await sliced.StartRunAsync();
        var first = await sliced.RunAsync(null);
        first.Should().NotBeNull("a slice of no length ends after its first page");
        (await sliced.RunAsync(first)).Should().NotBeNull();

        // The slice that stored the second checkpoint died before the job ledger recorded where it
        // ended: the ledger hands the next claim the first cursor again.
        var at = await sliced.RunAsync(first);
        while (at is not null)
            at = await sliced.RunAsync(at);

        var status = (await sliced.GetRunStatusAsync(runId))!;
        status.State.Should().Be(DeduplicationJobState.Completed);
        status.Result!.TotalRecordsProcessed.Should().Be(4, "the slice run again counts its pages once");
        status.Result.BolusesProcessed.Should().Be(2);
        status.Result.CarbIntakesProcessed.Should().Be(2);
        await using var db = _db.CreateContext();
        (await db.LinkedRecords.Select(l => l.CanonicalId).Distinct().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CancellingARunThatHasNotStartedEndsItBeforeItReadsAnything()
    {
        await SeedAsync();
        var admin = Admin();
        var runId = await admin.StartRunAsync();

        (await admin.CancelRunAsync(runId)).Should().BeTrue();

        (await admin.RunAsync(null)).Should().BeNull();
        (await admin.GetRunStatusAsync(runId))!.State.Should().Be(DeduplicationJobState.Cancelled);
        await using var db = _db.CreateContext();
        (await db.LinkedRecords.AnyAsync()).Should().BeFalse("the cancelled run linked nothing");
        (await admin.CancelRunAsync(runId)).Should().BeFalse("the run has already ended");
    }

    [Fact]
    public async Task CancellingARunningRunStopsItAfterItsNextPage()
    {
        await SeedAsync();
        var admin = Sliced();
        var runId = await admin.StartRunAsync();
        var resume = await admin.RunAsync(null);
        (await admin.GetRunStatusAsync(runId))!.State.Should().Be(DeduplicationJobState.Running);

        (await admin.CancelRunAsync(runId)).Should().BeTrue();

        (await admin.RunAsync(resume)).Should().BeNull();
        var status = (await admin.GetRunStatusAsync(runId))!;
        status.State.Should().Be(DeduplicationJobState.Cancelled);
        status.Result.Should().BeNull();
        await using var db = _db.CreateContext();
        (await db.DedupReconcileState.SingleAsync()).RunCancelRequestedAt.Should().BeNull(
            "the run took the cancel");
    }

    /// <summary>
    /// The job ledger keeps the cursor of a run whose last slice ended it but died before the ledger
    /// recorded the end, and hands it to the next run requested on the same row.
    /// </summary>
    [Fact]
    public async Task ANewRunStartsFromTheBeginningWhateverCursorTheLedgerStillHolds()
    {
        await SeedAsync();
        await using (var db = _db.CreateContext())
        {
            db.LinkedRecords.Add(new LinkedRecordEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TestTenantId, CanonicalId = Guid.CreateVersion7(),
                RecordType = "bolus", RecordId = Guid.CreateVersion7(), SourceTimestamp = 0,
                DataSource = "gone-connector", IsPrimary = true, SysCreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var admin = Admin();
        var runId = await admin.StartRunAsync();
        var stale = new JobCursor(At: new DateTimeOffset(At.AddHours(1)), Page: 3);

        var at = await admin.RunAsync(stale);
        while (at is not null)
            at = await admin.RunAsync(at);

        var status = (await admin.GetRunStatusAsync(runId))!;
        status.State.Should().Be(DeduplicationJobState.Completed);
        status.Progress!.TotalRecords.Should().Be(4, "the run counted what it would process");
        status.Result!.TotalRecordsProcessed.Should().Be(4, "the run passed over every type");
        await using var check = _db.CreateContext();
        (await check.LinkedRecords.AnyAsync(l => l.DataSource == "gone-connector")).Should().BeFalse(
            "the run swept the orphaned link first");
    }

    /// <summary>
    /// A run's first slice marks it running and then dies before the job ledger records anything, so
    /// the next claim is handed the cursor an earlier run left.
    /// </summary>
    [Fact]
    public async Task ARunningRunHandedACursorItNeverReachedStartsFromTheBeginning()
    {
        await SeedAsync();
        var admin = Admin();
        var runId = await admin.StartRunAsync();
        await using (var db = _db.CreateContext())
        {
            await db.DedupReconcileState.ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Run, e => e.Run!.Replace("\"Pending\"", "\"Running\"")));
        }
        (await admin.GetRunStatusAsync(runId))!.State.Should().Be(DeduplicationJobState.Running);

        var at = await admin.RunAsync(new JobCursor(At: new DateTimeOffset(At.AddHours(1)), Page: 3));
        while (at is not null)
            at = await admin.RunAsync(at);

        var status = (await admin.GetRunStatusAsync(runId))!;
        status.Progress!.TotalRecords.Should().Be(4, "the run counted what it would process");
        status.Result!.TotalRecordsProcessed.Should().Be(4, "the run passed over every type");
    }

    /// <summary>
    /// The slice resumed from a cursor dies before the ledger records where it ended, twice in a row:
    /// the ledger hands the same cursor a third time, and the run must still resume there.
    /// </summary>
    [Fact]
    public async Task ARunThatLosesTwoLedgerFinishesInARowStillResumesWhereTheLedgerSays()
    {
        await SeedAsync();
        var sliced = Sliced();
        var runId = await sliced.StartRunAsync();
        var first = await sliced.RunAsync(null);
        var lost = await sliced.RunAsync(first);
        (await sliced.RunAsync(first)).Should().Be(lost, "the first lost finish resumed from the first cursor");

        var again = await sliced.RunAsync(first);
        again.Should().Be(lost, "the run resumed from the first cursor after a second lost finish");
        var at = again;
        while (at is not null)
            at = await sliced.RunAsync(at);

        var status = (await sliced.GetRunStatusAsync(runId))!;
        status.Result!.TotalRecordsProcessed.Should().Be(4);
        status.Result.BolusesProcessed.Should().Be(2);
        status.Result.CarbIntakesProcessed.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_EndsAtOnceWhenNoRunIsRequested() =>
        (await Admin().RunAsync(null)).Should().BeNull();

    private DeduplicationAdmin Admin() => DeduplicationModules.Admin(_db.CreateContext().AsFactory(), TestTenantId);

    /// <summary>An admin whose every slice ends after its first page.</summary>
    private DeduplicationAdmin Sliced() =>
        new(_db.CreateContext().AsFactory(), DeduplicationModules.Linker(_db.CreateContext().AsFactory()),
            DeduplicationModules.Reconciler(_db.CreateContext().AsFactory()), NullLogger<DeduplicationAdmin>.Instance,
            Tenant, new CategoryReadContext()) { SliceLength = TimeSpan.Zero };

    private DeduplicationAdmin Admin(ITenantAccessor? tenantAccessor) =>
        new(_db.CreateContext().AsFactory(), DeduplicationModules.Linker(_db.CreateContext().AsFactory()),
            DeduplicationModules.Reconciler(_db.CreateContext().AsFactory()), NullLogger<DeduplicationAdmin>.Instance,
            tenantAccessor, new CategoryReadContext());

    private DeduplicationAdmin Admin(ICategoryReadContext? readContext) =>
        new(_db.CreateContext().AsFactory(), DeduplicationModules.Linker(_db.CreateContext().AsFactory()),
            DeduplicationModules.Reconciler(_db.CreateContext().AsFactory()), NullLogger<DeduplicationAdmin>.Instance,
            Tenant, readContext);

    /// <summary>A dose and a meal, each reported by two connectors seconds apart.</summary>
    private async Task SeedAsync()
    {
        await using var db = _db.CreateContext();
        db.Boluses.AddRange(
            new BolusEntity { Id = Guid.CreateVersion7(), TenantId = TestTenantId, Timestamp = At, Insulin = 2, DataSource = "mylife-connector" },
            new BolusEntity { Id = Guid.CreateVersion7(), TenantId = TestTenantId, Timestamp = At.AddSeconds(5), Insulin = 2, DataSource = "glooko-connector" });
        db.CarbIntakes.AddRange(
            new CarbIntakeEntity { Id = Guid.CreateVersion7(), TenantId = TestTenantId, Timestamp = At, Carbs = 40, DataSource = "mylife-connector" },
            new CarbIntakeEntity { Id = Guid.CreateVersion7(), TenantId = TestTenantId, Timestamp = At.AddSeconds(5), Carbs = 40, DataSource = "glooko-connector" });
        await db.SaveChangesAsync();
    }
}
