using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// The delete contract owed by the repositories whose delete is keyed on something other than the
/// data source — a legacy-id prefix, a (data source, sync identifier) pair, a correlation id. Each
/// must reach exactly the rows its key names, and must run through the audited path so a user delete
/// stamps <c>deleted_by_user</c> and records one <c>bulk_delete</c> summary row while a system sweep
/// leaves the rows re-importable
/// (<see cref="Nocturne.Infrastructure.Data.Extensions.SoftDeleteDedupExtensions"/>).
/// </summary>
public abstract class KeyedSoftDeleteAuditTests<TEntity> : AuditedSoftDeleteTestBase<TEntity>
    where TEntity : class, ISoftDeletable
{
    /// <summary>Seeds one row the delete's key names, and returns its id.</summary>
    protected abstract Guid SeedMatch();

    /// <summary>
    /// Seeds the rows that sit just outside the key — the boundary the predicate must not cross.
    /// Returns their ids.
    /// </summary>
    protected abstract IReadOnlyList<Guid> SeedNearMisses();

    /// <summary>Issues the delete for the key <see cref="SeedMatch"/> seeds against.</summary>
    protected abstract Task<int> DeleteAsync();

    /// <summary>The scope string the summary row must carry for that key.</summary>
    protected abstract string ExpectedScope { get; }

    [Fact]
    public async Task Delete_ReachesTheKeyedRow_AndSparesTheNearMisses()
    {
        var match = SeedMatch();
        var nearMisses = SeedNearMisses();

        var deleted = await DeleteAsync();

        deleted.Should().Be(1);
        (await ReadDeleteStateAsync(match)).DeletedAt.Should().NotBeNull();
        foreach (var id in nearMisses)
            (await ReadDeleteStateAsync(id)).DeletedAt.Should().BeNull(
                "a row outside the delete's key must survive");
    }

    [Fact]
    public async Task Delete_UserContext_StampsDeletedByUserAndWritesSummaryRow()
    {
        var match = SeedMatch();

        var deleted = await DeleteAsync();

        deleted.Should().Be(1);
        (await ReadDeleteStateAsync(match)).DeletedByUser.Should().BeTrue();

        var log = (await ReadAuditLogAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be("bulk_delete");
        log.EntityType.Should().Be(AuditEntityType);
        log.EntityId.Should().BeNull();
        log.SubjectName.Should().Be("tester");
        ReadSummary(log.ChangesJson).Should().Be((1, ExpectedScope));
    }

    [Fact]
    public async Task Delete_SystemContext_LeavesRowsReimportable()
    {
        var match = SeedMatch();
        UseAuditContext(SystemAuditContext.ForService("connector:dexcom"));

        var deleted = await DeleteAsync();

        deleted.Should().Be(1);
        var state = await ReadDeleteStateAsync(match);
        state.DeletedAt.Should().NotBeNull();
        state.DeletedByUser.Should().BeFalse();
        (await ReadAuditLogAsync()).Should().BeEmpty();
    }
}

/// <summary>
/// Shared shape for the four profile schedule repositories, whose <c>DeleteByLegacyIdPrefixAsync</c>
/// is one copy-paste each. The near misses pin <c>StartsWith</c>: a legacy id that merely contains
/// the prefix, one that shares a leading substring with it, and a row with no legacy id at all.
/// </summary>
public abstract class LegacyIdPrefixDeleteAuditTests<TEntity> : KeyedSoftDeleteAuditTests<TEntity>
    where TEntity : class, ISoftDeletable
{
    protected const string Prefix = "profile:morning:";

    protected override string ExpectedScope => $"legacy_id_prefix={Prefix}";

    /// <summary>A row of the repository's type carrying <paramref name="legacyId"/>, unsaved.</summary>
    protected abstract TEntity NewRow(string? legacyId);

    protected override Guid SeedMatch() => Add(NewRow($"{Prefix}0"));

    protected override IReadOnlyList<Guid> SeedNearMisses() =>
    [
        Add(NewRow($"copy-of-{Prefix}0")),
        Add(NewRow("profile:morningstar:0")),
        Add(NewRow("profile:evening:0")),
        Add(NewRow(null)),
    ];
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class CarbRatioScheduleRepositoryPrefixDeleteTests
    : LegacyIdPrefixDeleteAuditTests<CarbRatioScheduleEntity>
{
    private CarbRatioScheduleRepository _repository = null!;

    protected override string AuditEntityType => "CarbRatioSchedule";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new CarbRatioScheduleRepository(
            contextFactory, auditContext, Logger<CarbRatioScheduleRepository>());

    protected override CarbRatioScheduleEntity NewRow(string? legacyId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            ProfileName = "morning",
            LegacyId = legacyId,
        };

    protected override Task<int> DeleteAsync() =>
        _repository.DeleteByLegacyIdPrefixAsync(Prefix, WriteOrigin.Live);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class SensitivityScheduleRepositoryPrefixDeleteTests
    : LegacyIdPrefixDeleteAuditTests<SensitivityScheduleEntity>
{
    private SensitivityScheduleRepository _repository = null!;

    protected override string AuditEntityType => "SensitivitySchedule";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new SensitivityScheduleRepository(
            contextFactory, auditContext, Logger<SensitivityScheduleRepository>());

    protected override SensitivityScheduleEntity NewRow(string? legacyId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            ProfileName = "morning",
            LegacyId = legacyId,
        };

    protected override Task<int> DeleteAsync() =>
        _repository.DeleteByLegacyIdPrefixAsync(Prefix, WriteOrigin.Live);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class TargetRangeScheduleRepositoryPrefixDeleteTests
    : LegacyIdPrefixDeleteAuditTests<TargetRangeScheduleEntity>
{
    private TargetRangeScheduleRepository _repository = null!;

    protected override string AuditEntityType => "TargetRangeSchedule";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new TargetRangeScheduleRepository(
            contextFactory, auditContext, Logger<TargetRangeScheduleRepository>());

    protected override TargetRangeScheduleEntity NewRow(string? legacyId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            ProfileName = "morning",
            LegacyId = legacyId,
        };

    protected override Task<int> DeleteAsync() =>
        _repository.DeleteByLegacyIdPrefixAsync(Prefix, WriteOrigin.Live);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class TherapySettingsRepositoryPrefixDeleteTests
    : LegacyIdPrefixDeleteAuditTests<TherapySettingsEntity>
{
    private TherapySettingsRepository _repository = null!;

    protected override string AuditEntityType => "TherapySettings";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new TherapySettingsRepository(
            contextFactory, auditContext, Logger<TherapySettingsRepository>());

    protected override TherapySettingsEntity NewRow(string? legacyId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            ProfileName = "morning",
            Dia = 5.0,
            LegacyId = legacyId,
        };

    protected override Task<int> DeleteAsync() =>
        _repository.DeleteByLegacyIdPrefixAsync(Prefix, WriteOrigin.Live);
}

/// <summary>
/// The connector-facing note delete. Both halves of the key are load-bearing, so the near misses
/// vary one at a time: same sync identifier under another source, and another sync identifier under
/// the same source.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class NoteRepositorySyncIdentifierDeleteTests : KeyedSoftDeleteAuditTests<NoteEntity>
{
    private const string DataSource = "dexcom";
    private const string SyncIdentifier = "note-42";

    private NoteRepository _repository = null!;

    protected override string AuditEntityType => "Note";

    protected override string ExpectedScope => $"sync_identifier={DataSource}/{SyncIdentifier}";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new NoteRepository(
            contextFactory,
            new Mock<IDeduplicationService>().Object,
            auditContext,
            Logger<NoteRepository>());

    private NoteEntity NewRow(string? dataSource, string? syncIdentifier) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            Text = "note",
            DataSource = dataSource,
            SyncIdentifier = syncIdentifier,
        };

    protected override Guid SeedMatch() => Add(NewRow(DataSource, SyncIdentifier));

    protected override IReadOnlyList<Guid> SeedNearMisses() =>
    [
        Add(NewRow("libre", SyncIdentifier)),
        Add(NewRow(DataSource, "note-43")),
        Add(NewRow(DataSource, null)),
        Add(NewRow(null, SyncIdentifier)),
    ];

    protected override Task<int> DeleteAsync() =>
        _repository.DeleteBySyncIdentifierAsync(DataSource, SyncIdentifier, WriteOrigin.Live);
}

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class DeviceStatusExtrasRepositoryCorrelationDeleteTests
    : KeyedSoftDeleteAuditTests<DeviceStatusExtrasEntity>
{
    private static readonly Guid CorrelationId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private DeviceStatusExtrasRepository _repository = null!;

    protected override string AuditEntityType => "DeviceStatusExtras";

    protected override string ExpectedScope => $"correlation_id={CorrelationId}";

    protected override void CreateRepository(ITenantDbContextFactory contextFactory, IAuditContext auditContext) =>
        _repository = new DeviceStatusExtrasRepository(contextFactory, auditContext);

    private DeviceStatusExtrasEntity NewRow(Guid correlationId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            CorrelationId = correlationId,
            ExtrasJson = "{}",
        };

    protected override Guid SeedMatch() => Add(NewRow(CorrelationId));

    protected override IReadOnlyList<Guid> SeedNearMisses() =>
    [
        Add(NewRow(Guid.Parse("22222222-2222-2222-2222-222222222222"))),
        Add(NewRow(Guid.Empty)),
    ];

    protected override Task<int> DeleteAsync() => _repository.DeleteByCorrelationIdAsync(CorrelationId);
}
