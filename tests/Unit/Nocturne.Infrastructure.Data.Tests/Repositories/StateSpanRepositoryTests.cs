using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly Mock<IDeduplicationService> _mockDedup;
    private readonly StateSpanRepository _repository;
    private readonly SaveCounter _saves = new();

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

        _db = TestDbContextFactory.CreateSqliteWithTenant(
                TestTenantId, "test", new MutationAuditInterceptor(httpContextAccessor.Object), _saves)
            .SeedTenant(OtherTenantId, "other");

        _context = _db.CreateContext();
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

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
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
    public async Task UpsertStateSpanAsync_OlderStartingSpan_DoesNotSupersedeALaterOpenSpan()
    {
        // Historical backfill can insert a span that starts BEFORE an already-open one (a pump that
        // reports newest-first, ingested out of order). Superseding the later span would close it at
        // the earlier span's start — inverting it (end < start) and clearing a live suspension.
        var laterStart = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        var laterOpen = new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Suspended.ToString(),
            StartTimestamp = laterStart,
            EndTimestamp = null,
            Source = "Trio",
            OriginalId = "pump-suspended:later",
        };
        await _repository.UpsertStateSpanAsync(laterOpen);

        // A backfilled suspension that STARTS EARLIER arrives afterwards.
        var earlier = new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = PumpModeState.Suspended.ToString(),
            StartTimestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            EndTimestamp = new DateTime(2026, 1, 1, 10, 30, 0, DateTimeKind.Utc),
            Source = "Trio",
            OriginalId = "pump-suspended:earlier",
        };
        await _repository.UpsertStateSpanAsync(earlier);

        var spans = (await _repository.GetStateSpansAsync(category: StateSpanCategory.PumpMode)).ToList();

        var later = spans.First(s => s.OriginalId == "pump-suspended:later");
        later.EndTimestamp.Should().BeNull("a span that starts after the inserted one must not be superseded");
        later.SupersededById.Should().BeNullOrEmpty();
        later.IsActive.Should().BeTrue();
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
        _context.LinkedRecords.Add(Link(Guid.NewGuid(), duplicateEntity.Id, isPrimary: false));
        await _context.SaveChangesAsync();

        var current = await _repository.GetCurrentPumpModeAsync();

        current.Should().Be(PumpModeState.Automatic);
    }

    [Fact]
    public async Task GetStateSpansAsync_ExcludesNonPrimaryDeduplicatedSpans()
    {
        var start = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var state = PumpModeState.Automatic.ToString();

        var primaryEntity = SpanEntity(_context.TenantId, StateSpanCategory.PumpMode, state, start, null);
        primaryEntity.Source = "primary";
        var duplicateEntity = SpanEntity(_context.TenantId, StateSpanCategory.PumpMode, state, start, null);
        duplicateEntity.Source = "duplicate";

        _context.StateSpans.AddRange(primaryEntity, duplicateEntity);
        var canonicalId = Guid.NewGuid();
        _context.LinkedRecords.AddRange(
            Link(canonicalId, primaryEntity.Id, isPrimary: true),
            Link(canonicalId, duplicateEntity.Id, isPrimary: false));
        await _context.SaveChangesAsync();

        var spans = (await _repository.GetStateSpansAsync()).ToList();

        spans.Should().ContainSingle().Which.Source.Should().Be("primary");
    }

    private static LinkedRecordEntity Link(Guid canonicalId, Guid recordId, bool isPrimary) => new()
    {
        Id = Guid.NewGuid(),
        CanonicalId = canonicalId,
        RecordType = "statespan",
        RecordId = recordId,
        SourceTimestamp = 0,
        DataSource = isPrimary ? "primary" : "duplicate",
        IsPrimary = isPrimary,
        SysCreatedAt = DateTime.UtcNow,
    };

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
        await _repository.UpsertStateSpanAsync(activity);
        var originalRowId = (await RowsForAsync("glooko-illness-1")).Single().Id;

        (await _repository.DeleteActivityStateSpanAsync("glooko-illness-1")).Should().BeTrue();

        (await _repository.GetActivityStateSpansAsync()).Should().BeEmpty();

        await _repository.UpsertStateSpanAsync(activity);

        (await _repository.GetActivityStateSpansAsync()).Should().BeEmpty(
            "a user-deleted activity must not come back on the next sync");
        (await RowsForAsync("glooko-illness-1"))
            .Should().ContainSingle().Which.Id.Should().Be(originalRowId);
    }

    [Fact]
    public async Task UpsertStateSpanAsync_OpenSpanInsertedBehindALaterSpan_EndsAtTheLaterStart()
    {
        var laterStart = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        await _repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = laterStart,
            Source = "loop://iPhone",
            OriginalId = "later",
        });

        await _repository.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = new DateTime(2025, 12, 1, 10, 0, 0, DateTimeKind.Utc),
            Source = "nightscout",
            OriginalId = "backfilled",
        });

        var spans = (await _repository.GetStateSpansAsync(category: StateSpanCategory.Override)).ToList();
        spans.Single(s => s.OriginalId == "backfilled").EndTimestamp.Should().Be(laterStart);
        spans.Single(s => s.OriginalId == "later").IsActive.Should().BeTrue();
    }

    private static StateSpan LoopOverride(string originalId, DateTime start, string source, Dictionary<string, object> metadata) => new()
    {
        Category = StateSpanCategory.Override,
        State = OverrideState.Custom.ToString(),
        StartTimestamp = start,
        Source = source,
        OriginalId = originalId,
        Metadata = metadata,
    };

    private static Dictionary<string, object> TreatmentMetadata(string reason, double factor, string enteredBy = "Loop") =>
        new()
        {
            [StateSpanMetadataExtensions.CollectionKey] = StateSpanMetadataExtensions.TreatmentsCollection,
            ["reason"] = reason, ["insulinNeedsScaleFactor"] = factor, ["enteredBy"] = enteredBy,
        };

    private static Dictionary<string, object> DeviceStatusMetadata(string name, double multiplier) =>
        new()
        {
            [StateSpanMetadataExtensions.CollectionKey] = StateSpanMetadataExtensions.DeviceStatusCollection,
            ["name"] = name, ["multiplier"] = multiplier, ["currentCorrectionRange.minValue"] = 100,
        };

    [Fact]
    public async Task UpsertStateSpanAsync_AStoredTreatmentWithoutACollection_IsSupersededByItsSnapshot()
    {
        var snapshotStart = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc);
        var unmarked = TreatmentMetadata("N Night", 0.9);
        unmarked.Remove(StateSpanMetadataExtensions.CollectionKey);
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "treatment", new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), "Loop", unmarked));
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "snapshot", snapshotStart, "loop://iPhone", DeviceStatusMetadata("Night", 0.9)));

        (await _repository.GetStateSpansAsync(category: StateSpanCategory.Override))
            .Single(s => s.OriginalId == "treatment").EndTimestamp.Should().Be(snapshotStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpsertStateSpanAsync_TheSameOverrideAsTreatmentAndSnapshot_SupersedesNeither(bool treatmentFirst)
    {
        var treatment = LoopOverride(
            "treatment", new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), "Loop", TreatmentMetadata("N Night", 0.9));
        var snapshot = LoopOverride(
            "snapshot", new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc), "loop://iPhone", DeviceStatusMetadata("Night", 0.9));

        foreach (var span in treatmentFirst ? new[] { treatment, snapshot } : [snapshot, treatment])
            await _repository.UpsertStateSpanAsync(span);

        (await _repository.GetStateSpansAsync(category: StateSpanCategory.Override))
            .Should().HaveCount(2).And.OnlyContain(s => s.IsActive);
    }

    [Fact]
    public async Task UpsertStateSpanAsync_TheSamePresetFromTheSameSource_SupersedesTheOpenOne()
    {
        var laterStart = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc);
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "first", new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), "Loop", TreatmentMetadata("N Night", 0.9)));
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "second", laterStart, "Loop", TreatmentMetadata("N Night", 0.9)));

        var spans = (await _repository.GetStateSpansAsync(category: StateSpanCategory.Override)).ToList();
        spans.Single(s => s.OriginalId == "first").EndTimestamp.Should().Be(laterStart);
        spans.Single(s => s.OriginalId == "second").IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_TheSamePresetFromARemoteThenALocalTreatment_SupersedesTheOpenOne()
    {
        var localStart = new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc);
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "remote", new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), "Loop (via remote command)",
            TreatmentMetadata("R Running", 0.8, enteredBy: "Loop (via remote command)")));
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "local", localStart, "Loop", TreatmentMetadata("R Running", 0.8)));

        var spans = (await _repository.GetStateSpansAsync(category: StateSpanCategory.Override)).ToList();
        spans.Single(s => s.OriginalId == "remote").EndTimestamp.Should().Be(localStart);
        spans.Single(s => s.OriginalId == "local").IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_ADifferentOverrideFromAnotherSource_SupersedesTheOpenOne()
    {
        var laterStart = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc);
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "treatment", new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), "Loop", TreatmentMetadata("P Party", 0.9)));
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "snapshot", laterStart, "loop://iPhone", DeviceStatusMetadata("Night", 0.9)));

        var spans = (await _repository.GetStateSpansAsync(category: StateSpanCategory.Override)).ToList();
        spans.Single(s => s.OriginalId == "treatment").EndTimestamp.Should().Be(laterStart);
        spans.Single(s => s.OriginalId == "snapshot").IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_OpenSpanBehindTheSameOverride_EndsAtTheFirstDifferentOne()
    {
        var sameStart = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);
        var differentStart = new DateTime(2026, 1, 3, 8, 0, 0, DateTimeKind.Utc);
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "different", differentStart, "Loop", TreatmentMetadata("X Exercise", 0.5)));
        var same = LoopOverride("same", sameStart, "loop://iPhone", DeviceStatusMetadata("Night", 0.9));
        same.EndTimestamp = differentStart;
        await _repository.UpsertStateSpanAsync(same);

        await _repository.UpsertStateSpanAsync(LoopOverride(
            "backfilled", new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), "Loop", TreatmentMetadata("N Night", 0.9)));

        (await _repository.GetStateSpansAsync(category: StateSpanCategory.Override))
            .Single(s => s.OriginalId == "backfilled").EndTimestamp.Should().Be(differentStart);
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
        _context.StateSpans.Add(SpanEntity(
            OtherTenantId, StateSpanCategory.Override, "Custom",
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

    // --- Bulk upsert ---

    private static readonly DateTime BatchDay = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private static StateSpan Span(
        StateSpanCategory category, string state, int startHour, string originalId, int? endHour = null) => new()
    {
        Category = category,
        State = state,
        StartTimestamp = BatchDay.AddHours(startHour),
        EndTimestamp = endHour is { } end ? BatchDay.AddHours(end) : null,
        Source = "connector",
        OriginalId = originalId,
    };

    private Task<Dictionary<string, StateSpanEntity>> LiveRowsAsync() =>
        _context.StateSpans.AsNoTracking().ToDictionaryAsync(s => s.OriginalId!);

    [Fact]
    public async Task BulkUpsertAsync_ExclusiveBatch_EachSpanSupersedesTheOneBeforeIt()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "ov-stored"));

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Override, "Custom", 10, "ov-a"),
            Span(StateSpanCategory.Override, "Eating", 11, "ov-b"),
            Span(StateSpanCategory.Override, "Custom", 12, "ov-c"),
        ]);

        var rows = await LiveRowsAsync();
        rows["ov-stored"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["ov-stored"].SupersededById.Should().Be(rows["ov-a"].Id);
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(11));
        rows["ov-a"].SupersededById.Should().Be(rows["ov-b"].Id);
        rows["ov-b"].EndTimestamp.Should().Be(BatchDay.AddHours(12));
        rows["ov-b"].SupersededById.Should().Be(rows["ov-c"].Id);
        rows["ov-c"].EndTimestamp.Should().BeNull();
        rows["ov-c"].SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task BulkUpsertAsync_OutOfOrderBatch_EndsEachSpanAtTheNextStart()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 3, "pr-stored"));

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Profile, "Active", 10, "pr-late"),
            Span(StateSpanCategory.Profile, "Active", 5, "pr-early"),
        ]);

        var rows = await LiveRowsAsync();
        rows["pr-stored"].EndTimestamp.Should().Be(BatchDay.AddHours(5));
        rows["pr-stored"].SupersededById.Should().Be(rows["pr-early"].Id);
        rows["pr-early"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["pr-early"].SupersededById.Should().Be(rows["pr-late"].Id);
        rows["pr-late"].EndTimestamp.Should().BeNull("a span starting before it cannot supersede it");
    }

    [Fact]
    public async Task UpsertStateSpanAsync_OutOfOrderAcrossCalls_EndsEachSpanAtTheNextStart()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 1, "pr-x"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 5, "pr-b"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 3, "pr-c"));

        var rows = await LiveRowsAsync();
        rows["pr-x"].EndTimestamp.Should().Be(BatchDay.AddHours(3));
        rows["pr-x"].SupersededById.Should().Be(rows["pr-c"].Id);
        rows["pr-c"].EndTimestamp.Should().Be(BatchDay.AddHours(5));
        rows["pr-c"].SupersededById.Should().Be(rows["pr-b"].Id);
        rows["pr-b"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_BackfillBehindASuccessor_LeavesAnUploadedEndInPlace()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 1, "pr-x"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 5, "pr-b"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 1, "pr-x", endHour: 2));
        (await LiveRowsAsync())["pr-x"].SupersededById.Should().BeNull();

        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 3, "pr-c"));

        var rows = await LiveRowsAsync();
        rows["pr-x"].EndTimestamp.Should().Be(BatchDay.AddHours(2));
        rows["pr-x"].SupersededById.Should().BeNull();
        rows["pr-c"].EndTimestamp.Should().Be(BatchDay.AddHours(5));
    }

    [Fact]
    public async Task UpsertStateSpanAsync_BackfillBehindASuccessor_LeavesAnEndItDidNotSet()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 5, "pr-b"));
        var successorId = (await LiveRowsAsync())["pr-b"].Id;
        var stalePointer = SpanEntity(
            _context.TenantId, StateSpanCategory.Profile, "Active", BatchDay.AddHours(1), BatchDay.AddHours(2));
        stalePointer.OriginalId = "pr-x";
        stalePointer.SupersededById = successorId;
        _context.StateSpans.Add(stalePointer);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 3, "pr-c"));

        var rows = await LiveRowsAsync();
        rows["pr-x"].EndTimestamp.Should().Be(BatchDay.AddHours(2));
        rows["pr-x"].SupersededById.Should().Be(successorId);
    }

    [Fact]
    public async Task BulkUpsertAsync_BackfillBehindASuccessor_LeavesAnEndItDidNotSetOnARowInTheBatch()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 5, "pr-b"));
        var successorId = (await LiveRowsAsync())["pr-b"].Id;
        var stalePointer = SpanEntity(
            _context.TenantId, StateSpanCategory.Profile, "Active", BatchDay.AddHours(1), BatchDay.AddHours(2));
        stalePointer.OriginalId = "pr-x";
        stalePointer.SupersededById = successorId;
        _context.StateSpans.Add(stalePointer);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Profile, "Active", 1, "pr-x", endHour: 2),
            Span(StateSpanCategory.Profile, "Active", 3, "pr-c"),
        ]);

        (await LiveRowsAsync())["pr-x"].EndTimestamp.Should().Be(BatchDay.AddHours(2));
    }

    [Fact]
    public async Task BulkUpsertAsync_StoredSuccessorMovedByTheBatch_IsReadAtItsNewStart()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 5, "pr-moved"));

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Profile, "Active", 8, "pr-moved"),
            Span(StateSpanCategory.Profile, "Active", 3, "pr-backfilled"),
        ]);

        var rows = await LiveRowsAsync();
        rows["pr-backfilled"].EndTimestamp.Should().Be(BatchDay.AddHours(8));
        rows["pr-backfilled"].SupersededById.Should().Be(rows["pr-moved"].Id);
    }

    [Fact]
    public async Task BulkUpsertAsync_BackfillBehindASuccessor_LeavesATombstoneItClosed()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 1, "pr-x"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Profile, "Active", 5, "pr-b"));
        (await _repository.DeleteStateSpanAsync("pr-x")).Should().BeTrue();

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Profile, "Active", 1, "pr-x"),
            Span(StateSpanCategory.Profile, "Active", 3, "pr-c"),
        ]);

        var tombstone = (await RowsForAsync("pr-x")).Should().ContainSingle().Subject;
        tombstone.DeletedAt.Should().NotBeNull();
        tombstone.EndTimestamp.Should().Be(BatchDay.AddHours(5));
        (await LiveRowsAsync())["pr-c"].EndTimestamp.Should().Be(BatchDay.AddHours(5));
    }

    [Fact]
    public async Task BulkUpsertAsync_BackfillAtTheStartOfAClosedSpan_LeavesThatSpanItsLength()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Profile, "Active", 1, "pr-x"),
            Span(StateSpanCategory.Profile, "Active", 5, "pr-b"),
            Span(StateSpanCategory.Profile, "Active", 1, "pr-c"),
        ]);

        var rows = await LiveRowsAsync();
        rows["pr-x"].EndTimestamp.Should().Be(BatchDay.AddHours(5));
        rows["pr-x"].SupersededById.Should().Be(rows["pr-b"].Id);
        rows["pr-c"].EndTimestamp.Should().Be(BatchDay.AddHours(5));
    }

    [Fact]
    public async Task BulkUpsertAsync_TwoOpenSpansAtOneInstant_LeavesBothOpen()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Override, "Custom", 9, "ov-a"),
            Span(StateSpanCategory.Override, "Custom", 9, "ov-b"),
        ]);

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().BeNull();
        rows["ov-a"].SupersededById.Should().BeNull();
        rows["ov-b"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task BulkUpsertAsync_PumpModeBatch_SupersedesOnlyTheSameState()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString(), 10, "pm-auto-1"),
            Span(StateSpanCategory.PumpMode, PumpModeState.Suspended.ToString(), 11, "pm-suspend"),
            Span(StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString(), 12, "pm-auto-2"),
        ]);

        var rows = await LiveRowsAsync();
        rows["pm-auto-1"].SupersededById.Should().Be(rows["pm-auto-2"].Id);
        rows["pm-auto-1"].EndTimestamp.Should().Be(BatchDay.AddHours(12));
        rows["pm-suspend"].EndTimestamp.Should().BeNull();
        rows["pm-auto-2"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task BulkUpsertAsync_OutOfOrderPumpMode_EndsAtTheNextSameStateStart()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.PumpMode, PumpModeState.Suspended.ToString(), 11, "pm-suspend"),
            Span(StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString(), 12, "pm-auto-late"),
            Span(StateSpanCategory.PumpMode, PumpModeState.Automatic.ToString(), 10, "pm-auto-early"),
        ]);

        var rows = await LiveRowsAsync();
        rows["pm-auto-early"].EndTimestamp.Should().Be(BatchDay.AddHours(12));
        rows["pm-auto-early"].SupersededById.Should().Be(rows["pm-auto-late"].Id);
        rows["pm-suspend"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task BulkUpsertAsync_MixedBatch_UpdatesStoredRowsWithoutDuplicating()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Exercise, "Running", 8, "ex-stored"));
        var storedId = (await RowsForAsync("ex-stored")).Single().Id;

        var count = await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Exercise, "Running", 8, "ex-stored", endHour: 9),
            Span(StateSpanCategory.Exercise, "Cycling", 10, "ex-new"),
            Span(StateSpanCategory.Exercise, "Cycling", 10, "ex-new", endHour: 11),
        ]);

        count.Should().Be(3);
        var stored = (await RowsForAsync("ex-stored")).Should().ContainSingle().Subject;
        stored.Id.Should().Be(storedId);
        stored.EndTimestamp.Should().Be(BatchDay.AddHours(9));
        (await RowsForAsync("ex-new")).Should().ContainSingle()
            .Which.EndTimestamp.Should().Be(
                BatchDay.AddHours(11), "a repeated OriginalId updates the row its first occurrence inserted");
    }

    [Fact]
    public async Task BulkUpsertAsync_UserDeletedSpan_IsNotRecreated_WhileTheRestOfTheBatchLands()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var originalRowId = (await RowsForAsync("glooko-exercise-1")).Single().Id;
        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().BeTrue();

        await _repository.BulkUpsertAsync(
            [ConnectorSpan(), Span(StateSpanCategory.Exercise, "Cycling", 10, "ex-new")]);

        (await RowsForAsync("glooko-exercise-1")).Should().ContainSingle()
            .Which.Id.Should().Be(originalRowId);
        (await RowsForAsync("ex-new")).Should().ContainSingle();
    }

    [Fact]
    public async Task BulkUpsertAsync_UserDeletedOpenSpan_IsNotSupersededByALaterSpanInTheBatch()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "ov-deleted"));
        (await _repository.DeleteStateSpanAsync("ov-deleted")).Should().BeTrue();

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Override, "Custom", 9, "ov-deleted"),
            Span(StateSpanCategory.Override, "Custom", 10, "ov-new"),
        ]);

        var tombstone = (await RowsForAsync("ov-deleted")).Should().ContainSingle().Subject;
        tombstone.DeletedAt.Should().NotBeNull();
        tombstone.EndTimestamp.Should().BeNull();
        tombstone.SupersededById.Should().BeNull();
        (await RowsForAsync("ov-new")).Should().ContainSingle();
    }

    [Fact]
    public async Task BulkUpsertAsync_LeavesNoStateSpanTracked_EvenForEveryTombstoneItLoaded()
    {
        foreach (var hour in new[] { 8, 9 })
        {
            var tombstone = SpanEntity(
                _context.TenantId, StateSpanCategory.Exercise, "Running", BatchDay.AddHours(hour), null);
            tombstone.OriginalId = "ex-twice-deleted";
            tombstone.DeletedAt = BatchDay.AddHours(hour + 1);
            _context.StateSpans.Add(tombstone);
            _context.Entry(tombstone).Property("DeletedByUser").CurrentValue = true;
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Exercise, "Running", 8, "ex-twice-deleted"),
            Span(StateSpanCategory.Override, "Custom", 10, "ov-new"),
        ]);

        _context.ChangeTracker.Entries<StateSpanEntity>().Should().BeEmpty();
        (await RowsForAsync("ex-twice-deleted")).Should().HaveCount(2).And.OnlyContain(r => r.DeletedAt != null);
    }

    [Fact]
    public async Task BulkUpsertAsync_SystemSweptSpan_IsRecreated()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        _auditContext.IsSystem = true;
        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().BeTrue();
        _auditContext.IsSystem = false;

        await _repository.BulkUpsertAsync([ConnectorSpan()]);

        var rows = await RowsForAsync("glooko-exercise-1");
        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(r => r.DeletedAt == null);
    }

    [Fact]
    public async Task BulkUpsertAsync_SavesOnceAndDeduplicatesEveryNewSpanTogether()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "ov-stored"));
        _mockDedup.Invocations.Clear();
        var savesBefore = _saves.Count;

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Override, "Custom", 9, "ov-stored", endHour: 10),
            Span(StateSpanCategory.Override, "Custom", 10, "ov-a"),
            Span(StateSpanCategory.Override, "Custom", 11, "ov-b"),
            Span(StateSpanCategory.Exercise, "Running", 12, "ex-a"),
        ]);

        _saves.Count.Should().Be(savesBefore + 1);
        var rows = await LiveRowsAsync();
        var expectedIds = new[] { rows["ov-a"].Id, rows["ov-b"].Id, rows["ex-a"].Id };
        _mockDedup.Verify(d => d.DeduplicateBatchAsync(
            RecordType.StateSpan,
            It.Is<IReadOnlyList<DeduplicationInput>>(inputs =>
                inputs.Select(i => i.RecordId).SequenceEqual(expectedIds)),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockDedup.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateActivitiesAsStateSpansAsync_SavesOnceAndReturnsEverySpanInInputOrder()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Exercise, "Running", 8, "act-stored"));
        var savesBefore = _saves.Count;

        var created = (await _repository.CreateActivitiesAsStateSpansAsync(
        [
            Span(StateSpanCategory.Exercise, "Running", 8, "act-stored", endHour: 9),
            Span(StateSpanCategory.Illness, "Flu", 10, "act-a"),
            Span(StateSpanCategory.Travel, "Flight", 12, "act-b"),
        ])).ToList();

        _saves.Count.Should().Be(savesBefore + 1);
        created.Select(s => s.OriginalId).Should().Equal("act-stored", "act-a", "act-b");
        created[0].EndTimestamp.Should().Be(BatchDay.AddHours(9));
        (await RowsForAsync("act-stored")).Should().ContainSingle();
    }
}
