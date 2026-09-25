using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <see cref="AlertTrackerRepository.LockRuleAsync"/> against real Postgres, under the runtime
/// role and Row Level Security: two contexts stand in for two replicas sharing the database.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class AlertTrackerTransitionLockTests
{
    private readonly RlsCompletenessFixture _fx;

    public AlertTrackerTransitionLockTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task A_rule_lock_excludes_another_transaction_until_the_holder_commits()
    {
        var tenant = Guid.NewGuid();
        await SeedTenantAsync(tenant);
        await using var provider = BuildProvider();
        var ruleId = await SeedRuleAsync(provider, tenant);

        await using var first = await ContextAsync(provider, tenant);
        await using var second = await ContextAsync(provider, tenant);
        var firstRepo = new AlertTrackerRepository(first);
        var secondRepo = new AlertTrackerRepository(second);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = firstRepo.ExecuteInTransactionAsync(async ct =>
        {
            await firstRepo.LockRuleAsync(ruleId, ct);
            await firstRepo.UpsertTrackerStateAsync(State(ruleId, "active"), ct);
            locked.SetResult();
            await release.Task;
            return true;
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var contender = secondRepo.ExecuteInTransactionAsync(async ct =>
        {
            await secondRepo.LockRuleAsync(ruleId, ct);
            return (await secondRepo.GetTrackerStateAsync(ruleId, ct))?.State;
        });
        await Task.Delay(500);
        contender.IsCompleted.Should().BeFalse("the first transaction holds the rule's lock");

        release.SetResult();
        await holder.WaitAsync(TimeSpan.FromSeconds(30));
        (await contender.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be("active",
            "the lock is granted after the holder's commit, so the contender reads what it wrote");
    }

    [Fact]
    public async Task Different_rules_do_not_wait_for_each_other()
    {
        var tenant = Guid.NewGuid();
        await SeedTenantAsync(tenant);
        await using var provider = BuildProvider();
        var ruleA = await SeedRuleAsync(provider, tenant);
        var ruleB = await SeedRuleAsync(provider, tenant);

        await using var first = await ContextAsync(provider, tenant);
        await using var second = await ContextAsync(provider, tenant);
        var firstRepo = new AlertTrackerRepository(first);
        var secondRepo = new AlertTrackerRepository(second);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = firstRepo.ExecuteInTransactionAsync(async ct =>
        {
            await firstRepo.LockRuleAsync(ruleA, ct);
            locked.SetResult();
            await release.Task;
            return true;
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await secondRepo.ExecuteInTransactionAsync(async ct =>
        {
            await secondRepo.LockRuleAsync(ruleB, ct);
            return true;
        }).WaitAsync(TimeSpan.FromSeconds(10));

        release.SetResult();
        await holder;
    }

    [Fact]
    public async Task Locking_outside_a_transaction_is_refused()
    {
        var tenant = Guid.NewGuid();
        await SeedTenantAsync(tenant);
        await using var provider = BuildProvider();
        await using var context = await ContextAsync(provider, tenant);

        var act = () => new AlertTrackerRepository(context).LockRuleAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static AlertTrackerState State(Guid ruleId, string state) => new()
    {
        AlertRuleId = ruleId,
        State = state,
        UpdatedAt = DateTime.UtcNow,
    };

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString, configuration: null);
        return services.BuildServiceProvider();
    }

    private static async Task<NocturneDbContext> ContextAsync(ServiceProvider provider, Guid tenantId)
    {
        var context = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        context.TenantId = tenantId;
        return context;
    }

    private static async Task<Guid> SeedRuleAsync(ServiceProvider provider, Guid tenantId)
    {
        await using var context = await ContextAsync(provider, tenantId);
        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = "transition-lock-test",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = """{"direction":"below","value":70}""",
            ClientConfiguration = "{}",
            Severity = AlertRuleSeverity.Warning,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();
        return rule.Id;
    }

    private async Task SeedTenantAsync(Guid tenantId)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'transition-lock-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"lock-{tenantId:N}"));
        await cmd.ExecuteNonQueryAsync();
    }
}
