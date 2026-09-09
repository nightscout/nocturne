using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// <c>BulkUpsertByLegacyIdAsync</c> is the batch form of get-by-legacy-id then create-or-update, so
/// every rule the per-record pair enforces has to hold for the batch: existing rows update in place,
/// new rows insert, a held identity is refused, a stored correlation id is kept only when asked, and
/// an unchanged re-upsert is silent.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class TherapySettingsRepositoryBulkUpsertTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly NocturneDbContext _context;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly TherapySettingsRepository _repository;

    public TherapySettingsRepositoryBulkUpsertTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext($"therapy_settings_bulk_upsert_{Guid.NewGuid()}");
        _context.TenantId = Tenant;
        _repository = new TherapySettingsRepository(
            new TestTenantDbContextFactory(_context),
            new SystemAuditContext(),
            NullLogger<TherapySettingsRepository>.Instance,
            _broadcaster);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TherapySettings Settings(string legacyId, double dia = 3.0, Guid? correlationId = null) => new()
    {
        LegacyId = legacyId,
        Timestamp = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
        ProfileName = legacyId.Split(':')[^1],
        Dia = dia,
        CorrelationId = correlationId,
    };

    [Fact]
    public async Task BulkUpsert_InsertsNewRecords_AndReportsThemCreated()
    {
        var outcomes = await _repository.BulkUpsertByLegacyIdAsync(
            [Settings("p1:Default"), Settings("p1:Weekend")], WriteOrigin.Live);

        outcomes.Keys.Should().BeEquivalentTo(["p1:Default", "p1:Weekend"]);
        outcomes.Values.Should().AllSatisfy(o => o.Created.Should().BeTrue());
        _context.TherapySettings.Count().Should().Be(2);
        _broadcaster.Created.Should().HaveCount(2);
        _broadcaster.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkUpsert_UpdatesAStoredRowInPlace_AndWritesItsIdBack()
    {
        var first = await _repository.BulkUpsertByLegacyIdAsync([Settings("p1:Default", dia: 3.0)], WriteOrigin.Live);
        var storedId = first["p1:Default"].Record.Id;
        var changed = Settings("p1:Default", dia: 4.5);

        var outcomes = await _repository.BulkUpsertByLegacyIdAsync([changed], WriteOrigin.Live);

        outcomes["p1:Default"].Created.Should().BeFalse();
        outcomes["p1:Default"].Record.Id.Should().Be(storedId);
        changed.Id.Should().Be(storedId, "the update path hands the caller the stored identity");
        _context.TherapySettings.Count().Should().Be(1);
        _context.TherapySettings.Single().Dia.Should().Be(4.5);
        _broadcaster.Updated.Should().ContainSingle();
    }

    /// <summary>
    /// A connector re-publishes its whole profile set every cycle; a byte-identical re-send must
    /// neither write nor broadcast, the same gate <c>UpdateAsync</c> applies per record.
    /// </summary>
    [Fact]
    public async Task BulkUpsert_UnchangedReUpsert_IsReportedUpdatedButBroadcastsNothing()
    {
        await _repository.BulkUpsertByLegacyIdAsync([Settings("p1:Default")], WriteOrigin.Live);
        _broadcaster.Clear();

        var outcomes = await _repository.BulkUpsertByLegacyIdAsync([Settings("p1:Default")], WriteOrigin.Live);

        outcomes["p1:Default"].Created.Should().BeFalse();
        _broadcaster.Created.Should().BeEmpty();
        _broadcaster.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkUpsert_MixedBatch_InsertsAndUpdatesTogether()
    {
        await _repository.BulkUpsertByLegacyIdAsync([Settings("p1:Default", dia: 3.0)], WriteOrigin.Live);

        var outcomes = await _repository.BulkUpsertByLegacyIdAsync(
            [Settings("p1:Default", dia: 5.0), Settings("p1:Weekend")], WriteOrigin.Live);

        outcomes["p1:Default"].Created.Should().BeFalse();
        outcomes["p1:Weekend"].Created.Should().BeTrue();
        _context.TherapySettings.Count().Should().Be(2);
        _context.TherapySettings.Single(t => t.LegacyId == "p1:Default").Dia.Should().Be(5.0);
    }

    [Fact]
    public async Task BulkUpsert_KeepsTheStoredCorrelationId_OnlyWhenAsked()
    {
        var stored = Guid.CreateVersion7();
        await _repository.BulkUpsertByLegacyIdAsync([Settings("p1:Default", correlationId: stored)], WriteOrigin.Live);

        var preserving = await _repository.BulkUpsertByLegacyIdAsync(
            [Settings("p1:Default", correlationId: Guid.CreateVersion7())], WriteOrigin.Live,
            preserveStoredCorrelationId: true);
        preserving["p1:Default"].Record.CorrelationId.Should().Be(stored);
        StoredCorrelationId("p1:Default").Should().Be(stored);

        // A correlation id is bookkeeping the audit gate ignores, so this write changes nothing the
        // broadcast counts as material and still has to reach the row: it is the sibling-convergence
        // repair the anchor's preservation exists for.
        var fresh = Guid.CreateVersion7();
        var overwriting = await _repository.BulkUpsertByLegacyIdAsync(
            [Settings("p1:Default", correlationId: fresh)], WriteOrigin.Live);
        overwriting["p1:Default"].Record.CorrelationId.Should().Be(fresh);
        StoredCorrelationId("p1:Default").Should().Be(fresh, "a correlation-id-only change must be persisted");
        _broadcaster.Updated.Should().BeEmpty("a correlation-id-only change is not a material update");
    }

    private Guid? StoredCorrelationId(string legacyId)
    {
        _context.ChangeTracker.Clear();
        return _context.TherapySettings.AsNoTracking().Single(t => t.LegacyId == legacyId).CorrelationId;
    }

    /// <summary>
    /// An empty stored id is a client artefact, not an identity; freezing it would merge the group
    /// with every other group carrying it.
    /// </summary>
    [Fact]
    public async Task BulkUpsert_DoesNotPreserveAnEmptyStoredCorrelationId()
    {
        await _repository.BulkUpsertByLegacyIdAsync([Settings("p1:Default", correlationId: Guid.Empty)], WriteOrigin.Live);
        var minted = Guid.CreateVersion7();

        var outcomes = await _repository.BulkUpsertByLegacyIdAsync(
            [Settings("p1:Default", correlationId: minted)], WriteOrigin.Live, preserveStoredCorrelationId: true);

        outcomes["p1:Default"].Record.CorrelationId.Should().Be(minted);
        StoredCorrelationId("p1:Default").Should().Be(minted, "the empty id self-heals on the row, not only in the answer");
    }

    /// <summary>
    /// A row the user deleted holds its legacy id against re-creation, exactly as
    /// <c>CreateAsync</c> refuses it with <see cref="RecreationBlockedException"/>; the batch drops the
    /// record instead of throwing, so its siblings still land.
    /// </summary>
    [Fact]
    public async Task BulkUpsert_DropsARecordWhoseIdentityIsHeldByAUserTombstone()
    {
        var created = await _repository.BulkUpsertByLegacyIdAsync([Settings("p1:Default")], WriteOrigin.Live);
        var entity = _context.TherapySettings.Single(t => t.Id == created["p1:Default"].Record.Id);
        entity.DeletedAt = DateTime.UtcNow;
        _context.Entry(entity).Property("DeletedByUser").CurrentValue = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var outcomes = await _repository.BulkUpsertByLegacyIdAsync(
            [Settings("p1:Default"), Settings("p1:Weekend")], WriteOrigin.Live);

        outcomes.Keys.Should().BeEquivalentTo(["p1:Weekend"]);
        _context.TherapySettings.IgnoreQueryFilters().Count().Should().Be(2, "the tombstone and the new sibling");
    }

    [Fact]
    public async Task BulkUpsert_IgnoresRecordsWithoutALegacyId_AndKeepsTheLastOfADuplicate()
    {
        var outcomes = await _repository.BulkUpsertByLegacyIdAsync(
            [Settings("p1:Default", dia: 1.0), Settings("p1:Default", dia: 2.0), new TherapySettings { ProfileName = "loose" }],
            WriteOrigin.Live);

        outcomes.Should().ContainSingle();
        outcomes["p1:Default"].Record.Dia.Should().Be(2.0);
        _context.TherapySettings.Count().Should().Be(1);
    }

    private sealed class RecordingBroadcaster : IV4RecordBroadcaster<TherapySettings>
    {
        public List<TherapySettings> Created { get; } = [];
        public List<TherapySettings> Updated { get; } = [];

        public Task BroadcastCreatedAsync(IReadOnlyList<TherapySettings> items, CancellationToken ct = default)
        {
            Created.AddRange(items);
            return Task.CompletedTask;
        }

        public Task BroadcastUpdatedAsync(IReadOnlyList<TherapySettings> items, CancellationToken ct = default)
        {
            Updated.AddRange(items);
            return Task.CompletedTask;
        }

        public void Clear()
        {
            Created.Clear();
            Updated.Clear();
        }
    }
}
