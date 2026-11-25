using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
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
    private readonly Mock<IDuplicateLinker> _mockDedup;
    private readonly StateSpanRepository _repository;
    private readonly Mock<ILogger<StateSpanRepository>> _logger = new();
    private readonly SaveCounter _saves = new();
    private readonly StatementCounter _statements = new();

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
                TestTenantId, "test", new MutationAuditInterceptor(), _saves, _statements)
            .SeedTenant(OtherTenantId, "other");

        _context = _db.CreateContext();
        _context.AttributedTo(_auditContext);
        _mockDedup = new Mock<IDuplicateLinker>();
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        _repository = new StateSpanRepository(
            _context.AsFactory(), _mockDedup.Object, _auditContext, _logger.Object);
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

    private sealed class StatementCounter : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public int Selects { get; private set; }

        public int Updates { get; private set; }

        public int Transactions { get; private set; }

        public void Reset() => (Selects, Updates, Transactions) = (0, 0, 0);

        public ValueTask<InterceptionResult<System.Data.Common.DbTransaction>> TransactionStartingAsync(
            System.Data.Common.DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<System.Data.Common.DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Transactions++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                Selects++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                Updates++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
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
        primaryEntity.LegacyId = "pm-primary";
        primaryEntity.Source = "primary";

        var duplicateEntity = SpanEntity(
            _context.TenantId, StateSpanCategory.PumpMode, PumpModeState.Manual.ToString(),
            new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc), null);
        duplicateEntity.LegacyId = "pm-duplicate";
        duplicateEntity.Source = "duplicate";

        _context.StateSpans.AddRange(primaryEntity, duplicateEntity);
        await DuplicateGroups.LinkAsync(_context, RecordType.StateSpan, primaryEntity.Id, duplicateEntity.Id);

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
        await DuplicateGroups.LinkAsync(_context, RecordType.StateSpan, primaryEntity.Id, duplicateEntity.Id);

        var spans = (await _repository.GetStateSpansAsync()).ToList();

        spans.Should().ContainSingle().Which.Source.Should().Be("primary");
    }

    [Fact]
    public async Task GetActivityStateSpansAsync_ExcludesNonPrimaryDeduplicatedSpans()
    {
        var start = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var primaryEntity = SpanEntity(_context.TenantId, StateSpanCategory.Exercise, "Running", start, null);
        primaryEntity.Source = "primary";
        var duplicateEntity = SpanEntity(_context.TenantId, StateSpanCategory.Exercise, "Running", start, null);
        duplicateEntity.Source = "duplicate";

        _context.StateSpans.AddRange(primaryEntity, duplicateEntity);
        await DuplicateGroups.LinkAsync(_context, RecordType.StateSpan, primaryEntity.Id, duplicateEntity.Id);

        var spans = await _repository.GetActivityStateSpansAsync();

        spans.Should().ContainSingle().Which.Source.Should().Be("primary");
    }

    [Fact]
    public async Task GetActiveAtAsync_ExcludesNonPrimaryDeduplicatedSpans()
    {
        var primaryEntity = SpanEntity(_context.TenantId, StateSpanCategory.Override, "Custom",
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), null);
        primaryEntity.Source = "primary";
        var duplicateEntity = SpanEntity(_context.TenantId, StateSpanCategory.Override, "Custom",
            new DateTime(2026, 1, 1, 9, 0, 20, DateTimeKind.Utc), null);
        duplicateEntity.Source = "duplicate";

        _context.StateSpans.AddRange(primaryEntity, duplicateEntity);
        await DuplicateGroups.LinkAsync(_context, RecordType.StateSpan, primaryEntity.Id, duplicateEntity.Id);

        var active = await _repository.GetActiveAtAsync(
            StateSpanCategory.Override, null, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        active.Should().NotBeNull();
        active!.Source.Should().Be("primary");
    }

    [Fact]
    public async Task GetByCategories_ExcludesNonPrimaryDeduplicatedSpans()
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var carriedPrimary = SpanEntity(TestTenantId, StateSpanCategory.Override, "Custom", from.AddHours(-2), null);
        carriedPrimary.Source = "primary";
        var carriedDuplicate = SpanEntity(TestTenantId, StateSpanCategory.Override, "Custom", from.AddHours(-1), null);
        carriedDuplicate.Source = "duplicate";
        var windowPrimary = SpanEntity(
            TestTenantId, StateSpanCategory.PumpMode, "Automatic", from.AddHours(1), null);
        windowPrimary.Source = "primary";
        var windowDuplicate = SpanEntity(
            TestTenantId, StateSpanCategory.PumpMode, "Automatic", from.AddHours(1).AddSeconds(10), null);
        windowDuplicate.Source = "duplicate";

        _context.StateSpans.AddRange(carriedPrimary, carriedDuplicate, windowPrimary, windowDuplicate);
        await DuplicateGroups.LinkAsync(_context, RecordType.StateSpan, carriedPrimary.Id, carriedDuplicate.Id);
        await DuplicateGroups.LinkAsync(_context, RecordType.StateSpan, windowPrimary.Id, windowDuplicate.Id);

        var result = await _repository.GetByCategories(
            [StateSpanCategory.Override, StateSpanCategory.PumpMode], from, from.AddDays(1));

        result[StateSpanCategory.Override].Should().ContainSingle().Which.Source.Should().Be("primary");
        result[StateSpanCategory.PumpMode].Should().ContainSingle().Which.Source.Should().Be("primary");
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

    [Fact]
    public async Task GetByCategories_OpenSpansBeforeTheWindow_ExclusiveCategoriesCarryInOnlyTheOneInEffect()
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 50; i++)
            _context.StateSpans.Add(SpanEntity(
                TestTenantId, StateSpanCategory.Override, "Custom", from.AddHours(-i), end: null));
        _context.StateSpans.Add(SpanEntity(
            TestTenantId, StateSpanCategory.Override, "Custom", from.AddHours(1), end: null));
        foreach (var (state, days) in new[]
                 { ("Automatic", 30), ("Automatic", 20), ("Suspended", 40), ("Suspended", 2) })
            _context.StateSpans.Add(SpanEntity(
                TestTenantId, StateSpanCategory.PumpMode, state, from.AddDays(-days), end: null));
        foreach (var (state, days) in new[] { ("Default", 5), ("Weekend", 3) })
            _context.StateSpans.Add(SpanEntity(
                TestTenantId, StateSpanCategory.TemporaryTarget, state, from.AddDays(-days), end: null));
        await _context.SaveChangesAsync();

        var result = await _repository.GetByCategories(
            [StateSpanCategory.Override, StateSpanCategory.PumpMode, StateSpanCategory.TemporaryTarget],
            from, from.AddDays(1));

        result[StateSpanCategory.Override].Select(s => s.StartTimestamp)
            .Should().BeEquivalentTo([from.AddHours(1), from.AddHours(-1)]);
        result[StateSpanCategory.PumpMode].Select(s => (s.State, s.StartTimestamp))
            .Should().BeEquivalentTo([("Automatic", from.AddDays(-20)), ("Suspended", from.AddDays(-2))]);
        result[StateSpanCategory.TemporaryTarget].Select(s => (s.State, s.StartTimestamp))
            .Should().BeEquivalentTo([("Weekend", from.AddDays(-3))]);
    }

    [Fact]
    public async Task GetByCategories_OpenSpanBehindANewerClosedSpan_IsNotCarriedIn()
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _context.StateSpans.Add(SpanEntity(
            TestTenantId, StateSpanCategory.PumpMode, "Suspended", from.AddDays(-40), end: null));
        _context.StateSpans.Add(SpanEntity(
            TestTenantId, StateSpanCategory.PumpMode, "Suspended", from.AddDays(-2), from.AddHours(1)));
        _context.StateSpans.Add(SpanEntity(
            TestTenantId, StateSpanCategory.PumpMode, "Automatic", from.AddDays(-30), end: null));
        _context.StateSpans.Add(SpanEntity(
            TestTenantId, StateSpanCategory.TemporaryTarget, "Default", from.AddDays(-5), end: null));
        _context.StateSpans.Add(SpanEntity(
            TestTenantId, StateSpanCategory.TemporaryTarget, "Weekend", from.AddDays(-3), from.AddDays(-2)));
        await _context.SaveChangesAsync();

        var result = await _repository.GetByCategories(
            [StateSpanCategory.PumpMode, StateSpanCategory.TemporaryTarget], from, from.AddDays(1));

        result[StateSpanCategory.PumpMode].Select(s => (s.State, s.StartTimestamp))
            .Should().BeEquivalentTo([("Suspended", from.AddDays(-2)), ("Automatic", from.AddDays(-30))]);
        result[StateSpanCategory.TemporaryTarget].Should().BeEmpty();
    }

    [Fact]
    public async Task GetByCategories_OpenSpansBeforeTheWindowWithTheSameStart_CarryInTheHigherId()
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (state, id) in new[] { ("Higher", 2), ("Lower", 1) })
        {
            var span = SpanEntity(TestTenantId, StateSpanCategory.TemporaryTarget, state, from.AddDays(-1), end: null);
            span.Id = Guid.Parse($"00000000-0000-0000-0000-{id:D12}");
            _context.StateSpans.Add(span);
        }
        await _context.SaveChangesAsync();

        var targets = (await _repository.GetByCategories(
            [StateSpanCategory.TemporaryTarget], from, from.AddDays(1)))[StateSpanCategory.TemporaryTarget];

        targets.Select(s => s.State).Should().Equal("Higher");
    }

    [Fact]
    public async Task GetByCategories_OpenOverridesFromSeveralSourcesBeforeTheWindow_CarryInOnlyTheNewest()
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var (source, state, days) in new[]
                 {
                     ("Loop", "Exercise", 10),
                     ("loop://iPhone", "Exercise", 10),
                     ("Loop (via remote command)", "Pre-Meal", 1),
                 })
        {
            var span = SpanEntity(TestTenantId, StateSpanCategory.Override, state, from.AddDays(-days), end: null);
            span.Source = source;
            _context.StateSpans.Add(span);
        }
        await _context.SaveChangesAsync();

        var overrides = (await _repository.GetByCategories(
            [StateSpanCategory.Override], from, from.AddDays(1)))[StateSpanCategory.Override];

        overrides.Select(s => (s.Source, s.State)).Should().Equal(("Loop (via remote command)", "Pre-Meal"));
    }

    [Fact]
    public async Task GetByCategories_OpenSpansBeforeTheWindow_OverlappingCategoriesCarryInUpToTheCap()
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 15; i++)
            _context.StateSpans.Add(SpanEntity(
                TestTenantId, StateSpanCategory.Exercise, "Active", from.AddHours(-i), end: null));
        await _context.SaveChangesAsync();

        var exercise = (await _repository.GetByCategories(
            [StateSpanCategory.Exercise], from, from.AddDays(1)))[StateSpanCategory.Exercise];

        exercise.Select(s => s.StartTimestamp)
            .Should().BeEquivalentTo(Enumerable.Range(1, 10).Select(i => from.AddHours(-i)));
        _logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetByCategory_RangeOverlappingMoreThanAHundredSpans_ReturnsAllNewestFirst()
    {
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 150; i++)
            _context.StateSpans.Add(SpanEntity(
                TestTenantId, StateSpanCategory.Override, "Pre-Meal",
                from.AddMinutes(i), from.AddMinutes(i).AddSeconds(30)));
        _context.StateSpans.Add(SpanEntity(
            TestTenantId, StateSpanCategory.Exercise, "Running", from, from.AddMinutes(1)));
        await _context.SaveChangesAsync();

        var overrides = (await _repository.GetByCategory(
            StateSpanCategory.Override, from, from.AddHours(3))).ToList();

        overrides.Should().HaveCount(150);
        overrides.Select(s => s.StartTimestamp).Should().BeInDescendingOrder();
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
            .Where(s => s.LegacyId == originalId)
            .ToListAsync();

    [Fact]
    public async Task UserDeletedSpan_IsNotRecreatedByTheNextConnectorSync()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var originalRowId = (await RowsForAsync("glooko-exercise-1")).Single().Id;

        var deleted = await _repository.DeleteStateSpanAsync("glooko-exercise-1");
        deleted.Should().NotBeEmpty();

        await _repository.UpsertStateSpanAsync(ConnectorSpan());

        (await _repository.GetStateSpansAsync(category: StateSpanCategory.Exercise))
            .Should().BeEmpty("the delete must survive the next sync");

        var rows = await RowsForAsync("glooko-exercise-1");
        rows.Should().ContainSingle("the resync must not insert a second row")
            .Which.Id.Should().Be(originalRowId);
        rows[0].DeletedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null, "nocturne")]
    [InlineData("glooko-exercise-1", "nightscout")]
    public async Task UpdateActivityStateSpanAsync_KeepsTheStoredKeyAndSource_SoAResyncUpdatesIt(
        string? editedOriginalId, string editedSource)
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());

        var edited = ConnectorSpan();
        edited.State = "Cycling";
        edited.OriginalId = editedOriginalId;
        edited.Source = editedSource;
        var updated = await _repository.UpdateActivityStateSpanAsync("glooko-exercise-1", edited);

        updated.Should().NotBeNull();
        updated!.OriginalId.Should().Be("glooko-exercise-1");
        updated.Source.Should().Be("glooko-connector");
        updated.State.Should().Be("Cycling");

        await _repository.UpsertStateSpanAsync(ConnectorSpan());

        (await _context.StateSpans.AsNoTracking().Where(s => s.DeletedAt == null).ToListAsync())
            .Should().ContainSingle("the resync updates the edited row rather than inserting a second span")
            .Which.State.Should().Be("Running");
    }

    [Fact]
    public async Task UpdateActivityStateSpanAsync_ByRowId_KeepsTheStoredKeyAndSource()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var rowId = (await RowsForAsync("glooko-exercise-1")).Single().Id;

        var edited = ConnectorSpan();
        edited.OriginalId = rowId.ToString();
        edited.Source = "nightscout";
        var updated = await _repository.UpdateActivityStateSpanAsync(rowId.ToString(), edited);

        updated!.OriginalId.Should().Be("glooko-exercise-1");
        updated.Source.Should().Be("glooko-connector");
    }

    [Fact]
    public async Task SystemSweptSpan_IsRecreatedByTheNextConnectorSync()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var originalRowId = (await RowsForAsync("glooko-exercise-1")).Single().Id;

        _auditContext.IsSystem = true;
        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().NotBeEmpty();
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

        (await _repository.DeleteActivityStateSpanAsync("glooko-illness-1")).Should().NotBeEmpty();

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
        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().NotBeEmpty();

        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().BeEmpty();
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
        _context.StateSpans.AsNoTracking().ToDictionaryAsync(s => s.LegacyId!);

    private Task<IReadOnlyList<string>> RewriteAsync(IReadOnlyList<StateSpan> spans) =>
        _repository.RewriteFromUpstreamAsync("connector", spans);

    // --- Rewrite from upstream ---

    [Fact]
    public async Task RewriteFromUpstreamAsync_AShortenedSpanNothingEnded_TakesItsNewEnd()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 12),
            Span(StateSpanCategory.Override, "Custom", 14, "ov-b"),
        ]);
        var after = (await LiveRowsAsync())["ov-b"];

        var written = await RewriteAsync([Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 10)]);

        written.Should().Equal("ov-a");
        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["ov-a"].SupersededById.Should().BeNull();
        rows["ov-b"].UpdatedAt.Should().Be(after.UpdatedAt, "a span the rewrite does not move is not written");
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_ASpanShortenedBeforeTheSpanThatEndedIt_TakesItsOwnEnd()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await RewriteAsync([Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 9)]);

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(9));
        rows["ov-a"].SupersededById.Should().BeNull("an uploaded end is the span's own, which a later delete of ov-b must not move");
        rows["ov-b"].EndTimestamp.Should().BeNull();
    }

    [Theory]
    [InlineData(11)]
    [InlineData(null)]
    public async Task RewriteFromUpstreamAsync_ASpanWhoseEndLiesAfterTheSpanThatEndedIt_StaysEndedThere(int? endHour)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));
        var ended = (await LiveRowsAsync())["ov-b"].Id;

        await RewriteAsync([Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour)]);

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["ov-a"].SupersededById.Should().Be(ended);
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_ASpanMadeOpenBehindALaterSpan_EndsAtIt()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.TemporaryTarget, "Active", 8, "tt-a", endHour: 9),
            Span(StateSpanCategory.TemporaryTarget, "Active", 10, "tt-b"),
        ]);

        await RewriteAsync([Span(StateSpanCategory.TemporaryTarget, "Active", 8, "tt-a")]);

        var rows = await LiveRowsAsync();
        rows["tt-a"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["tt-a"].SupersededById.Should().Be(rows["tt-b"].Id);
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_ASpanThatMovesItsStart_ReSettlesTheSpanItEnded()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 12, "ov-c"));

        await RewriteAsync([Span(StateSpanCategory.Override, "Custom", 11, "ov-b")]);

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(11));
        rows["ov-a"].SupersededById.Should().Be(rows["ov-b"].Id);
        rows["ov-b"].StartTimestamp.Should().Be(BatchDay.AddHours(11));
        rows["ov-b"].EndTimestamp.Should().Be(BatchDay.AddHours(12), "it still ends where ov-c starts");
        rows["ov-b"].SupersededById.Should().Be(rows["ov-c"].Id);
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_ASpanNoLiveRowHolds_WritesNothing()
    {
        await _repository.BulkUpsertAsync([Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 9)]);
        await _repository.DeleteStateSpanAsync("ov-a");

        var written = await RewriteAsync(
            [Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 10), Span(StateSpanCategory.Override, "Custom", 8, "ov-none")]);

        written.Should().BeEmpty();
        (await LiveRowsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(9, 11)]
    [InlineData(7, 9)]
    public async Task RewriteFromUpstreamAsync_TwoNeighboursWhoseStartsBothMove_EndTheEarlierAtTheLatersNewStart(
        int earlierStart, int laterStart)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await RewriteAsync(
        [
            Span(StateSpanCategory.Override, "Custom", earlierStart, "ov-a"),
            Span(StateSpanCategory.Override, "Custom", laterStart, "ov-b"),
        ]);

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(laterStart));
        rows["ov-a"].SupersededById.Should().Be(rows["ov-b"].Id);
        rows["ov-b"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_ASpanWhoseCategoryChanges_ReleasesTheSpanItEnded()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await RewriteAsync([Span(StateSpanCategory.TemporaryTarget, "Active", 10, "ov-b")]);

        var rows = await LiveRowsAsync();
        rows["ov-b"].Category.Should().Be(nameof(StateSpanCategory.TemporaryTarget));
        rows["ov-a"].EndTimestamp.Should().BeNull("no override follows it any more");
        rows["ov-a"].SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_ASpanAnotherSourceStores_IsNotWritten()
    {
        var theirs = Span(StateSpanCategory.Override, "Custom", 8, "ov-a");
        theirs.Source = "uploader";
        await _repository.UpsertStateSpanAsync(theirs);
        var before = (await LiveRowsAsync())["ov-a"];

        var written = await RewriteAsync([Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 9)]);

        written.Should().BeEmpty();
        var row = (await LiveRowsAsync())["ov-a"];
        row.EndTimestamp.Should().BeNull();
        row.UpdatedAt.Should().Be(before.UpdatedAt);
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_AStartMovedEarlierPastASpan_EndsAtThatSpan()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await RewriteAsync([Span(StateSpanCategory.Override, "Custom", 7, "ov-b")]);

        var rows = await LiveRowsAsync();
        rows["ov-b"].EndTimestamp.Should().Be(BatchDay.AddHours(8));
        rows["ov-b"].SupersededById.Should().Be(rows["ov-a"].Id);
        rows["ov-a"].EndTimestamp.Should().BeNull("the span that ended it now starts before it");
        rows["ov-a"].SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_AStartMovedLaterPastTheSpanThatEndedIt_EndsThatSpanInstead()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await RewriteAsync([Span(StateSpanCategory.Override, "Custom", 11, "ov-a")]);

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().BeNull();
        rows["ov-a"].SupersededById.Should().BeNull();
        rows["ov-b"].EndTimestamp.Should().Be(BatchDay.AddHours(11));
        rows["ov-b"].SupersededById.Should().Be(rows["ov-a"].Id);
    }

    [Fact]
    public async Task RewriteFromUpstreamAsync_ASpanWhoseSupersederIsDeleted_EndsAtItsFirstLiveSuccessor()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 12, "ov-c"));
        var deleted = (await LiveRowsAsync())["ov-b"].Id;
        await _repository.DeleteStateSpanAsync("ov-b");
        await _context.StateSpans.Where(s => s.LegacyId == "ov-a").ExecuteUpdateAsync(u => u
            .SetProperty(s => s.EndTimestamp, BatchDay.AddHours(10))
            .SetProperty(s => s.SupersededById, deleted));

        await RewriteAsync([Span(StateSpanCategory.Override, "Custom", 8, "ov-a")]);

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(12));
        rows["ov-a"].SupersededById.Should().Be(rows["ov-c"].Id);
    }

    // --- An uploaded end against a superseded end ---

    /// <summary>A crawl writes an upload by the upsert, a reconcile by the rewrite; both must leave one row.</summary>
    private async Task UploadAsync(bool republish, params StateSpan[] spans)
    {
        if (republish)
            await RewriteAsync(spans);
        else
            await _repository.BulkUpsertAsync(spans);
    }

    [Theory]
    [InlineData(false, 11)]
    [InlineData(true, 11)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public async Task AnUploadedEndPastTheSpanThatEndedIt_KeepsTheEndThatSpanGave_ByEitherWriter(bool republish, int? endHour)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));
        var ended = (await LiveRowsAsync())["ov-b"].Id;

        await UploadAsync(republish, Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour));

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(10), "an upload does not stretch a span over the span that ended it");
        rows["ov-a"].SupersededById.Should().Be(ended);
        rows["ov-b"].EndTimestamp.Should().BeNull();
    }

    [Theory]
    [InlineData(false, 9)]
    [InlineData(true, 9)]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    public async Task AnUploadedEndAtOrBeforeTheSpanThatEndedIt_IsTheUploadsOwn_ByEitherWriter(bool republish, int endHour)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await UploadAsync(republish, Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour));

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(endHour));
        rows["ov-a"].SupersededById.Should().BeNull("an uploaded end is the span's own, which a later delete of ov-b must not move");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUploadPastTheSpanThatEndedIt_WhenThatSpanMoves_EndsAtItsNewStart_ByEitherWriter(bool republish)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await UploadAsync(republish,
            Span(StateSpanCategory.Override, "Custom", 12, "ov-b"),
            Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 13));

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(12));
        rows["ov-a"].SupersededById.Should().Be(rows["ov-b"].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUploadThatLeavesASpanAlone_WhenTheSpanThatEndedItMoves_EndsItAtItsNewStart_ByEitherWriter(bool republish)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await UploadAsync(republish,
            Span(StateSpanCategory.Override, "Custom", 8, "ov-a"),
            Span(StateSpanCategory.Override, "Custom", 12, "ov-b"));

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(12));
        rows["ov-a"].SupersededById.Should().Be(rows["ov-b"].Id);
    }

    /// <summary>
    /// D [6,8) ended by A, a span of another preset, and A [8,10) ended by B. The upload moves A to 5 with no
    /// end, so D runs on to B, and A now ends where D starts.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUploadThatMovesASpansStartBeforeTheSpanItEnded_ReleasesThatSpan_ByEitherWriter(bool republish)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 6, "ov-d"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Eating", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));
        var before = await LiveRowsAsync();
        before["ov-d"].SupersededById.Should().Be(before["ov-a"].Id);
        before["ov-a"].SupersededById.Should().Be(before["ov-b"].Id);

        await UploadAsync(republish, Span(StateSpanCategory.Override, "Eating", 5, "ov-a"));

        var rows = await LiveRowsAsync();
        (rows["ov-d"].StartTimestamp, rows["ov-d"].EndTimestamp, rows["ov-d"].SupersededById)
            .Should().Be((BatchDay.AddHours(6), BatchDay.AddHours(10), rows["ov-b"].Id));
        (rows["ov-a"].StartTimestamp, rows["ov-a"].EndTimestamp, rows["ov-a"].SupersededById)
            .Should().Be((BatchDay.AddHours(5), BatchDay.AddHours(6), rows["ov-d"].Id));
        (rows["ov-b"].EndTimestamp, rows["ov-b"].SupersededById).Should().Be((null, null));
    }

    /// <summary>
    /// ov-r ends at 8 by its own end but still names ov-m, which starts at 10. Moving ov-m clears that
    /// pointer, which is also what the re-sent ov-r leaves, so the save writes nothing more to ov-r.
    /// </summary>
    [Fact]
    public async Task BulkUpsertAsync_ARowTheReleaseAlreadyLeftAsSent_IsAnsweredWithItsStoredStamp()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-m"));
        var stalePointer = SpanEntity(
            _context.TenantId, StateSpanCategory.Override, "Eating", BatchDay.AddHours(6), BatchDay.AddHours(8));
        stalePointer.LegacyId = "ov-r";
        stalePointer.Source = "connector";
        stalePointer.SupersededById = (await LiveRowsAsync())["ov-m"].Id;
        _context.StateSpans.Add(stalePointer);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var answered = await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Override, "Custom", 12, "ov-m"),
            Span(StateSpanCategory.Override, "Eating", 6, "ov-r", endHour: 8),
        ]);

        var row = (await LiveRowsAsync())["ov-r"];
        (row.EndTimestamp, row.SupersededById).Should().Be((BatchDay.AddHours(8), null));
        var span = answered.Single(a => a.Span.OriginalId == "ov-r").Span;
        (span.EndTimestamp, span.SupersededById, span.UpdatedAt)
            .Should().Be((row.EndTimestamp, null, row.UpdatedAt), "the answer is the row as stored");
    }

    [Fact]
    public async Task BulkUpsertAsync_ThatMovesASpansStart_AnswersItAsStored()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 6, "ov-d"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Eating", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        var answered = (await _repository.BulkUpsertAsync([Span(StateSpanCategory.Override, "Eating", 5, "ov-a")]))
            .Should().ContainSingle().Subject;

        var rows = await LiveRowsAsync();
        answered.Outcome.Should().Be(UpsertOutcome.Updated);
        answered.Span.EndTimestamp.Should().Be(BatchDay.AddHours(6));
        answered.Span.SupersededById.Should().Be(rows["ov-d"].Id.ToString());
    }

    /// <summary>A ended by C at 12, then B [10,11) arrives between them, as a backfill with its own end.</summary>
    [Fact]
    public async Task BulkUpsertAsync_AnEndedSpanBetweenASpanAndItsSuperseder_EndsThatSpanAtIt()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 12, "ov-c"));

        var answered = await _repository.BulkUpsertAsync([Span(StateSpanCategory.Override, "Eating", 10, "ov-b", endHour: 11)]);

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["ov-a"].SupersededById.Should().Be(rows["ov-b"].Id);
        (rows["ov-b"].EndTimestamp, rows["ov-b"].SupersededById).Should().Be((BatchDay.AddHours(11), null));
        rows["ov-c"].EndTimestamp.Should().BeNull();
        answered.Should().ContainSingle().Which.Span.EndTimestamp.Should().Be(BatchDay.AddHours(11));
    }

    [Fact]
    public async Task BulkUpsertAsync_AnEndedSpanInsideASpanWithAnUploadedEnd_LeavesThatEnd()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 11),
            Span(StateSpanCategory.Override, "Custom", 12, "ov-c"),
        ]);
        var before = (await LiveRowsAsync())["ov-a"];

        await _repository.BulkUpsertAsync([Span(StateSpanCategory.Override, "Eating", 9, "ov-b", endHour: 10)]);

        var row = (await LiveRowsAsync())["ov-a"];
        (row.EndTimestamp, row.SupersededById, row.UpdatedAt)
            .Should().Be((BatchDay.AddHours(11), null, before.UpdatedAt), "an uploaded end is the span's own");
    }

    [Fact]
    public async Task UpdateStateSpansAsync_AFoldThatEndsASupersededSpanLater_KeepsTheEndItsSupersederGave()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));
        var ended = (await LiveRowsAsync())["ov-b"].Id;
        var extended = Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 11);
        extended.Id = "ov-a";

        await _repository.UpdateStateSpansAsync([extended]);

        var row = (await LiveRowsAsync())["ov-a"];
        row.EndTimestamp.Should().Be(BatchDay.AddHours(10));
        row.SupersededById.Should().Be(ended);
    }

    [Fact]
    public async Task UpdateStateSpansAsync_AFoldThatLeavesASupersededEndAlone_KeepsItsSuperseder()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));
        var ended = (await LiveRowsAsync())["ov-b"].Id;
        var moved = Span(StateSpanCategory.Override, "Custom", 9, "ov-a", endHour: 10);
        moved.Id = "ov-a";

        await _repository.UpdateStateSpansAsync([moved]);

        var row = (await LiveRowsAsync())["ov-a"];
        row.StartTimestamp.Should().Be(BatchDay.AddHours(9));
        row.EndTimestamp.Should().Be(BatchDay.AddHours(10));
        row.SupersededById.Should().Be(ended, "the fold handed back the end it read, which a delete of ov-b must still reopen");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateStateSpansAsync_AFoldThatMovesTheSpanThatEndedAnOpenedSpan_EndsItAtTheNewStart(bool supersederFirst)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.TemporaryTarget, "Custom", 8, "tt-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.TemporaryTarget, "Custom", 10, "tt-b"));
        var opened = Span(StateSpanCategory.TemporaryTarget, "Custom", 8, "tt-a");
        opened.Id = "tt-a";
        var moved = Span(StateSpanCategory.TemporaryTarget, "Custom", 11, "tt-b");
        moved.Id = "tt-b";

        var folded = await _repository.UpdateStateSpansAsync(supersederFirst ? [moved, opened] : [opened, moved]);

        var rows = await LiveRowsAsync();
        rows["tt-a"].EndTimestamp.Should().Be(BatchDay.AddHours(11), "the fold saves tt-b at 11 before tt-a is ended");
        rows["tt-a"].SupersededById.Should().Be(rows["tt-b"].Id);
        folded.Single(s => s!.OriginalId == "tt-a")!.EndTimestamp.Should().Be(BatchDay.AddHours(11));
    }

    [Fact]
    public async Task UpdateStateSpansAsync_AFoldThatAlsoMovesTheOpenedSpansStart_DoesNotEndItAtItself()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.TemporaryTarget, "Custom", 8, "tt-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.TemporaryTarget, "Custom", 10, "tt-b"));
        var moved = Span(StateSpanCategory.TemporaryTarget, "Custom", 11, "tt-b");
        moved.Id = "tt-b";
        var opened = Span(StateSpanCategory.TemporaryTarget, "Custom", 7, "tt-a");
        opened.Id = "tt-a";

        await _repository.UpdateStateSpansAsync([moved, opened]);

        var rows = await LiveRowsAsync();
        rows["tt-a"].StartTimestamp.Should().Be(BatchDay.AddHours(7));
        rows["tt-a"].EndTimestamp.Should().Be(BatchDay.AddHours(11));
        rows["tt-a"].SupersededById.Should().Be(rows["tt-b"].Id, "the stored tt-a at 8 is the span itself, not its successor");
    }

    [Fact]
    public async Task UpdateStateSpanAsync_AnEditedEndPastTheSpanThatEndedIt_IsTheEditors()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await _repository.UpdateStateSpanAsync("ov-a", Span(StateSpanCategory.Override, "Custom", 8, "ov-a", endHour: 11));

        var row = (await LiveRowsAsync())["ov-a"];
        row.EndTimestamp.Should().Be(BatchDay.AddHours(11), "a user's edit wins over supersession");
        row.SupersededById.Should().BeNull();
    }

    /// <summary>
    /// Settling the batch reads the store and writes the ends it moves in as many statements for fifty
    /// spans, each ended at the next, as for one.
    /// </summary>
    [Fact]
    public async Task BulkUpsertAsync_OpenSpansBehindAStoredSuccessor_RunAsManyStatementsForFiftyAsForOne()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.TemporaryTarget, "Custom", 200, "tt-stored"));

        _statements.Reset();
        await _repository.BulkUpsertAsync(
            [.. Enumerable.Range(0, 50).Select(i => Span(StateSpanCategory.TemporaryTarget, "Custom", 100 + i, $"tt-{i}"))]);
        var fifty = (_statements.Selects, _statements.Updates);

        _statements.Reset();
        await _repository.BulkUpsertAsync([Span(StateSpanCategory.TemporaryTarget, "Custom", 1, "tt-one")]);
        var one = (_statements.Selects, _statements.Updates);

        fifty.Should().Be(one);
        one.Updates.Should().Be(1);
        var rows = await LiveRowsAsync();
        rows["tt-0"].EndTimestamp.Should().Be(BatchDay.AddHours(101));
        rows["tt-49"].EndTimestamp.Should().Be(BatchDay.AddHours(200), "the last span ends at the stored successor");
        rows["tt-one"].EndTimestamp.Should().Be(BatchDay.AddHours(100));
    }

    /// <summary>
    /// A span of a category nothing supersedes releases nothing when its start moves, so the batch runs
    /// what one that only changes an end runs.
    /// </summary>
    [Fact]
    public async Task BulkUpsertAsync_ANonExclusiveSpanWhoseStartMoves_RunsNoSettle()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Exercise, "Running", 8, "ex-moved", endHour: 9),
            Span(StateSpanCategory.Exercise, "Running", 10, "ex-ended", endHour: 11),
        ]);

        _statements.Reset();
        await _repository.BulkUpsertAsync([Span(StateSpanCategory.Exercise, "Running", 7, "ex-moved", endHour: 9)]);
        var moved = (_statements.Selects, _statements.Updates, _statements.Transactions);

        _statements.Reset();
        await _repository.BulkUpsertAsync([Span(StateSpanCategory.Exercise, "Running", 10, "ex-ended", endHour: 12)]);
        var ended = (_statements.Selects, _statements.Updates, _statements.Transactions);

        moved.Should().Be(ended);
        (await LiveRowsAsync())["ex-moved"].StartTimestamp.Should().Be(BatchDay.AddHours(7));
    }

    /// <summary>
    /// A span under a long later history reads only the nearest stored spans as successor candidates,
    /// not the whole history after it.
    /// </summary>
    [Fact]
    public async Task LoadCandidatesAsync_UnderALongLaterHistory_ReadsOnlyTheNearestSpans()
    {
        await _repository.BulkUpsertAsync(
            [.. Enumerable.Range(0, 60).Select(i =>
                Span(StateSpanCategory.TemporaryTarget, "Custom", 300 + i, $"tt-history-{i}", endHour: 301 + i))]);
        var key = StateSpanSuccession.Key(nameof(StateSpanCategory.TemporaryTarget), "Custom", BatchDay.AddHours(1));

        var candidates = await StateSpanSuccession.LoadCandidatesAsync(_context, [key], CancellationToken.None);

        candidates[key].Select(s => s.LegacyId).Should().Equal(
            Enumerable.Range(0, StateSpanSuccession.Candidates).Select(i => $"tt-history-{i}"));
    }

    /// <summary>
    /// When every candidate read is a copy of the same override, which does not end it, the successor
    /// is the first conflicting span beyond them.
    /// </summary>
    [Fact]
    public async Task BulkUpsertAsync_EveryCandidatePassedOver_EndsAtTheFirstConflictingSpanBeyond()
    {
        StateSpan Override(int hour, string id, string collection, string name, int? endHour) => new()
        {
            Category = StateSpanCategory.Override,
            State = "Custom",
            StartTimestamp = BatchDay.AddHours(hour),
            EndTimestamp = endHour is { } end ? BatchDay.AddHours(end) : null,
            Source = "loop://iPhone",
            OriginalId = id,
            Metadata = new() { [StateSpanMetadataExtensions.CollectionKey] = collection, ["name"] = name },
        };
        await _repository.BulkUpsertAsync(
        [
            .. Enumerable.Range(0, StateSpanSuccession.Candidates + 2)
                .Select(i => Override(300 + i, $"ov-same-{i}", StateSpanMetadataExtensions.DeviceStatusCollection, "Run", 301 + i)),
            Override(400, "ov-other", StateSpanMetadataExtensions.DeviceStatusCollection, "Sleep", 401),
        ]);

        await _repository.BulkUpsertAsync([Override(1, "ov-treatment", StateSpanMetadataExtensions.TreatmentsCollection, "Run", null)]);

        (await LiveRowsAsync())["ov-treatment"].EndTimestamp.Should().Be(BatchDay.AddHours(400));
    }

    [Fact]
    public async Task BulkUpsertAsync_ANewSpan_IsCreated()
    {
        var written = await _repository.BulkUpsertAsync([Span(StateSpanCategory.Exercise, "Running", 8, "ex-new", endHour: 9)]);

        written.Should().ContainSingle().Which.Outcome.Should().Be(UpsertOutcome.Created);
    }

    [Fact]
    public async Task BulkUpsertAsync_AnIdenticalReupsert_IsUnchanged()
    {
        await _repository.BulkUpsertAsync([Span(StateSpanCategory.Exercise, "Running", 8, "ex-same", endHour: 9)]);

        var written = await _repository.BulkUpsertAsync([Span(StateSpanCategory.Exercise, "Running", 8, "ex-same", endHour: 9)]);

        written.Should().ContainSingle().Which.Outcome.Should().Be(UpsertOutcome.Unchanged);
    }

    [Fact]
    public async Task BulkUpsertAsync_AReupsertWithANewEndOrMetadata_IsUpdated()
    {
        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Exercise, "Running", 8, "ex-end", endHour: 9),
            Span(StateSpanCategory.Exercise, "Running", 10, "ex-metadata", endHour: 11),
        ]);
        var newMetadata = Span(StateSpanCategory.Exercise, "Running", 10, "ex-metadata", endHour: 11);
        newMetadata.Metadata = new() { ["intensity"] = "high" };

        var written = await _repository.BulkUpsertAsync(
            [Span(StateSpanCategory.Exercise, "Running", 8, "ex-end", endHour: 10), newMetadata]);

        written.Select(w => w.Outcome).Should().Equal(UpsertOutcome.Updated, UpsertOutcome.Updated);
    }

    [Fact]
    public async Task BulkUpsertAsync_ASpanAUserDeleted_WritesNothing()
    {
        await _repository.BulkUpsertAsync([Span(StateSpanCategory.Exercise, "Running", 8, "ex-deleted", endHour: 9)]);
        await _repository.DeleteStateSpanAsync("ex-deleted");

        var written = await _repository.BulkUpsertAsync([Span(StateSpanCategory.Exercise, "Running", 8, "ex-deleted", endHour: 9)]);

        written.Should().ContainSingle().Which.Outcome.Should().BeNull();
        (await LiveRowsAsync()).Should().NotContainKey("ex-deleted");
    }

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
    public async Task CreateActivitiesAsStateSpansAsync_XDripMotionClass_EachRecordEndsTheOneBeforeIt()
    {
        static StateSpan Motion(string motionClass, long at)
        {
            var activity = System.Text.Json.JsonSerializer.Deserialize<Activity>($$"""
                {"type":"motion-class","timeStamp":{{at}},"created_at":"2026-05-28T20:26:40Z","class":"{{motionClass}}"}
                """)!;
            activity.Mills = at;
            return Nocturne.Infrastructure.Data.Mappers.ActivityStateSpanMapper.ToStateSpan(activity);
        }

        var still = new DateTimeOffset(BatchDay.AddHours(8)).ToUnixTimeMilliseconds();
        var walking = still + 600_000;
        var inVehicle = walking + 600_000;

        await _repository.CreateActivitiesAsStateSpansAsync([Motion("still", still)]);
        await _repository.CreateActivitiesAsStateSpansAsync(
            [Motion("walking", walking), Motion("in vehicle", inVehicle)]);

        var rows = await _context.StateSpans.AsNoTracking().ToDictionaryAsync(s => s.State);
        rows.Values.Should().OnlyContain(s => s.Category == nameof(StateSpanCategory.Motion));
        rows["still"].EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(walking).UtcDateTime);
        rows["still"].SupersededById.Should().Be(rows["walking"].Id);
        rows["walking"].EndTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(inVehicle).UtcDateTime);
        rows["walking"].SupersededById.Should().Be(rows["in vehicle"].Id);
        rows["in vehicle"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task BulkUpsertAsync_OutOfOrderBatch_EndsEachSpanAtTheNextStart()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 3, "pr-stored"));

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.PumpMode, "Automatic", 10, "pr-late"),
            Span(StateSpanCategory.PumpMode, "Automatic", 5, "pr-early"),
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
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 1, "pr-x"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 5, "pr-b"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 3, "pr-c"));

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
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 1, "pr-x"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 5, "pr-b"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 1, "pr-x", endHour: 2));
        (await LiveRowsAsync())["pr-x"].SupersededById.Should().BeNull();

        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 3, "pr-c"));

        var rows = await LiveRowsAsync();
        rows["pr-x"].EndTimestamp.Should().Be(BatchDay.AddHours(2));
        rows["pr-x"].SupersededById.Should().BeNull();
        rows["pr-c"].EndTimestamp.Should().Be(BatchDay.AddHours(5));
    }

    [Fact]
    public async Task UpsertStateSpanAsync_SupersededSpanReUploadedOpen_StaysClosedAtItsSuccessorsStart()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "ov-a"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "ov-b"));

        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "ov-a"));

        var rows = await LiveRowsAsync();
        rows["ov-a"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["ov-a"].SupersededById.Should().Be(rows["ov-b"].Id);
        rows["ov-b"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task UpsertStateSpanAsync_BackfillBehindASuccessor_LeavesAnEndItDidNotSet()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 5, "pr-b"));
        var successorId = (await LiveRowsAsync())["pr-b"].Id;
        var stalePointer = SpanEntity(
            _context.TenantId, StateSpanCategory.Profile, "Active", BatchDay.AddHours(1), BatchDay.AddHours(2));
        stalePointer.LegacyId = "pr-x";
        stalePointer.SupersededById = successorId;
        _context.StateSpans.Add(stalePointer);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 3, "pr-c"));

        var rows = await LiveRowsAsync();
        rows["pr-x"].EndTimestamp.Should().Be(BatchDay.AddHours(2));
        rows["pr-x"].SupersededById.Should().Be(successorId);
    }

    [Fact]
    public async Task BulkUpsertAsync_BackfillBehindASuccessor_LeavesAnEndItDidNotSetOnARowInTheBatch()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 5, "pr-b"));
        var successorId = (await LiveRowsAsync())["pr-b"].Id;
        var stalePointer = SpanEntity(
            _context.TenantId, StateSpanCategory.Profile, "Active", BatchDay.AddHours(1), BatchDay.AddHours(2));
        stalePointer.LegacyId = "pr-x";
        stalePointer.SupersededById = successorId;
        _context.StateSpans.Add(stalePointer);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.PumpMode, "Automatic", 1, "pr-x", endHour: 2),
            Span(StateSpanCategory.PumpMode, "Automatic", 3, "pr-c"),
        ]);

        (await LiveRowsAsync())["pr-x"].EndTimestamp.Should().Be(BatchDay.AddHours(2));
    }

    [Fact]
    public async Task BulkUpsertAsync_StoredSuccessorMovedByTheBatch_IsReadAtItsNewStart()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 5, "pr-moved"));

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.PumpMode, "Automatic", 8, "pr-moved"),
            Span(StateSpanCategory.PumpMode, "Automatic", 3, "pr-backfilled"),
        ]);

        var rows = await LiveRowsAsync();
        rows["pr-backfilled"].EndTimestamp.Should().Be(BatchDay.AddHours(8));
        rows["pr-backfilled"].SupersededById.Should().Be(rows["pr-moved"].Id);
    }

    [Fact]
    public async Task BulkUpsertAsync_BackfillBehindASuccessor_LeavesATombstoneItClosed()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 1, "pr-x"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 5, "pr-b"));
        (await _repository.DeleteStateSpanAsync("pr-x")).Should().NotBeEmpty();

        await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.PumpMode, "Automatic", 1, "pr-x"),
            Span(StateSpanCategory.PumpMode, "Automatic", 3, "pr-c"),
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
            Span(StateSpanCategory.PumpMode, "Automatic", 1, "pr-x"),
            Span(StateSpanCategory.PumpMode, "Automatic", 5, "pr-b"),
            Span(StateSpanCategory.PumpMode, "Automatic", 1, "pr-c"),
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

        var written = await _repository.BulkUpsertAsync(
        [
            Span(StateSpanCategory.Exercise, "Running", 8, "ex-stored", endHour: 9),
            Span(StateSpanCategory.Exercise, "Cycling", 10, "ex-new"),
            Span(StateSpanCategory.Exercise, "Cycling", 10, "ex-new", endHour: 11),
        ]);

        written.Select(w => w.Outcome).Should().Equal(UpsertOutcome.Updated, UpsertOutcome.Created, UpsertOutcome.Created);
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
        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().NotBeEmpty();

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
        (await _repository.DeleteStateSpanAsync("ov-deleted")).Should().NotBeEmpty();

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
            tombstone.LegacyId = "ex-twice-deleted";
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
        (await _repository.DeleteStateSpanAsync("glooko-exercise-1")).Should().NotBeEmpty();
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
    public async Task BulkUpsertAsync_ACancelledDedup_PropagatesAfterReportingTheCommittedWrite()
    {
        var legacyReads = new Mock<ILegacyReadCacheInvalidation>();
        legacyReads.Setup(r => r.WrittenAsync(It.IsAny<IReadOnlyCollection<Type>>())).Returns(Task.CompletedTask);
        _mockDedup
            .Setup(d => d.DeduplicateBatchAsync(
                It.IsAny<RecordType>(), It.IsAny<IReadOnlyList<DeduplicationInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var repository = new StateSpanRepository(
            _context.AsFactory(), _mockDedup.Object, _auditContext, _logger.Object, legacyReads.Object);

        var act = () => repository.BulkUpsertAsync([Span(StateSpanCategory.Override, "Custom", 9, "ov-cancelled")]);

        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;
        legacyReads.Verify(r => r.WrittenAsync(It.Is<IReadOnlyCollection<Type>>(t => t.Contains(typeof(StateSpan)))), Times.Once);
        CommittedWrite.TryGet<IReadOnlyList<StateSpanUpsert>>(thrown, out var committed).Should().BeTrue();
        committed!.Should().ContainSingle().Which.Span.OriginalId.Should().Be("ov-cancelled");
        (await LiveRowsAsync()).Should().ContainKey("ov-cancelled");
    }

    [Fact]
    public async Task BulkUpsertAsync_ALinkedWrite_TellsTheLegacyReadCacheAgainAfterTheLink()
    {
        var order = new List<string>();
        var legacyReads = new Mock<ILegacyReadCacheInvalidation>();
        legacyReads
            .Setup(r => r.WrittenAsync(It.IsAny<IReadOnlyCollection<Type>>()))
            .Callback(() => order.Add("reported"))
            .Returns(Task.CompletedTask);
        _mockDedup
            .Setup(d => d.DeduplicateBatchAsync(
                It.IsAny<RecordType>(), It.IsAny<IReadOnlyList<DeduplicationInput>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("linked"))
            .ReturnsAsync(new DeduplicationBatchResult(0, 0, 0, 0));
        var repository = new StateSpanRepository(
            _context.AsFactory(), _mockDedup.Object, _auditContext, _logger.Object, legacyReads.Object);

        await repository.BulkUpsertAsync([Span(StateSpanCategory.Override, "Custom", 9, "ov-linked")]);

        order.Should().Equal("reported", "linked", "reported");
    }

    [Fact]
    public async Task BulkUpsertAsync_ADedupThatFails_IsLoggedAndTheUpsertReturns()
    {
        _mockDedup
            .Setup(d => d.DeduplicateBatchAsync(
                It.IsAny<RecordType>(), It.IsAny<IReadOnlyList<DeduplicationInput>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dedup failed"));

        var written = await _repository.BulkUpsertAsync([Span(StateSpanCategory.Override, "Custom", 9, "ov-failed")]);

        written.Should().ContainSingle().Which.Outcome.Should().Be(UpsertOutcome.Created);
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<InvalidOperationException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task CreateActivitiesAsStateSpansAsync_DoesNotReturnASpanTheUserDeleted()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Exercise, "Running", 8, "act-deleted"));
        (await _repository.DeleteStateSpanAsync("act-deleted")).Should().NotBeEmpty();

        var created = (await _repository.CreateActivitiesAsStateSpansAsync(
        [
            Span(StateSpanCategory.Exercise, "Running", 8, "act-deleted"),
            Span(StateSpanCategory.Illness, "Flu", 10, "act-fresh"),
        ])).ToList();

        created.Select(s => s.OriginalId).Should().Equal("act-fresh");
        (await RowsForAsync("act-deleted")).Should().ContainSingle().Which.DeletedAt.Should().NotBeNull();
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

    // --- Restore and supersession ---

    private async Task<Guid> RowIdAsync(string originalId) => (await RowsForAsync(originalId)).Single().Id;

    private Task<bool> DeletedByUserAsync(Guid id) =>
        _context.StateSpans.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => EF.Property<bool>(s, "DeletedByUser"))
            .SingleAsync();

    [Fact]
    public async Task RestoreAsync_BringsBackAUserDeletedSpan_AndALaterSyncUpsertsIt()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var id = await RowIdAsync("glooko-exercise-1");
        await _repository.DeleteStateSpanAsync("glooko-exercise-1");
        (await DeletedByUserAsync(id)).Should().BeTrue();

        var restored = await _repository.RestoreAsync(id, WriteOrigin.Live);

        restored.OriginalId.Should().Be("glooko-exercise-1");
        (await _repository.GetStateSpanByIdAsync("glooko-exercise-1")).Should().NotBeNull();
        (await DeletedByUserAsync(id)).Should().BeFalse();

        var resync = ConnectorSpan();
        resync.State = "Cycling";
        await _repository.UpsertStateSpanAsync(resync);

        var rows = await RowsForAsync("glooko-exercise-1");
        rows.Should().ContainSingle().Which.State.Should().Be("Cycling");
        rows[0].DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task RestoreAsync_OfASpanThatIsNotDeleted_Throws()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());

        var id = await RowIdAsync("glooko-exercise-1");
        var act = () => _repository.RestoreAsync(id, WriteOrigin.Live);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RestoreAsync_RefusesASpanWhoseOriginalIdALiveSpanNowHolds()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var swept = await RowIdAsync("glooko-exercise-1");
        _auditContext.IsSystem = true;
        await _repository.DeleteStateSpanAsync("glooko-exercise-1");
        _auditContext.IsSystem = false;
        await _repository.UpsertStateSpanAsync(ConnectorSpan());

        var act = () => _repository.RestoreAsync(swept, WriteOrigin.Live);

        await act.Should().ThrowAsync<RecreationBlockedException>().WithMessage("*glooko-exercise-1*");
        (await RowsForAsync("glooko-exercise-1")).Should().ContainSingle(r => r.Id == swept && r.DeletedAt != null);
    }

    [Fact]
    public async Task BulkRestoreAsync_ReportsALiveHeldOriginalIdAsAConflict_AndRestoresTheRest()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Illness, "Flu", 10, "act-b"));
        var clashing = await RowIdAsync("glooko-exercise-1");
        var clear = await RowIdAsync("act-b");
        _auditContext.IsSystem = true;
        await _repository.DeleteStateSpanAsync("glooko-exercise-1");
        _auditContext.IsSystem = false;
        await _repository.DeleteStateSpanAsync("act-b");
        await _repository.UpsertStateSpanAsync(ConnectorSpan());

        var result = await _repository.BulkRestoreAsync([clashing, clear], WriteOrigin.Live);

        result.Conflicts.Should().Equal(clashing);
        result.Restored.Select(s => s.OriginalId).Should().Equal("act-b");
        (await RowsForAsync("act-b")).Should().ContainSingle().Which.DeletedAt.Should().BeNull();
        (await RowsForAsync("glooko-exercise-1")).Should().ContainSingle(r => r.Id == clashing && r.DeletedAt != null);
    }

    [Fact]
    public async Task ResolveDeletedAsync_NamesADeletedSpanByTheIdReadsReturnForIt()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Illness, "Flu", 10, "act-live"));
        var deleted = await RowIdAsync("glooko-exercise-1");
        await _repository.DeleteStateSpanAsync("glooko-exercise-1");

        var byOriginalId = await _repository.ResolveDeletedAsync(["glooko-exercise-1", "act-live", "nope"]);
        var byRowId = await _repository.ResolveDeletedAsync([deleted.ToString()]);

        byOriginalId.Should().ContainSingle().Which.Should().Be(
            new KeyValuePair<Guid, DeletedStateSpan>(deleted, new("glooko-exercise-1", StateSpanCategory.Exercise)));
        byRowId.Keys.Should().Equal(byOriginalId.Keys);
        byRowId[deleted].SentId.Should().Be(deleted.ToString());
    }

    [Fact]
    public async Task ResolveDeletedAsync_NamesTheLatestDeletedOfSpansSharingAnOriginalId()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var swept = await RowIdAsync("glooko-exercise-1");
        _auditContext.IsSystem = true;
        await _repository.DeleteStateSpanAsync("glooko-exercise-1");
        _auditContext.IsSystem = false;
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        var second = (await RowsForAsync("glooko-exercise-1")).Single(r => r.Id != swept).Id;
        await _repository.DeleteStateSpanAsync("glooko-exercise-1");

        var named = await _repository.ResolveDeletedAsync(["glooko-exercise-1"]);

        named.Keys.Should().Equal(second);
    }

    [Fact]
    public async Task RestoreAsync_ByTheResolvedIdOfASpanWithAnOriginalId_BringsItBack()
    {
        await _repository.UpsertStateSpanAsync(ConnectorSpan());
        await _repository.DeleteStateSpanAsync("glooko-exercise-1");

        var rowId = (await _repository.ResolveDeletedAsync(["glooko-exercise-1"])).Keys.Single();
        await _repository.RestoreAsync(rowId, WriteOrigin.Live);

        (await _repository.GetStateSpanByIdAsync("glooko-exercise-1")).Should().NotBeNull();
    }

    private async Task<(Guid Superseded, Guid Supersessor)> SupersededPairAsync(string supersessorSource)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "first"));
        var second = Span(StateSpanCategory.Override, "Custom", 9, "second");
        second.Source = supersessorSource;
        await _repository.UpsertStateSpanAsync(second);

        var first = (await LiveRowsAsync())["first"];
        var supersessor = await RowIdAsync("second");
        first.SupersededById.Should().Be(supersessor);
        return (first.Id, supersessor);
    }

    [Fact]
    public async Task DeleteStateSpanAsync_ReopensTheSpanItSuperseded()
    {
        await SupersededPairAsync("connector");

        await _repository.DeleteStateSpanAsync("second");

        var first = (await LiveRowsAsync())["first"];
        first.SupersededById.Should().BeNull();
        first.EndTimestamp.Should().BeNull();
        (await _repository.GetStateSpansAsync(category: StateSpanCategory.Override, active: true))
            .Should().ContainSingle().Which.OriginalId.Should().Be("first");
    }

    [Fact]
    public async Task DeleteBySourceAsync_ReopensALiveSpanADeletedOneSuperseded()
    {
        await SupersededPairAsync("other-connector");

        await _repository.DeleteBySourceAsync("other-connector");

        var first = (await LiveRowsAsync()).Should().ContainSingle().Which.Value;
        first.SupersededById.Should().BeNull();
        first.EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task DeleteStateSpanAsync_EndsTheReopenedSpanAtItsNextLiveSuccessor()
    {
        await SupersededPairAsync("connector");
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "third"));

        await _repository.DeleteStateSpanAsync("second");

        var rows = await LiveRowsAsync();
        rows["first"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["first"].SupersededById.Should().Be(rows["third"].Id);
        rows["third"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task DeleteStateSpanAsync_LeavesAnUploadedEndOnASpanPointingAtIt()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "second"));
        var uploadedEnd = SpanEntity(
            _context.TenantId, StateSpanCategory.Override, "Custom", BatchDay.AddHours(8), BatchDay.AddHours(8.5));
        uploadedEnd.LegacyId = "first";
        uploadedEnd.SupersededById = await RowIdAsync("second");
        _context.StateSpans.Add(uploadedEnd);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _repository.DeleteStateSpanAsync("second");

        var first = (await LiveRowsAsync())["first"];
        first.SupersededById.Should().BeNull();
        first.EndTimestamp.Should().Be(BatchDay.AddHours(8.5));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteBySourceAsync_NeverEndsAReopenedSpanAtASpanTheSameDeleteRemoves(bool laterSurvivor)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "kept"));
        foreach (var (hour, id) in new[] { (9, "removed-1"), (10, "removed-2") })
        {
            var removed = Span(StateSpanCategory.Override, "Custom", hour, id);
            removed.Source = "removed-connector";
            await _repository.UpsertStateSpanAsync(removed);
        }

        if (laterSurvivor)
            await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 11, "later"));

        await _repository.DeleteBySourceAsync("removed-connector");

        var rows = await LiveRowsAsync();
        rows["kept"].EndTimestamp.Should().Be(laterSurvivor ? BatchDay.AddHours(11) : (DateTime?)null);
        rows["kept"].SupersededById.Should().Be(laterSurvivor ? rows["later"].Id : (Guid?)null);
    }

    [Fact]
    public async Task DeleteStateSpanAsync_PumpMode_ReopensPastALaterSpanOfAnotherState()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 8, "pm-first"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Automatic", 9, "pm-second"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, "Suspended", 10, "pm-suspended"));

        await _repository.DeleteStateSpanAsync("pm-second");

        var first = (await LiveRowsAsync())["pm-first"];
        first.EndTimestamp.Should().BeNull();
        first.SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task DeleteStateSpanAsync_ReopensPastASnapshotOfTheSameOverride()
    {
        var day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "treatment", day.AddHours(8), "Loop", TreatmentMetadata("N Night", 0.9)));
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "different", day.AddHours(9), "Loop", TreatmentMetadata("X Exercise", 0.5)));
        await _repository.UpsertStateSpanAsync(LoopOverride(
            "snapshot", day.AddHours(10), "loop://iPhone", DeviceStatusMetadata("Night", 0.9)));
        (await LiveRowsAsync())["treatment"].EndTimestamp.Should().Be(day.AddHours(9));

        await _repository.DeleteStateSpanAsync("different");

        var treatment = (await LiveRowsAsync())["treatment"];
        treatment.EndTimestamp.Should().BeNull("a snapshot of the same override does not end it");
        treatment.SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task RestoreAsync_OfADeletedSuperseder_EndsTheSpanItReopenedAgain()
    {
        var (_, supersessor) = await SupersededPairAsync("connector");
        await _repository.DeleteStateSpanAsync("second");

        await _repository.RestoreAsync(supersessor, WriteOrigin.Live);

        var rows = await LiveRowsAsync();
        rows["first"].EndTimestamp.Should().Be(BatchDay.AddHours(9));
        rows["first"].SupersededById.Should().Be(supersessor);
        rows["second"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task RestoreAsync_OfASupersederBetweenTwoSpans_EndsTheEarlierAtItAndLeavesItEndedByTheLater()
    {
        var (_, supersessor) = await SupersededPairAsync("connector");
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "third"));
        await _repository.DeleteStateSpanAsync("second");
        (await LiveRowsAsync())["first"].EndTimestamp.Should().Be(BatchDay.AddHours(10));

        await _repository.RestoreAsync(supersessor, WriteOrigin.Live);

        var rows = await LiveRowsAsync();
        rows["first"].EndTimestamp.Should().Be(BatchDay.AddHours(9));
        rows["first"].SupersededById.Should().Be(supersessor);
        rows["second"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["second"].SupersededById.Should().Be(rows["third"].Id);
        rows["third"].EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task RestoreAsync_LeavesAnUploadedEndThatRunsPastTheRestoredSpan()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "first", endHour: 11));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "second"));
        var second = await RowIdAsync("second");
        await _repository.DeleteStateSpanAsync("second");

        await _repository.RestoreAsync(second, WriteOrigin.Live);

        var first = (await LiveRowsAsync())["first"];
        first.EndTimestamp.Should().Be(BatchDay.AddHours(11));
        first.SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task RestoreAsync_OfAPumpModeSpan_EndsAnOpenSpanOfItsOwnStateOnly()
    {
        var automatic = PumpModeState.Automatic.ToString();
        await _repository.UpsertStateSpanAsync(
            Span(StateSpanCategory.PumpMode, PumpModeState.Suspended.ToString(), 9, "suspended"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, automatic, 10, "automatic"));
        var suspended = await RowIdAsync("suspended");
        var restoredAutomatic = await RowIdAsync("automatic");
        await _repository.DeleteStateSpanAsync("suspended");
        await _repository.DeleteStateSpanAsync("automatic");
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.PumpMode, automatic, 8, "open"));

        await _repository.RestoreAsync(suspended, WriteOrigin.Live);

        (await LiveRowsAsync())["open"].EndTimestamp.Should().BeNull();

        await _repository.RestoreAsync(restoredAutomatic, WriteOrigin.Live);

        var open = (await LiveRowsAsync())["open"];
        open.EndTimestamp.Should().Be(BatchDay.AddHours(10));
        open.SupersededById.Should().Be(restoredAutomatic);
    }

    [Fact]
    public async Task BulkRestoreAsync_OfASourcesSpans_RebuildsTheSupersessionTheirDeleteUndid()
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 8, "kept"));
        foreach (var (hour, id) in new[] { (9, "removed-1"), (10, "removed-2") })
        {
            var removed = Span(StateSpanCategory.Override, "Custom", hour, id);
            removed.Source = "removed-connector";
            await _repository.UpsertStateSpanAsync(removed);
        }

        var removedIds = new[] { await RowIdAsync("removed-1"), await RowIdAsync("removed-2") };
        await _repository.DeleteBySourceAsync("removed-connector");
        (await LiveRowsAsync())["kept"].EndTimestamp.Should().BeNull();

        await _repository.BulkRestoreAsync(removedIds, WriteOrigin.Live);

        var rows = await LiveRowsAsync();
        rows["kept"].EndTimestamp.Should().Be(BatchDay.AddHours(9));
        rows["kept"].SupersededById.Should().Be(removedIds[0]);
        rows["removed-1"].EndTimestamp.Should().Be(BatchDay.AddHours(10));
        rows["removed-1"].SupersededById.Should().Be(removedIds[1]);
        rows["removed-2"].EndTimestamp.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceBySourceAsync_UndoesTheSupersessionAPurgedSpanGaveAnotherSource(bool laterSpanWritten)
    {
        await SupersededPairAsync("seeder");
        var later = Span(StateSpanCategory.Override, "Custom", 10, "seeded-later");
        later.Source = "seeder";

        await _repository.ReplaceBySourceAsync("seeder", laterSpanWritten ? [later] : []);

        var rows = await LiveRowsAsync();
        rows.Should().NotContainKey("second");
        rows["first"].EndTimestamp.Should().Be(laterSpanWritten ? BatchDay.AddHours(10) : (DateTime?)null);
        rows["first"].SupersededById.Should().Be(laterSpanWritten ? rows["seeded-later"].Id : (Guid?)null);
    }

    [Fact]
    public async Task ReplaceBySourceAsync_WritesItsOwnSpansAsGiven()
    {
        var early = Span(StateSpanCategory.Override, "Custom", 8, "seeded-early");
        var late = Span(StateSpanCategory.Override, "Custom", 10, "seeded-late");
        early.Source = late.Source = "seeder";

        await _repository.ReplaceBySourceAsync("seeder", [early, late]);

        var rows = await LiveRowsAsync();
        rows["seeded-early"].EndTimestamp.Should().BeNull();
        rows["seeded-early"].SupersededById.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreAsync_AnswersTheSuccessorTheRestoredSpanWasEndedAt(bool bulk)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "restored"));
        var restoredId = await RowIdAsync("restored");
        await _repository.DeleteStateSpanAsync("restored");
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "arrived-meanwhile"));
        var successor = await RowIdAsync("arrived-meanwhile");

        var answered = await RestoreAsync(restoredId, bulk);

        answered.EndTimestamp.Should().Be(BatchDay.AddHours(10));
        answered.SupersededById.Should().Be(successor.ToString());
        var stored = (await LiveRowsAsync())["restored"];
        stored.EndTimestamp.Should().Be(BatchDay.AddHours(10));
        stored.SupersededById.Should().Be(successor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreAsync_AnswersOpenASpanWhoseSupersederWasDeleted(bool bulk)
    {
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 9, "restored"));
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Override, "Custom", 10, "superseder"));
        var restoredId = await RowIdAsync("restored");
        await _repository.DeleteStateSpanAsync("restored");
        await _repository.DeleteStateSpanAsync("superseder");

        var answered = await RestoreAsync(restoredId, bulk);

        answered.EndTimestamp.Should().BeNull();
        answered.SupersededById.Should().BeNull();
        var stored = (await LiveRowsAsync())["restored"];
        stored.EndTimestamp.Should().BeNull();
        stored.SupersededById.Should().BeNull();
    }

    private async Task<StateSpan> RestoreAsync(Guid id, bool bulk) => bulk
        ? (await _repository.BulkRestoreAsync([id], WriteOrigin.Live)).Restored.Should().ContainSingle().Subject
        : await _repository.RestoreAsync(id, WriteOrigin.Live);

    [Fact]
    public async Task ReplaceBySourceAsync_ReopensADeletedSpanAPurgedSpanEnded_SoItsRestoreIsOpen()
    {
        var (superseded, _) = await SupersededPairAsync("seeder");
        await _repository.DeleteStateSpanAsync("first");

        await _repository.ReplaceBySourceAsync("seeder", []);

        var tombstone = (await RowsForAsync("first")).Should().ContainSingle().Subject;
        tombstone.DeletedAt.Should().NotBeNull();
        tombstone.EndTimestamp.Should().BeNull();
        tombstone.SupersededById.Should().BeNull();
        (await _repository.RestoreAsync(superseded, WriteOrigin.Live)).EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task DeleteStateSpanAsync_LeavesAnUnrelatedSpansSupersededByAlone()
    {
        var (_, supersessor) = await SupersededPairAsync("connector");
        await _repository.UpsertStateSpanAsync(Span(StateSpanCategory.Exercise, "Running", 7, "bystander"));

        await _repository.DeleteStateSpanAsync("bystander");

        (await LiveRowsAsync())["first"].SupersededById.Should().Be(supersessor);
    }

    // --- A supersession pointer a caller sends is never stored ---

    private static StateSpan SpanNamingASuperseder(string originalId)
    {
        var span = Span(StateSpanCategory.Override, "Custom", 8, originalId);
        span.SupersededById = Guid.CreateVersion7().ToString();
        return span;
    }

    [Fact]
    public async Task UpsertStateSpanAsync_InsertingASpanThatNamesASuperseder_StoresNoPointer()
    {
        var answered = await _repository.UpsertStateSpanAsync(SpanNamingASuperseder("planted"));

        (await LiveRowsAsync())["planted"].SupersededById.Should().BeNull();
        answered.SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task BulkUpsertAsync_InsertingASpanThatNamesASuperseder_StoresNoPointer()
    {
        await _repository.BulkUpsertAsync([SpanNamingASuperseder("planted")]);

        (await LiveRowsAsync())["planted"].SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task ReplaceBySourceAsync_WritingASpanThatNamesASuperseder_StoresNoPointer()
    {
        var planted = SpanNamingASuperseder("planted");
        planted.Source = "seeder";

        await _repository.ReplaceBySourceAsync("seeder", [planted]);

        (await LiveRowsAsync())["planted"].SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task StoreDeletedAsync_StoringASpanThatNamesASuperseder_StoresNoPointer()
    {
        await new DecomposedTreatmentKeys(_context.AsFactory(), TimeProvider.System)
            .StoreDeletedAsync([], [SpanNamingASuperseder("planted")]);

        var row = await _context.StateSpans.IncludingDeleted().AsNoTracking().SingleAsync(s => s.LegacyId == "planted");
        row.DeletedAt.Should().NotBeNull();
        row.SupersededById.Should().BeNull();
    }
}
