using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A tracker transition's excursion write and tracker-state upsert commit together. A failure in
/// the upsert must not leave an excursion row that no tracker state points at.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExcursionTransitionAtomicityTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0003-000000000001");
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0003-0000000000aa");

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    public ExcursionTransitionAtomicityTests()
    {
        using var seed = _db.CreateContext();
        seed.AlertRules.Add(new AlertRuleEntity
        {
            Id = RuleId,
            TenantId = TenantId,
            Name = "low",
            ConditionParams = """{"direction":"below","value":70}""",
        });
        seed.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private sealed class FailingUpsertRepository(NocturneDbContext context) : AlertTrackerRepository(context)
    {
        public override Task UpsertTrackerStateAsync(AlertTrackerState state, CancellationToken ct = default) =>
            throw new InvalidOperationException("upsert failed");
    }

    [Fact]
    public async Task A_failed_upsert_rolls_back_the_excursion_it_opened()
    {
        await using var context = _db.CreateContext();
        var tracker = new ExcursionTracker(
            new FailingUpsertRepository(context),
            new AlertRuleEvaluationGate(),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<ExcursionTracker>.Instance);

        var act = () => tracker.ProcessEvaluationAsync(RuleId, conditionMet: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await using var check = _db.CreateContext();
        (await check.AlertExcursions.CountAsync()).Should().Be(0);
        (await check.AlertTrackerState.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_transition_commits_its_excursion_and_state()
    {
        await using var context = _db.CreateContext();
        var tracker = new ExcursionTracker(
            new AlertTrackerRepository(context),
            new AlertRuleEvaluationGate(),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<ExcursionTracker>.Instance);

        var opened = await tracker.ProcessEvaluationAsync(RuleId, conditionMet: true, CancellationToken.None);

        await using var check = _db.CreateContext();
        var excursion = await check.AlertExcursions.SingleAsync();
        excursion.Id.Should().Be(opened.ExcursionId!.Value);
        (await check.AlertTrackerState.SingleAsync()).ActiveExcursionId.Should().Be(excursion.Id);
    }

    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Another process opens an excursion for the rule after this one read the idle state and
    /// before it got the rule's transition lock.
    /// </summary>
    private sealed class ConcurrentOpenRepository(NocturneDbContext context, Guid excursionId)
        : AlertTrackerRepository(context)
    {
        public override async Task LockRuleAsync(Guid alertRuleId, CancellationToken ct = default)
        {
            context.AlertExcursions.Add(new AlertExcursionEntity
            {
                Id = excursionId, TenantId = TenantId, AlertRuleId = alertRuleId, StartedAt = Now.UtcDateTime,
            });
            var state = await context.AlertTrackerState.SingleAsync(s => s.AlertRuleId == alertRuleId, ct);
            state.State = "active";
            state.ActiveExcursionId = excursionId;
            state.UpdatedAt = Now.UtcDateTime;
            await context.SaveChangesAsync(ct);
        }
    }

    [Fact]
    public async Task A_transition_decided_from_a_state_another_process_changed_writes_nothing()
    {
        var theirs = Guid.Parse("00000000-0000-0000-0003-0000000000e2");
        await using (var seed = _db.CreateContext())
        {
            seed.AlertTrackerState.Add(new AlertTrackerStateEntity
            {
                AlertRuleId = RuleId, TenantId = TenantId, State = "idle", UpdatedAt = Now.UtcDateTime.AddMinutes(-5),
            });
            await seed.SaveChangesAsync();
        }
        await using var context = _db.CreateContext();
        var tracker = new ExcursionTracker(
            new ConcurrentOpenRepository(context, theirs), new AlertRuleEvaluationGate(),
            new FakeTimeProvider(Now), NullLogger<ExcursionTracker>.Instance);

        var transition = await tracker.ProcessEvaluationAsync(RuleId, conditionMet: true, CancellationToken.None);

        transition.Type.Should().Be(ExcursionTransitionType.None, "the other process opened and dispatched it");
        await using var check = _db.CreateContext();
        (await check.AlertExcursions.SingleAsync()).Id.Should().Be(theirs);
        (await check.AlertTrackerState.SingleAsync()).ActiveExcursionId.Should().Be(theirs);
    }

    private sealed class TransientFault : Exception;

    private sealed class RetryOnTransientFault(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientFault;
    }

    /// <summary>Fails the first commit, either before it reaches the store or after it lands.</summary>
    private sealed class FirstCommitFault(bool afterCommit) : DbTransactionInterceptor
    {
        private int _remaining = 1;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (!afterCommit && Interlocked.Decrement(ref _remaining) == 0)
                throw new TransientFault();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (afterCommit && Interlocked.Decrement(ref _remaining) == 0)
                throw new TransientFault();
            return Task.CompletedTask;
        }
    }

    private NocturneDbContext RetryingContext(bool faultAfterCommit) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_db.Connection, o => o.ExecutionStrategy(d => new RetryOnTransientFault(d)))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(new FirstCommitFault(faultAfterCommit))
            .Options) { TenantId = TenantId };

    private static ExcursionTracker TrackerOver(NocturneDbContext context) =>
        new(new AlertTrackerRepository(context), new AlertRuleEvaluationGate(),
            new FakeTimeProvider(Now), NullLogger<ExcursionTracker>.Instance);

    [Fact]
    public async Task A_retried_open_commits_one_excursion_and_the_state_pointing_at_it()
    {
        await using var context = RetryingContext(faultAfterCommit: false);

        var opened = await TrackerOver(context).ProcessEvaluationAsync(RuleId, conditionMet: true, CancellationToken.None);

        opened.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        await using var check = _db.CreateContext();
        var excursion = await check.AlertExcursions.SingleAsync();
        excursion.Id.Should().Be(opened.ExcursionId!.Value);
        var state = await check.AlertTrackerState.SingleAsync();
        state.State.Should().Be("active");
        state.ActiveExcursionId.Should().Be(excursion.Id);
    }

    [Fact]
    public async Task An_open_whose_commit_landed_but_reported_failure_is_not_written_twice()
    {
        await using var context = RetryingContext(faultAfterCommit: true);

        var opened = await TrackerOver(context).ProcessEvaluationAsync(RuleId, conditionMet: true, CancellationToken.None);

        opened.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        await using var check = _db.CreateContext();
        var excursion = await check.AlertExcursions.SingleAsync();
        excursion.Id.Should().Be(opened.ExcursionId!.Value);
        (await check.AlertTrackerState.SingleAsync()).ActiveExcursionId.Should().Be(excursion.Id);
    }

    [Fact]
    public async Task A_retried_close_ends_the_excursion_and_idles_the_state()
    {
        var excursionId = Guid.Parse("00000000-0000-0000-0003-0000000000e1");
        await using (var seed = _db.CreateContext())
        {
            seed.AlertExcursions.Add(new AlertExcursionEntity
            {
                Id = excursionId, TenantId = TenantId, AlertRuleId = RuleId, StartedAt = Now.UtcDateTime.AddHours(-1),
            });
            seed.AlertTrackerState.Add(new AlertTrackerStateEntity
            {
                AlertRuleId = RuleId, TenantId = TenantId, State = "active",
                ActiveExcursionId = excursionId, UpdatedAt = Now.UtcDateTime.AddMinutes(-5),
            });
            await seed.SaveChangesAsync();
        }
        await using var context = RetryingContext(faultAfterCommit: false);

        var closed = await TrackerOver(context).ForceCloseAsync(
            RuleId, ExcursionCloseReason.AutoResolve, CancellationToken.None);

        closed.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        await using var check = _db.CreateContext();
        (await check.AlertExcursions.SingleAsync()).EndedAt.Should().Be(Now.UtcDateTime);
        var state = await check.AlertTrackerState.SingleAsync();
        state.State.Should().Be("idle");
        state.ActiveExcursionId.Should().BeNull();
    }
}
