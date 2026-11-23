using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Services.Deduplication;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A committed glucose write, through the real repository and record-changed event, is evaluated in
/// a scope of its own pinned to the writer's tenant: on PostgreSQL under the runtime role and Row
/// Level Security, it reads that tenant's rules and readings and no other tenant's. A deduplication
/// re-pick of the site change a site-age rule waits on evaluates it against the site change reads now show.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertFactEvaluationPostgresTests(AlertFactEvaluationDatabase database)
    : IClassFixture<AlertFactEvaluationDatabase>, IDisposable
{
    private readonly AlertFactEvaluationStack _stack = new(database);

    public void Dispose() => _stack.Dispose();

    [Fact]
    public async Task AGlucoseWriteIsEvaluatedAgainstItsOwnTenantsRulesAndReadingsOnly()
    {
        var (a, ruleA) = await _stack.SeedTenantAsync("a");
        var (b, ruleB) = await _stack.SeedTenantAsync("b");

        await _stack.WriteReadingAsync(b, 180, TimeSpan.Zero);
        var passB = await _stack.NextPassAsync(b.TenantId);
        await _stack.WriteReadingAsync(a, 52, TimeSpan.FromMinutes(2));
        var passA = await _stack.NextPassAsync(a.TenantId);

        passB.Rules.Select(r => r.Id).Should().Equal(ruleB);
        passB.Context.LatestValue.Should().Be(180m);
        passA.Rules.Select(r => r.Id).Should().Equal([ruleA], "tenant b's rule is invisible to tenant a's evaluation");
        passA.Context.LatestValue.Should().Be(52m, "tenant b's newer reading is invisible to tenant a's evaluation");
    }

    [NativeFact]
    public async Task A_reconcile_that_re_picks_the_newest_site_change_evaluates_the_waiting_site_age_rule()
    {
        var (tenant, rule) = await _stack.SeedTenantAsync(
            "repick", AlertConditionType.SiteAge, AlertFactEvaluationStack.SiteAgeOver72Hours);
        var earlier = _stack.WholeSecondsAgo(TimeSpan.FromDays(2));
        var later = earlier.AddSeconds(20);
        await _stack.SeedSiteChangeAsync(tenant.TenantId, earlier, "mylife-connector", linked: true);
        await _stack.SeedSiteChangeAsync(tenant.TenantId, later, "glooko-connector", linked: true);
        await AgeLinksPastTheReconcileLagAsync(tenant.TenantId);

        await _stack.WriteReadingAsync(tenant, 120, TimeSpan.Zero);
        var waiting = await _stack.NextPassAsync(tenant.TenantId);
        waiting.SiteChangeAt.Should().Be(later);
        RuleTrees.WaitsForNextChange(waiting.Rules.Single()).Should().BeTrue();

        await using (var scope = TenantJobScope.Open(_stack.Provider, tenant, "job:dedup-reconciliation"))
            (await scope.ServiceProvider.GetRequiredService<IDuplicateReconciler>()
                .ReconcileNewLinksAsync(5000, 4, 3, CancellationToken.None)).GroupsMerged.Should().Be(1);
        _stack.PassTheDebounce();

        var moved = await _stack.NextPassAsync(tenant.TenantId);
        moved.Rules.Select(r => r.Id).Should().Equal(rule);
        moved.SiteChangeAt.Should().Be(earlier, "the re-pick hides the later copy");
    }

    /// <summary>
    /// A reconcile reads only links two minutes old by the machine's clock, not the test's
    /// (<see cref="DuplicateReconciler"/>).
    /// </summary>
    private async Task AgeLinksPastTheReconcileLagAsync(Guid tenant)
    {
        await using var db = _stack.Context(tenant);
        await db.LinkedRecords.ExecuteUpdateAsync(s => s.SetProperty(
            l => l.SysCreatedAt, DateTime.UtcNow - TimeSpan.FromMinutes(10)));
    }
}
