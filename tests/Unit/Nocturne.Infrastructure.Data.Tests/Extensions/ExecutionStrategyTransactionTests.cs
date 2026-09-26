using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// <see cref="RetryingTransactionExtensions.ExecuteInTransactionAsync{T}"/> under an execution
/// strategy that retries: a retried attempt writes each row once, and a reported success means
/// the write is in the store.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExecutionStrategyTransactionTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0015-000000000001");
    private static readonly Guid SeededRuleId = Guid.Parse("00000000-0000-0000-0015-0000000000aa");

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);

    public ExecutionStrategyTransactionTests()
    {
        using var seed = _db.CreateContext();
        seed.AlertRules.Add(new AlertRuleEntity { Id = SeededRuleId, TenantId = TenantId, Name = "before", ConditionParams = "{}" });
        seed.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private sealed class TransientFault : Exception;

    private sealed class RetryOnTransientFault(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientFault;
    }

    public enum Fault { SaveChanges, BeforeCommit, AfterCommit }

    /// <summary>Fails the first save or commit once, then lets every later one through.</summary>
    private sealed class FirstFault(Fault fault) : IDbTransactionInterceptor, ISaveChangesInterceptor
    {
        private int _remaining = 1;

        private bool Fire(Fault at) => at == fault && Interlocked.Decrement(ref _remaining) == 0;

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Fire(Fault.SaveChanges) ? throw new TransientFault() : ValueTask.FromResult(result);

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            Fire(Fault.BeforeCommit) ? throw new TransientFault() : ValueTask.FromResult(result);

        public Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            Fire(Fault.AfterCommit) ? throw new TransientFault() : Task.CompletedTask;
    }

    private NocturneDbContext RetryingContext(Fault fault) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_db.Connection, o => o.ExecutionStrategy(d => new RetryOnTransientFault(d)))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(new FirstFault(fault))
            .Options) { TenantId = TenantId };

    private static AlertRuleEntity NewRule(string name) =>
        new() { Id = Guid.CreateVersion7(), TenantId = TenantId, Name = name, ConditionParams = "{}" };

    private async Task<List<AlertRuleEntity>> StoredRulesAsync()
    {
        await using var check = _db.CreateContext();
        return await check.AlertRules.AsNoTracking().Where(r => r.Id != SeededRuleId).ToListAsync();
    }

    private async Task<string> SeededNameAsync()
    {
        await using var check = _db.CreateContext();
        return (await check.AlertRules.AsNoTracking().SingleAsync(r => r.Id == SeededRuleId)).Name;
    }

    [Fact]
    public async Task A_save_that_failed_after_staging_its_row_inserts_it_once_on_retry()
    {
        await using var context = RetryingContext(Fault.SaveChanges);

        var inserted = await context.ExecuteInTransactionAsync(async ct =>
        {
            var rule = NewRule("new");
            context.AlertRules.Add(rule);
            await context.SaveChangesAsync(ct);
            return rule;
        });

        (await StoredRulesAsync()).Should().ContainSingle().Which.Id.Should().Be(inserted.Id);
    }

    [Fact]
    public async Task An_update_whose_commit_failed_is_written_by_the_retry()
    {
        await using var context = RetryingContext(Fault.BeforeCommit);

        await context.ExecuteInTransactionAsync(async ct =>
        {
            var rule = await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId, ct);
            rule.Name = "after";
            await context.SaveChangesAsync(ct);
        });

        (await SeededNameAsync()).Should().Be("after", "the call reported success");
    }

    [Fact]
    public async Task An_insert_whose_commit_landed_but_reported_failure_is_not_written_twice()
    {
        await using var context = RetryingContext(Fault.AfterCommit);

        var inserted = await context.ExecuteInTransactionAsync(
            async ct =>
            {
                List<TempBasalEntity> rows =
                [
                    new()
                    {
                        Id = Guid.CreateVersion7(), TenantId = TenantId, Origin = "Algorithm", Rate = 1.5,
                        StartTimestamp = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc),
                    },
                ];
                context.TempBasals.AddRange(rows);
                await context.SaveChangesAsync(ct);
                return rows;
            },
            (attempt, ct) => context.AnyLandedAsync(attempt, ct));

        await using var check = _db.CreateContext();
        (await check.TempBasals.AsNoTracking().ToListAsync())
            .Should().ContainSingle().Which.Id.Should().Be(inserted.Single().Id);
    }

    [Fact]
    public async Task A_retry_leaves_what_the_context_tracked_before_the_call()
    {
        await using var context = RetryingContext(Fault.BeforeCommit);
        var held = await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId);

        await context.ExecuteInTransactionAsync(async ct =>
        {
            context.AlertRules.Add(NewRule("new"));
            await context.SaveChangesAsync(ct);
        });

        context.Entry(held).State.Should().Be(EntityState.Unchanged);
        (await StoredRulesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_write_to_an_entity_tracked_before_the_call_is_written_by_the_retry()
    {
        await using var context = RetryingContext(Fault.BeforeCommit);
        var held = await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId);

        await context.ExecuteInTransactionAsync(async ct =>
        {
            var rule = await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId, ct);
            rule.Name = "after";
            await context.SaveChangesAsync(ct);
        });

        (await SeededNameAsync()).Should().Be("after", "the call reported success");
        context.Entry(held).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task A_pending_insert_the_caller_left_on_the_context_lands_once_across_a_retry()
    {
        await using var context = RetryingContext(Fault.BeforeCommit);
        var pending = NewRule("pending");
        context.AlertRules.Add(pending);

        var added = await context.ExecuteInTransactionAsync(async ct =>
        {
            var rule = NewRule("new");
            context.AlertRules.Add(rule);
            await context.SaveChangesAsync(ct);
            return rule;
        });

        (await StoredRulesAsync()).Select(r => r.Id).Should().BeEquivalentTo([pending.Id, added.Id]);
    }

    [Theory]
    [InlineData(Fault.SaveChanges)]
    [InlineData(Fault.BeforeCommit)]
    public async Task A_pending_edit_the_caller_left_on_the_context_lands_across_a_retry(Fault fault)
    {
        await using var context = RetryingContext(fault);
        var held = await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId);
        held.Name = "pending";

        await context.ExecuteInTransactionAsync(async ct =>
        {
            context.AlertRules.Add(NewRule("new"));
            await context.SaveChangesAsync(ct);
        });

        (await SeededNameAsync()).Should().Be("pending");
        (await StoredRulesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task An_edit_that_reads_before_it_writes_is_applied_once_across_a_retry()
    {
        await using var context = RetryingContext(Fault.BeforeCommit);
        await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId);

        await context.ExecuteInTransactionAsync(async ct =>
        {
            var rule = await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId, ct);
            rule.Name += "!";
            await context.SaveChangesAsync(ct);
        });

        (await SeededNameAsync()).Should().Be("before!");
    }

    [Fact]
    public async Task A_failed_call_leaves_nothing_of_its_work_pending_on_an_entity_tracked_before_it()
    {
        await using var context = _db.CreateContext();
        var held = await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId);

        var act = () => context.ExecuteInTransactionAsync<int>(async ct =>
        {
            held.Name = "from-failed-call";
            await context.SaveChangesAsync(ct);
            throw new InvalidOperationException("work failed");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        context.Entry(held).State.Should().Be(EntityState.Unchanged);
        held.Name.Should().Be("before");
        await context.SaveChangesAsync();
        (await SeededNameAsync()).Should().Be("before");
    }

    [Fact]
    public async Task An_entity_named_to_detach_is_read_fresh_by_the_retry()
    {
        await using var context = RetryingContext(Fault.SaveChanges);
        await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId);
        var reads = new List<string>();

        await context.ExecuteInTransactionAsync(
            async ct =>
            {
                var rule = await context.AlertRules.SingleAsync(r => r.Id == SeededRuleId, ct);
                reads.Add(rule.Name);
                rule.Name = "dirty";
                await context.SaveChangesAsync(ct);
                return rule;
            },
            detachBeforeEachAttempt: entity => entity is AlertRuleEntity);

        reads.Should().Equal("before", "before");
    }

    [Fact]
    public async Task A_failure_before_the_commit_is_retried_without_asking_whether_it_landed()
    {
        await using var context = RetryingContext(Fault.SaveChanges);
        var verifications = 0;

        var inserted = await context.ExecuteInTransactionAsync(
            async ct =>
            {
                var rule = NewRule("new");
                context.AlertRules.Add(rule);
                await context.SaveChangesAsync(ct);
                return rule;
            },
            (_, _) =>
            {
                verifications++;
                return Task.FromResult(true);
            });

        verifications.Should().Be(0);
        (await StoredRulesAsync()).Should().ContainSingle().Which.Id.Should().Be(inserted.Id);
    }

    [Fact]
    public async Task A_call_that_fails_leaves_none_of_its_rows_tracked()
    {
        await using var context = _db.CreateContext();

        var act = () => context.ExecuteInTransactionAsync<int>(async ct =>
        {
            context.AlertRules.Add(NewRule("new"));
            await context.SaveChangesAsync(ct);
            throw new InvalidOperationException("work failed");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        context.ChangeTracker.Entries().Should().BeEmpty();
        await context.SaveChangesAsync();
        (await StoredRulesAsync()).Should().BeEmpty();
    }
}
