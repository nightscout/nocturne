using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The delete contract every glucose repository owes: the source predicate reaches both origin
/// handles a row can carry (<see cref="Nocturne.Infrastructure.Data.Extensions.SourceFilter"/>),
/// and the delete runs through the audited path so a user delete stamps <c>deleted_by_user</c> —
/// the discriminator that stops the next connector sync re-importing the rows
/// (<see cref="Nocturne.Infrastructure.Data.Extensions.SoftDeleteDedupExtensions"/>).
/// </summary>
public abstract class GlucoseSourceDeleteAuditTests<TEntity> : AuditedSoftDeleteTestBase<TEntity>
    where TEntity : class, ISoftDeletable, ISourcedEntity
{
    /// <summary>A row of the repository's type, unsaved.</summary>
    protected abstract TEntity NewRow(Guid id, DateTime timestamp, string? dataSource, string? device);

    protected abstract Task<int> DeleteBySourceAsync(string source, WriteOrigin origin);

    protected abstract Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, WriteOrigin origin);

    private Mock<IDataEventSink<Entry>>? _entrySink;

    // Read by CreateRepository from the base constructor, before this class's initialisers run.
    protected Mock<IDataEventSink<Entry>> EntrySink => _entrySink ??= new();

    private Task<int> DeleteBySourceAsync(string source) => DeleteBySourceAsync(source, WriteOrigin.Live);

    private Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to) =>
        DeleteByTimeRangeAsync(from, to, WriteOrigin.Live);

    private Guid Seed(DateTime timestamp, string? dataSource = null, string? device = null) =>
        Add(NewRow(Guid.CreateVersion7(), timestamp, dataSource, device));

    [Fact]
    public async Task DeleteBySourceAsync_DeletesRowsUnderEitherOriginHandle_AndSparesOtherSources()
    {
        var byDataSource = Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        var byDevice = Seed(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), dataSource: "other", device: "dexcom");
        var unrelated = Seed(new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), dataSource: "libre", device: "Libre3");

        var deleted = await DeleteBySourceAsync("dexcom");

        deleted.Should().Be(2);
        (await ReadDeleteStateAsync(byDataSource)).DeletedAt.Should().NotBeNull();
        (await ReadDeleteStateAsync(byDevice)).DeletedAt.Should().NotBeNull();
        (await ReadDeleteStateAsync(unrelated)).DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteBySourceAsync_UserContext_StampsDeletedByUserAndWritesARowPerRecord()
    {
        Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        var byDevice = Seed(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), device: "dexcom");

        var deleted = await DeleteBySourceAsync("dexcom");

        deleted.Should().Be(2);
        (await ReadDeleteStateAsync(byDevice)).DeletedByUser.Should().BeTrue();

        var log = await ReadAuditLogAsync();
        log.Should().HaveCount(2).And.OnlyContain(l =>
            l.Action == "delete" && l.EntityType == AuditEntityType && l.SubjectName == "tester");
    }

    [Fact]
    public async Task DeleteBySourceAsync_SystemContext_LeavesRowsReimportable()
    {
        var id = Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        UseAuditContext(SystemAuditContext.ForService("connector:dexcom"));

        var deleted = await DeleteBySourceAsync("dexcom");

        deleted.Should().Be(1);
        var state = await ReadDeleteStateAsync(id);
        state.DeletedAt.Should().NotBeNull();
        state.DeletedByUser.Should().BeFalse();
        (await ReadAuditLogAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteByTimeRangeAsync_ExactDateWindow_DeletesTheBoundaryRow()
    {
        // The v1 entries bulk remove parses an exact-date / $lte find into FindQuery.ToMills,
        // which is an inclusive upper bound: the row at that millisecond is part of the matched
        // set. The sweep must delete it. An exclusive `Timestamp < to` drops it, so an exact-date
        // bulk remove reports success while leaving the row behind.
        var boundary = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var justAfter = Seed(boundary.AddMilliseconds(1), dataSource: "dexcom");
        var atBoundary = Seed(boundary, dataSource: "dexcom");
        var beforeBoundary = Seed(boundary.AddMilliseconds(-1), dataSource: "dexcom");

        var deleted = await DeleteByTimeRangeAsync(boundary, boundary);

        deleted.Should().Be(1, "the exact-date window is [boundary, boundary] and inclusive at both ends");
        (await ReadDeleteStateAsync(atBoundary)).DeletedAt.Should().NotBeNull("the row at the exact date is in scope");
        (await ReadDeleteStateAsync(beforeBoundary)).DeletedAt.Should().BeNull("rows before the window are out of scope");
        (await ReadDeleteStateAsync(justAfter)).DeletedAt.Should().BeNull("rows after the window are out of scope");
    }

    [Fact]
    public async Task DeleteByTimeRangeAsync_UserContext_StampsDeletedByUserAndWritesARowPerRecord()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
        var beforeFrom = Seed(from.AddHours(-1), dataSource: "dexcom");
        var atLowerBound = Seed(from, dataSource: "dexcom");
        var inRange = Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        var atUpperBound = Seed(to, dataSource: "dexcom");
        var afterTo = Seed(to.AddMilliseconds(1), dataSource: "dexcom");

        var deleted = await DeleteByTimeRangeAsync(from, to);

        deleted.Should().Be(3);
        (await ReadDeleteStateAsync(inRange)).DeletedByUser.Should().BeTrue();
        (await ReadDeleteStateAsync(atLowerBound)).DeletedAt.Should().NotBeNull("the lower bound is inclusive");
        (await ReadDeleteStateAsync(beforeFrom)).DeletedAt.Should().BeNull("rows before the window are out of scope");
        (await ReadDeleteStateAsync(atUpperBound)).DeletedAt.Should().NotBeNull("the upper bound is inclusive");
        (await ReadDeleteStateAsync(afterTo)).DeletedAt.Should().BeNull("rows after the window are out of scope");

        var log = await ReadAuditLogAsync();
        log.Should().HaveCount(3).And.OnlyContain(l =>
            l.Action == "delete" && l.EntityType == AuditEntityType && l.SubjectName == "tester");
    }

    [Fact]
    public async Task DeleteBySourceAsync_Live_TellsTheEntrySinkWhichReadingsWereDeleted()
    {
        var at = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        Seed(at, dataSource: "dexcom");
        Seed(at.AddMinutes(5), dataSource: "libre");

        await DeleteBySourceAsync("dexcom", WriteOrigin.Live);

        EntrySink.Verify(s => s.OnWrittenAsync(
            It.Is<IReadOnlyList<Entry>>(e => e.Count == 1 && e[0].Mills == new DateTimeOffset(at).ToUnixTimeMilliseconds()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteByTimeRangeAsync_Live_TellsTheEntrySinkWhichReadingsWereDeleted()
    {
        var at = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        Seed(at, dataSource: "dexcom");
        Seed(at.AddDays(2), dataSource: "dexcom");

        await DeleteByTimeRangeAsync(at.AddHours(-1), at.AddHours(1), WriteOrigin.Live);

        EntrySink.Verify(s => s.OnWrittenAsync(
            It.Is<IReadOnlyList<Entry>>(e => e.Count == 1 && e[0].Mills == new DateTimeOffset(at).ToUnixTimeMilliseconds()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteBySourceAsync_LiveBeyondTheBroadcastCap_RaisesTheBulkDeleteSignal()
    {
        SeedMany(AuditedBulkDeleteExtensions.BroadcastMaterializationCap + 1, "dexcom");

        var deleted = await DeleteBySourceAsync("dexcom", WriteOrigin.Live);

        EntrySink.Verify(s => s.OnBulkDeletedAsync(deleted, It.IsAny<CancellationToken>()), Times.Once);
        var log = (await ReadAuditLogAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be("bulk_delete");
        ReadSummary(log.ChangesJson).Should().Be((deleted, "data_source=dexcom"));
    }

    [Fact]
    public async Task DeleteByTimeRangeAsync_LiveBeyondTheBroadcastCap_RaisesTheBulkDeleteSignal()
    {
        SeedMany(AuditedBulkDeleteExtensions.BroadcastMaterializationCap + 1, "dexcom");

        var deleted = await DeleteByTimeRangeAsync(null, null, WriteOrigin.Live);

        EntrySink.Verify(s => s.OnBulkDeletedAsync(deleted, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteBySourceAsync_BackfillBeyondTheBroadcastCap_RaisesNoBulkDeleteSignal()
    {
        SeedMany(AuditedBulkDeleteExtensions.BroadcastMaterializationCap + 1, "dexcom");

        await DeleteBySourceAsync("dexcom", WriteOrigin.Backfill);

        EntrySink.Verify(s => s.OnBulkDeletedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Deletes_MatchingNothing_RaiseNothing()
    {
        Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "libre");

        await DeleteBySourceAsync("dexcom", WriteOrigin.Live);
        await DeleteByTimeRangeAsync(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, WriteOrigin.Live);

        EntrySink.VerifyNoOtherCalls();
    }

    private void SeedMany(int count, string dataSource)
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
            Seed(start.AddMinutes(i), dataSource);
    }
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class CalibrationRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<CalibrationEntity>
{
    private CalibrationRepository _repository = null!;

    protected override string AuditEntityType => "Calibration";

    protected override void CreateRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new CalibrationRepository(contextFactory, auditContext, Logger<CalibrationRepository>(), entrySink: EntrySink.Object);

    protected override CalibrationEntity NewRow(Guid id, DateTime timestamp, string? dataSource, string? device) =>
        new()
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            DataSource = dataSource,
            Device = device,
            Slope = 1.0,
        };

    protected override Task<int> DeleteBySourceAsync(string source, WriteOrigin origin) =>
        _repository.DeleteBySourceAsync(source, origin);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, WriteOrigin origin) =>
        _repository.DeleteByTimeRangeAsync(from, to, origin);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class MeterGlucoseRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<MeterGlucoseEntity>
{
    private MeterGlucoseRepository _repository = null!;

    protected override string AuditEntityType => "MeterGlucose";

    protected override void CreateRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new MeterGlucoseRepository(contextFactory, auditContext, Logger<MeterGlucoseRepository>(), entrySink: EntrySink.Object);

    protected override MeterGlucoseEntity NewRow(Guid id, DateTime timestamp, string? dataSource, string? device) =>
        new()
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            DataSource = dataSource,
            Device = device,
            Mgdl = 120,
        };

    protected override Task<int> DeleteBySourceAsync(string source, WriteOrigin origin) =>
        _repository.DeleteBySourceAsync(source, origin);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, WriteOrigin origin) =>
        _repository.DeleteByTimeRangeAsync(from, to, origin);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<SensorGlucoseEntity>
{
    private SensorGlucoseRepository _repository = null!;

    protected override string AuditEntityType => "SensorGlucose";

    protected override void CreateRepository(INocturneDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new SensorGlucoseRepository(
            contextFactory,
            new Mock<IDeduplicationService>().Object,
            auditContext,
            Logger<SensorGlucoseRepository>(),
            entrySink: EntrySink.Object);

    protected override SensorGlucoseEntity NewRow(Guid id, DateTime timestamp, string? dataSource, string? device) =>
        new()
        {
            Id = id,
            TenantId = TestTenantId,
            Timestamp = timestamp,
            DataSource = dataSource,
            Device = device,
            Mgdl = 120,
        };

    protected override Task<int> DeleteBySourceAsync(string source, WriteOrigin origin) =>
        _repository.DeleteBySourceAsync(source, origin);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to, WriteOrigin origin) =>
        _repository.DeleteByTimeRangeAsync(from, to, origin);
}
