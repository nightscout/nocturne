using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities.V4;
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

    protected abstract Task<int> DeleteBySourceAsync(string source);

    protected abstract Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to);

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
    public async Task DeleteBySourceAsync_UserContext_StampsDeletedByUserAndWritesSummaryRow()
    {
        Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        var byDevice = Seed(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc), device: "dexcom");

        var deleted = await DeleteBySourceAsync("dexcom");

        deleted.Should().Be(2);
        (await ReadDeleteStateAsync(byDevice)).DeletedByUser.Should().BeTrue();

        var log = (await ReadAuditLogAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be("bulk_delete");
        log.EntityType.Should().Be(AuditEntityType);
        log.EntityId.Should().BeNull();
        log.SubjectName.Should().Be("tester");
        ReadSummary(log.ChangesJson).Should().Be((2, "data_source=dexcom"));
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
    public async Task DeleteByTimeRangeAsync_UserContext_StampsDeletedByUserAndWritesSummaryRow()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
        var beforeFrom = Seed(from.AddHours(-1), dataSource: "dexcom");
        var atLowerBound = Seed(from, dataSource: "dexcom");
        var inRange = Seed(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), dataSource: "dexcom");
        var atUpperBound = Seed(to, dataSource: "dexcom");

        var deleted = await DeleteByTimeRangeAsync(from, to);

        deleted.Should().Be(2);
        (await ReadDeleteStateAsync(inRange)).DeletedByUser.Should().BeTrue();
        (await ReadDeleteStateAsync(atLowerBound)).DeletedAt.Should().NotBeNull("the lower bound is inclusive");
        (await ReadDeleteStateAsync(beforeFrom)).DeletedAt.Should().BeNull("rows before the window are out of scope");
        (await ReadDeleteStateAsync(atUpperBound)).DeletedAt.Should().BeNull("the upper bound is exclusive");

        var log = (await ReadAuditLogAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be("bulk_delete");
        log.EntityType.Should().Be(AuditEntityType);
        ReadSummary(log.ChangesJson).Should().Be((2, $"timestamp={from:O}..{to:O}"));
    }
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class CalibrationRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<CalibrationEntity>
{
    private CalibrationRepository _repository = null!;

    protected override string AuditEntityType => "Calibration";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new CalibrationRepository(contextFactory, auditContext, Logger<CalibrationRepository>());

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

    protected override Task<int> DeleteBySourceAsync(string source) => _repository.DeleteBySourceAsync(source);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to) =>
        _repository.DeleteByTimeRangeAsync(from, to);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class MeterGlucoseRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<MeterGlucoseEntity>
{
    private MeterGlucoseRepository _repository = null!;

    protected override string AuditEntityType => "MeterGlucose";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new MeterGlucoseRepository(contextFactory, auditContext, Logger<MeterGlucoseRepository>());

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

    protected override Task<int> DeleteBySourceAsync(string source) => _repository.DeleteBySourceAsync(source);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to) =>
        _repository.DeleteByTimeRangeAsync(from, to);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
[Trait("Category", "SensorGlucose")]
public class SensorGlucoseRepositorySourceDeleteTests : GlucoseSourceDeleteAuditTests<SensorGlucoseEntity>
{
    private SensorGlucoseRepository _repository = null!;

    protected override string AuditEntityType => "SensorGlucose";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new SensorGlucoseRepository(
            contextFactory,
            new Mock<IDeduplicationService>().Object,
            auditContext,
            Logger<SensorGlucoseRepository>());

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

    protected override Task<int> DeleteBySourceAsync(string source) => _repository.DeleteBySourceAsync(source);

    protected override Task<int> DeleteByTimeRangeAsync(DateTime? from, DateTime? to) =>
        _repository.DeleteByTimeRangeAsync(from, to);
}
