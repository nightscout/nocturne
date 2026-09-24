using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
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
}
