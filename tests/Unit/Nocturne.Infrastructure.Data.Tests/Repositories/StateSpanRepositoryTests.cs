using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class StateSpanRepositoryTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<IDeduplicationService> _mockDedup;
    private readonly StateSpanRepository _repository;

    /// <summary>
    /// The one audit context both the repository and the interceptor read, so flipping
    /// <see cref="StubAuditContext.IsSystem"/> switches a delete between user-initiated and
    /// system sweep the way <c>SystemAuditScope</c> does in the connector pipeline.
    /// </summary>
    private readonly StubAuditContext _auditContext = new()
    {
        SubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        SubjectName = "tester",
        AuthType = "SessionCookie",
    };

    public StateSpanRepositoryTests()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext)null!);

        _context = TestDbContextFactory.CreateInMemoryContext(
            interceptors: new MutationAuditInterceptor(httpContextAccessor.Object));
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _context.AuditContext = _auditContext;
        _mockDedup = new Mock<IDeduplicationService>();
        _repository = new StateSpanRepository(
            _context, _mockDedup.Object, _auditContext, NullLogger<StateSpanRepository>.Instance);
    }

    private sealed class StubAuditContext : IAuditContext
    {
        public Guid? SubjectId { get; init; }
        public string? SubjectName { get; init; }
        public string? AuthType { get; init; }
        public string? IpAddress { get; init; }
        public Guid? TokenId { get; init; }
        public string? TraceId { get; init; }
        public string? Endpoint { get; init; }
        public bool IsSystem { get; set; }
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UpsertStateSpanAsync_NewOverride_SupersedesExistingOpenOverride()
    {
        // Arrange - insert an open override span
        var existingSpan = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "nightscout",
            OriginalId = "old-override"
        };
        await _repository.UpsertStateSpanAsync(existingSpan);

        // Act - insert a new override span
        var newSpan = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "nightscout",
            OriginalId = "new-override"
        };
        var result = await _repository.UpsertStateSpanAsync(newSpan);

        // Assert - the old span should now be closed and superseded
        var allSpans = (await _repository.GetStateSpansAsync(
            category: StateSpanCategory.Override)).ToList();

        var oldSpan = allSpans.First(s => s.OriginalId == "old-override");
        oldSpan.EndTimestamp.Should().Be(new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc));
        oldSpan.SupersededById.Should().NotBeNullOrEmpty();
        oldSpan.IsActive.Should().BeFalse();

        var newSpanResult = allSpans.First(s => s.OriginalId == "new-override");
        newSpanResult.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_PumpModeAutomatic_DoesNotSupersedeOpenSuspended()
    {
        // Suspended (LGS) and Automatic/Manual loop mode are independent dimensions that can overlap,
        // so opening one must NOT close the other even though both share the PumpMode category.
        var suspended = new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Suspended.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "CareLink NGP",
            OriginalId = "suspend-1",
        };
        await _repository.UpsertStateSpanAsync(suspended);

        var automatic = new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Automatic.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 5, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "CareLink NGP",
            OriginalId = "auto-1",
        };
        await _repository.UpsertStateSpanAsync(automatic);

        var spans = (await _repository.GetStateSpansAsync(category: StateSpanCategory.PumpMode)).ToList();
        spans.First(s => s.OriginalId == "suspend-1").IsActive
            .Should().BeTrue("a different-state PumpMode span must not close the suspension span");
        spans.First(s => s.OriginalId == "auto-1").IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_PumpModeSameState_SupersedesOpenSpan()
    {
        // Per-state exclusivity is preserved: a newer open span of the SAME PumpMode state closes the prior.
        var first = new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Automatic.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "CareLink NGP",
            OriginalId = "auto-old",
        };
        await _repository.UpsertStateSpanAsync(first);

        var second = new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Automatic.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "CareLink NGP",
            OriginalId = "auto-new",
        };
        await _repository.UpsertStateSpanAsync(second);

        var spans = (await _repository.GetStateSpansAsync(category: StateSpanCategory.PumpMode)).ToList();
        spans.First(s => s.OriginalId == "auto-old").IsActive
            .Should().BeFalse("a newer same-state span supersedes the prior open one");
        spans.First(s => s.OriginalId == "auto-new").IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_NewTemporaryTarget_SupersedesExistingOpenTarget()
    {
        // Arrange
        var existingSpan = new StateSpan
        {
            Category = StateSpanCategory.TemporaryTarget,
            State = TemporaryTargetState.Active.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "AAPS",
            OriginalId = "old-target"
        };
        await _repository.UpsertStateSpanAsync(existingSpan);

        // Act
        var newSpan = new StateSpan
        {
            Category = StateSpanCategory.TemporaryTarget,
            State = TemporaryTargetState.Active.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "AAPS",
            OriginalId = "new-target"
        };
        await _repository.UpsertStateSpanAsync(newSpan);

        // Assert
        var allSpans = (await _repository.GetStateSpansAsync(
            category: StateSpanCategory.TemporaryTarget)).ToList();

        var oldSpan = allSpans.First(s => s.OriginalId == "old-target");
        oldSpan.EndTimestamp.Should().Be(new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc));
        oldSpan.SupersededById.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_NonExclusiveCategory_DoesNotSupersede()
    {
        // Arrange - Exercise is not an exclusive category
        var existingSpan = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "Running",
            StartTimestamp = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "manual",
            OriginalId = "sleep-1"
        };
        await _repository.UpsertStateSpanAsync(existingSpan);

        // Act - insert another sleep span
        var newSpan = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "Sleeping",
            StartTimestamp = new DateTime(2026, 1, 2, 22, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "manual",
            OriginalId = "sleep-2"
        };
        await _repository.UpsertStateSpanAsync(newSpan);

        // Assert - both should remain open
        var allSpans = (await _repository.GetStateSpansAsync(
            category: StateSpanCategory.Exercise)).ToList();

        allSpans.Should().HaveCount(2);
        allSpans.Should().AllSatisfy(s => s.EndTimestamp.Should().BeNull());
        allSpans.Should().AllSatisfy(s => s.SupersededById.Should().BeNull());
    }

    [Fact]
    public async Task UpsertStateSpanAsync_UpdateExisting_DoesNotTriggerSupersession()
    {
        // Arrange - insert a span
        var span = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "nightscout",
            OriginalId = "override-to-update"
        };
        await _repository.UpsertStateSpanAsync(span);

        // Act - upsert again with same OriginalId (update path)
        var updatedSpan = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            Source = "nightscout",
            OriginalId = "override-to-update"
        };
        await _repository.UpsertStateSpanAsync(updatedSpan);

        // Assert - should update in place, no supersession
        var allSpans = (await _repository.GetStateSpansAsync(
            category: StateSpanCategory.Override)).ToList();

        allSpans.Should().HaveCount(1);
        allSpans[0].SupersededById.Should().BeNull();
        allSpans[0].EndTimestamp.Should().Be(new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task GetCurrentPumpModeAsync_ReturnsLatestOpenPumpModeSpan()
    {
        await _repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Manual.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            EndTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            Source = "pump",
            OriginalId = "pm-old",
        });
        await _repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Automatic.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "pump",
            OriginalId = "pm-current",
        });

        var current = await _repository.GetCurrentPumpModeAsync();

        current.Should().Be(PumpModeState.Automatic);
    }

    [Fact]
    public async Task GetCurrentPumpModeAsync_NoOpenSpan_ReturnsNull()
    {
        await _repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Manual.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            EndTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            Source = "pump",
            OriginalId = "pm-closed",
        });

        var current = await _repository.GetCurrentPumpModeAsync();

        current.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentPumpModeAsync_UnrecognizedState_ReturnsNull()
    {
        await _repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = "NotAModeWeKnow",
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "pump",
            OriginalId = "pm-bogus",
        });

        var current = await _repository.GetCurrentPumpModeAsync();

        current.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentPumpModeAsync_IgnoresOpenSpansFromOtherCategories()
    {
        await _repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            EndTimestamp = null,
            Source = "nightscout",
            OriginalId = "ov-open",
        });

        var current = await _repository.GetCurrentPumpModeAsync();

        current.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentPumpModeAsync_ExcludesNonPrimaryDeduplicatedSpans()
    {
        // Insert entities directly to bypass exclusive-category auto-close logic;
        // this test verifies the deduplication query filter, not upsert behavior.
        var primaryEntity = SpanEntity(
            _context.TenantId, StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString(),
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), null);
        primaryEntity.OriginalId = "pm-primary";
        primaryEntity.Source = "primary";

        var duplicateEntity = SpanEntity(
            _context.TenantId, StateSpanCategory.PumpMode, PumpModeState.Manual.ToString(),
            new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc), null);
        duplicateEntity.OriginalId = "pm-duplicate";
        duplicateEntity.Source = "duplicate";

        _context.StateSpans.AddRange(primaryEntity, duplicateEntity);
        _context.LinkedRecords.Add(new LinkedRecordEntity
        {
            Id = Guid.NewGuid(),
            CanonicalId = Guid.NewGuid(),
            RecordType = "statespan",
            RecordId = duplicateEntity.Id,
            SourceTimestamp = 0,
            DataSource = "duplicate",
            IsPrimary = false,
            SysCreatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var current = await _repository.GetCurrentPumpModeAsync();

        current.Should().Be(PumpModeState.Automatic);
    }

    // --- GetActiveAtAsync ---

    private static StateSpanEntity SpanEntity(
        Guid tenantId,
        StateSpanCategory category,
        string state,
        DateTime start,
        DateTime? end)
        => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Category = category.ToString(),
            State = state,
            StartTimestamp = start,
            EndTimestamp = end,
            Source = "test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task GetActiveAtAsync_returns_null_when_no_rows()
    {
        var result = await _repository.GetActiveAtAsync(
            StateSpanCategory.Override,
            state: null,
            at: new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAtAsync_returns_active_span_with_null_end()
    {
        var start = new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc);
        _context.StateSpans.Add(SpanEntity(
            _context.TenantId, StateSpanCategory.Override, "Custom", start, end: null));
        await _context.SaveChangesAsync();

        var result = await _repository.GetActiveAtAsync(
            StateSpanCategory.Override,
            state: null,
            at: new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.StartTimestamp.Should().Be(start);
    }

    [Fact]
    public async Task GetActiveAtAsync_returns_active_span_with_future_end()
    {
        var start = new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 30, 14, 0, 0, DateTimeKind.Utc);
        _context.StateSpans.Add(SpanEntity(
            _context.TenantId, StateSpanCategory.Exercise, "Sleeping", start, end));
        await _context.SaveChangesAsync();

        var result = await _repository.GetActiveAtAsync(
            StateSpanCategory.Exercise,
            state: null,
            at: new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.EndTimestamp.Should().Be(end);
    }

    [Fact]
    public async Task GetActiveAtAsync_returns_null_when_none_active()
    {
        _context.StateSpans.Add(SpanEntity(
            _context.TenantId, StateSpanCategory.Exercise, "Sleeping",
            new DateTime(2026, 4, 30, 6, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 30, 7, 0, 0, DateTimeKind.Utc)));
        _context.StateSpans.Add(SpanEntity(
            _context.TenantId, StateSpanCategory.Exercise, "Sleeping",
            new DateTime(2026, 4, 30, 14, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 30, 15, 0, 0, DateTimeKind.Utc)));
        await _context.SaveChangesAsync();

        var result = await _repository.GetActiveAtAsync(
            StateSpanCategory.Exercise,
            state: null,
            at: new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAtAsync_picks_latest_start_when_overlapping()
    {
        _context.StateSpans.Add(SpanEntity(
            _context.TenantId, StateSpanCategory.Exercise, "A",
            new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 30, 14, 0, 0, DateTimeKind.Utc)));
        _context.StateSpans.Add(SpanEntity(
            _context.TenantId, StateSpanCategory.Exercise, "B",
            new DateTime(2026, 4, 30, 11, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 30, 13, 0, 0, DateTimeKind.Utc)));
        await _context.SaveChangesAsync();

        var result = await _repository.GetActiveAtAsync(
            StateSpanCategory.Exercise,
            state: null,
            at: new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result!.State.Should().Be("B");
    }

    [Fact]
    public async Task GetActiveAtAsync_filters_by_state_when_provided()
    {
        var at = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        _context.StateSpans.Add(SpanEntity(
            _context.TenantId, StateSpanCategory.Exercise, "A",
            new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 30, 14, 0, 0, DateTimeKind.Utc)));
        await _context.SaveChangesAsync();

        var matching = await _repository.GetActiveAtAsync(
            StateSpanCategory.Exercise, state: "A", at, CancellationToken.None);
        var nonMatching = await _repository.GetActiveAtAsync(
            StateSpanCategory.Exercise, state: "B", at, CancellationToken.None);

        matching.Should().NotBeNull();
        nonMatching.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveAtAsync_end_is_exclusive()
    {
        var end = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);
        _context.StateSpans.Add(SpanEntity(
            _context.TenantId, StateSpanCategory.Exercise, "A",
            new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc),
            end));
        await _context.SaveChangesAsync();

        var result = await _repository.GetActiveAtAsync(
            StateSpanCategory.Exercise, state: null, at: end, CancellationToken.None);

        result.Should().BeNull();
    }

    // --- Soft-delete re-creation guard ---

    private static StateSpan ConnectorSpan() => new()
    {
        Category = StateSpanCategory.Exercise,
        State = "Running",
        StartTimestamp = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc),
        EndTimestamp = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
        Source = "glooko-connector",
        OriginalId = "glooko-exercise-1"
    };

    /// <summary>Primary keys of every row carrying <paramref name="originalId"/>, deleted included.</summary>
    private Task<List<StateSpanEntity>> RowsForAsync(string originalId) =>
        _context.StateSpans.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.OriginalId == originalId)
            .ToListAsync();

    [Fact]
    public async Task UserDeletedSpan_IsNotRecreatedByTheNextConnectorSync()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var originalRowId = (await RowsForAsync("glooko-exercise-1")).Single().Id;

        var deleted = await _repository.DeleteStateSpanAsync("glooko-exercise-1");
        deleted.Should().BeTrue();

        await _repository.UpsertStateSpanAsync(ConnectorSpan());

        (await _repository.GetStateSpansAsync(category: StateSpanCategory.Exercise))
            .Should().BeEmpty("the delete must survive the next sync");

        var rows = await RowsForAsync("glooko-exercise-1");
        rows.Should().ContainSingle("the resync must not insert a second row")
            .Which.Id.Should().Be(originalRowId);
        rows[0].DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SystemSweptSpan_IsRecreatedByTheNextConnectorSync()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var originalRowId = (await RowsForAsync("glooko-exercise-1")).Single().Id;

        _auditContext.IsSystem = true;
        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().BeTrue();
        _auditContext.IsSystem = false;

        await _repository.UpsertStateSpanAsync(ConnectorSpan());

        (await _repository.GetStateSpansAsync(category: StateSpanCategory.Exercise))
            .Should().ContainSingle("a system sweep leaves the span re-importable");

        var rows = await RowsForAsync("glooko-exercise-1");
        rows.Should().HaveCount(2, "the swept row stays in place for audit continuity");
        rows.Should().ContainSingle(r => r.Id == originalRowId && r.DeletedAt != null);
        rows.Should().ContainSingle(r => r.Id != originalRowId && r.DeletedAt == null);
    }

    [Fact]
    public async Task DeleteActivityStateSpanAsync_SoftDeletes_AndBlocksRecreation()
    {
        var activity = new StateSpan
        {
            Category = StateSpanCategory.Illness,
            State = "Flu",
            StartTimestamp = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc),
            Source = "glooko-connector",
            OriginalId = "glooko-illness-1",
        };
        await _repository.UpsertActivityAsStateSpanAsync(activity);
        var originalRowId = (await RowsForAsync("glooko-illness-1")).Single().Id;

        (await _repository.DeleteActivityStateSpanAsync("glooko-illness-1")).Should().BeTrue();

        (await _repository.GetActivityStateSpansAsync()).Should().BeEmpty();

        await _repository.UpsertActivityAsStateSpanAsync(activity);

        (await _repository.GetActivityStateSpansAsync()).Should().BeEmpty(
            "a user-deleted activity must not come back on the next sync");
        (await RowsForAsync("glooko-illness-1"))
            .Should().ContainSingle().Which.Id.Should().Be(originalRowId);
    }

    [Fact]
    public async Task DeletedSpan_IsHiddenFromReadsAndCounts()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        await _repository.DeleteStateSpanAsync("glooko-exercise-1");

        (await _repository.GetStateSpanByIdAsync("glooko-exercise-1")).Should().BeNull();
        (await _repository.CountStateSpansAsync(category: StateSpanCategory.Exercise)).Should().Be(0);
        (await _repository.GetActiveAtAsync(
            StateSpanCategory.Exercise, state: null,
            at: new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc))).Should().BeNull();
        (await _repository.GetByCategories([StateSpanCategory.Exercise]))[StateSpanCategory.Exercise]
            .Should().BeEmpty();
    }

    [Fact]
    public async Task DeletedSpan_DoesNotBlockASecondDelete_ButReportsNotFound()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().BeTrue();

        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().BeFalse();
    }

    [Fact]
    public async Task GetActiveAtAsync_respects_tenant_isolation()
    {
        var otherTenant = Guid.Parse("00000000-0000-0000-0000-000000000099");
        _context.StateSpans.Add(SpanEntity(
            otherTenant, StateSpanCategory.Override, "Custom",
            new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc),
            end: null));
        await _context.SaveChangesAsync();

        var result = await _repository.GetActiveAtAsync(
            StateSpanCategory.Override,
            state: null,
            at: new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        result.Should().BeNull();
    }
}
