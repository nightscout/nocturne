using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// Supersession and deduplication over exclusive-category state spans, through the repository's
/// upsert with the real deduplication pass behind it.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Deduplication")]
public class StateSpanDedupSupersessionTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime T = new(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly DeduplicationService _dedup;
    private readonly StateSpanRepository _repository;

    public StateSpanDedupSupersessionTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId);
        _context = _db.CreateContext();
        _context.TenantId = TestTenantId;

        var dedupContext = _db.CreateContext();
        dedupContext.TenantId = TestTenantId;
        _dedup = new DeduplicationService(
            dedupContext.AsFactory(), new Mock<IServiceScopeFactory>().Object, NullLogger<DeduplicationService>.Instance);

        _repository = new StateSpanRepository(
            _context.AsFactory(), _dedup, new Mock<IAuditContext>().Object, NullLogger<StateSpanRepository>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static StateSpan PumpMode(string originalId, int seconds) => new()
    {
        Category = StateSpanCategory.PumpMode,
        State = PumpModeState.Automatic.ToString(),
        StartTimestamp = T.AddSeconds(seconds),
        Source = "loop://iPhone",
        OriginalId = originalId,
    };

    private static StateSpan Preset(StateSpanCategory category, string originalId, int seconds, string name) => new()
    {
        Category = category,
        State = category switch
        {
            StateSpanCategory.Override => OverrideState.Custom.ToString(),
            StateSpanCategory.TemporaryTarget => TemporaryTargetState.Active.ToString(),
            _ => ProfileState.Active.ToString(),
        },
        StartTimestamp = T.AddSeconds(seconds),
        Source = "Loop",
        OriginalId = originalId,
        Metadata = category == StateSpanCategory.Profile
            ? new Dictionary<string, object> { [StateSpanMetadataExtensions.ProfileNameKey] = name }
            : new Dictionary<string, object>
            {
                [StateSpanMetadataExtensions.CollectionKey] = StateSpanMetadataExtensions.TreatmentsCollection,
                ["reason"] = name,
            },
    };

    private async Task UpsertInOrderAsync(IEnumerable<StateSpan> spans)
    {
        foreach (var span in spans)
            await _repository.UpsertStateSpanAsync(span);
    }

    /// <summary>
    /// What every read shows of <paramref name="category"/>: the paged list, the chart's window read
    /// and the point-in-time read must agree, and each shows exactly one open span.
    /// </summary>
    private async Task<StateSpan> TheOneOpenSpanAsync(StateSpanCategory category, string? state)
    {
        var listed = (await _repository.GetStateSpansAsync(category: category)).ToList();
        var open = listed.Should().ContainSingle(s => s.IsActive).Subject;

        var charted = (await _repository.GetByCategories([category], T.AddHours(-1), T.AddHours(1)))[category];
        charted.Select(s => s.Id).Should().BeEquivalentTo(listed.Select(s => s.Id));

        var activeLater = await _repository.GetActiveAtAsync(category, state, T.AddMinutes(5));
        activeLater.Should().NotBeNull();
        activeLater!.Id.Should().Be(open.Id);
        return open;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASameStateSpanReReportedWithinTheWindow_LeavesOneOpenPrimary(bool laterFirst)
    {
        var first = PumpMode("pm-first", 0);
        var repeat = PumpMode("pm-repeat", 10);

        await UpsertInOrderAsync(laterFirst ? [repeat, first] : [first, repeat]);

        (await _repository.GetStateSpansAsync(category: StateSpanCategory.PumpMode))
            .Should().ContainSingle();
        await TheOneOpenSpanAsync(StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString());
        (await _repository.GetCurrentPumpModeAsync()).Should().Be(PumpModeState.Automatic);
    }

    [Theory]
    [InlineData(0, 25, 50)]
    [InlineData(50, 25, 0)]
    [InlineData(25, 0, 50)]
    public async Task AThreeCopyChain_IsOneOpenPrimary(int a, int b, int c)
    {
        await UpsertInOrderAsync([PumpMode($"pm-{a}", a), PumpMode($"pm-{b}", b), PumpMode($"pm-{c}", c)]);

        (await _repository.GetStateSpansAsync(category: StateSpanCategory.PumpMode)).Should().ContainSingle();
        await TheOneOpenSpanAsync(StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString());
        (await _repository.GetCurrentPumpModeAsync()).Should().Be(PumpModeState.Automatic);
    }

    /// <summary>
    /// The 50 s copy arrives before the 25 s one could join it to the 0 s copy, so it forms a group of
    /// its own and the chain shows as two spans until a reconcile merges them.
    /// </summary>
    [Fact]
    public async Task AThreeCopyChainArrivingEndsFirst_LeavesOneOpenPrimary()
    {
        await UpsertInOrderAsync([PumpMode("pm-0", 0), PumpMode("pm-50", 50), PumpMode("pm-25", 25)]);

        await TheOneOpenSpanAsync(StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString());
        (await _repository.GetCurrentPumpModeAsync()).Should().Be(PumpModeState.Automatic);
    }

    [Fact]
    public async Task AThreeCopyChainInOneBatch_LeavesOneOpenPrimary()
    {
        await _repository.BulkUpsertAsync([PumpMode("pm-0", 0), PumpMode("pm-25", 25), PumpMode("pm-50", 50)]);

        await TheOneOpenSpanAsync(StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString());
    }

    [Theory]
    [InlineData(StateSpanCategory.Override, false)]
    [InlineData(StateSpanCategory.Override, true)]
    [InlineData(StateSpanCategory.TemporaryTarget, false)]
    [InlineData(StateSpanCategory.TemporaryTarget, true)]
    public async Task TwoPresetsWithinTheWindow_AreNotLinked_AndTheLaterSupersedesTheEarlier(
        StateSpanCategory category, bool laterFirst)
    {
        var earlier = Preset(category, "earlier", 0, "N Night");
        var later = Preset(category, "later", 10, "X Exercise");

        await UpsertInOrderAsync(laterFirst ? [later, earlier] : [earlier, later]);

        var spans = (await _repository.GetStateSpansAsync(category: category)).ToList();
        spans.Should().HaveCount(2);
        var rows = await _context.StateSpans.AsNoTracking().ToDictionaryAsync(s => s.OriginalId!);
        rows["earlier"].EndTimestamp.Should().Be(later.StartTimestamp);
        rows["earlier"].SupersededById.Should().Be(rows["later"].Id);
        (await TheOneOpenSpanAsync(category, null)).OriginalId.Should().Be("later");

        var ids = rows.Values.Select(s => s.Id).ToList();
        var canonicals = await _context.LinkedRecords.AsNoTracking()
            .Where(lr => ids.Contains(lr.RecordId))
            .Select(lr => lr.CanonicalId)
            .ToListAsync();
        canonicals.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(StateSpanCategory.Override)]
    [InlineData(StateSpanCategory.TemporaryTarget)]
    public async Task TheSamePresetReReportedWithinTheWindow_LeavesOneOpenPrimary(StateSpanCategory category)
    {
        await UpsertInOrderAsync([Preset(category, "first", 0, "N Night"), Preset(category, "repeat", 10, "N Night")]);

        (await _repository.GetStateSpansAsync(category: category)).Should().ContainSingle();
        await TheOneOpenSpanAsync(category, null);
    }

    [Fact]
    public async Task AGroupStoredWithAClosedPrimaryAndAnOpenCopy_IsRepairedOnce()
    {
        await StoreSupersededPrimaryAsync(copyEnd: null);

        (await _dedup.RepairStateSpanPrimariesAsync()).Should().Be(1);

        var active = await _repository.GetActiveAtAsync(
            StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString(), T.AddMinutes(5));
        active.Should().NotBeNull();
        active!.OriginalId.Should().Be("pm-open");

        var linksBefore = await LinksAsync();
        (await _dedup.RepairStateSpanPrimariesAsync()).Should().Be(0);
        (await LinksAsync()).Should().BeEquivalentTo(linksBefore);
    }

    [Fact]
    public async Task RepairStateSpanPrimaries_LeavesAHealthyTenantAlone()
    {
        await UpsertInOrderAsync([PumpMode("pm-first", 0), PumpMode("pm-repeat", 10)]);
        var linksBefore = await LinksAsync();

        (await _dedup.RepairStateSpanPrimariesAsync()).Should().Be(0);
        (await LinksAsync()).Should().BeEquivalentTo(linksBefore);
    }

    [Fact]
    public async Task AClosedGroupStartedBeforeTheRepairHorizon_IsNotTouched()
    {
        await StoreSupersededPrimaryAsync(copyEnd: T.AddHours(1));
        var linksBefore = await LinksAsync();

        (await _dedup.RepairStateSpanPrimariesAsync()).Should().Be(0);
        (await LinksAsync()).Should().BeEquivalentTo(linksBefore);
    }

    [Fact]
    public async Task AClosedGroupStartedWithinTheRepairHorizon_IsRepaired()
    {
        var (_, copy) = await StoreSupersededPrimaryAsync(
            copyEnd: DateTime.UtcNow.AddMinutes(-5), start: DateTime.UtcNow.AddHours(-1));

        (await _dedup.RepairStateSpanPrimariesAsync()).Should().Be(1);
        (await LinksAsync()).Should().ContainSingle(l => l.IsPrimary).Which.RecordId.Should().Be(copy.Id);
    }

    [Fact]
    public async Task APrimaryWhoseSupersedingCopyWasDeleted_IsNotFlaggedForRepair()
    {
        var (closed, copy) = await StoreSupersededPrimaryAsync(copyEnd: null, start: DateTime.UtcNow.AddHours(-1));
        var row = await _context.StateSpans.SingleAsync(s => s.Id == copy.Id);
        row.DeletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await DeduplicationService.MisplacedStateSpanPrimaries(_context, DateTime.UtcNow - DeduplicationService.StateSpanRepairHorizon)
                .ToListAsync())
            .Should().BeEmpty("the group's only live member is {0}, its primary", closed.Id);
    }

    /// <summary>
    /// A group stored the way linking left it before the survivor rule: the earlier copy primary and
    /// closed by the later copy, which ends at <paramref name="copyEnd"/>.
    /// </summary>
    private async Task<(StateSpanEntity Closed, StateSpanEntity Copy)> StoreSupersededPrimaryAsync(
        DateTime? copyEnd, DateTime? start = null)
    {
        var closed = StateSpanMapper.ToEntity(PumpMode("pm-closed", 0));
        var copy = StateSpanMapper.ToEntity(PumpMode("pm-open", 10));
        if (start is { } at)
        {
            closed.StartTimestamp = at;
            copy.StartTimestamp = at.AddSeconds(10);
        }
        closed.EndTimestamp = copy.StartTimestamp;
        closed.SupersededById = copy.Id;
        copy.EndTimestamp = copyEnd;
        _context.StateSpans.AddRange(closed, copy);
        var canonicalId = Guid.CreateVersion7();
        _context.LinkedRecords.AddRange(
            Link(canonicalId, closed, isPrimary: true),
            Link(canonicalId, copy, isPrimary: false));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (closed, copy);
    }

    private static LinkedRecordEntity Link(Guid canonicalId, StateSpanEntity span, bool isPrimary) => new()
    {
        Id = Guid.CreateVersion7(),
        CanonicalId = canonicalId,
        RecordType = RecordTypeKeys.Key(RecordType.StateSpan),
        RecordId = span.Id,
        SourceTimestamp = new DateTimeOffset(span.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        DataSource = span.Source!,
        IsPrimary = isPrimary,
    };

    private async Task<List<(Guid RecordId, bool IsPrimary)>> LinksAsync() =>
        (await _context.LinkedRecords.AsNoTracking()
            .Select(lr => new { lr.RecordId, lr.IsPrimary })
            .ToListAsync())
        .Select(l => (l.RecordId, l.IsPrimary))
        .ToList();
}
