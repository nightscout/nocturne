using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The point-in-time reads, on PostgreSQL: whether a span that ends exactly at the instant is active at it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ActiveAtReadsTests : IAsyncLifetime
{
    private static readonly DateTime Instant = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly RlsCompletenessFixture _fx;
    private ServiceProvider _provider = null!;
    private Guid _tenant;

    public ActiveAtReadsTests(RlsCompletenessFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        _tenant = await TenantReaderData.SeedTenantAsync(_fx, _provider);
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task A_temp_basal_ending_exactly_at_the_instant_is_not_active_at_it()
    {
        await SeedAsync(db => db.TempBasals.Add(new TempBasalEntity
        {
            Id = Guid.CreateVersion7(), Timestamp = Instant.AddHours(-1), EndTimestamp = Instant, Rate = 1.2,
            Origin = "Algorithm", Device = "pump", DataSource = "test",
        }));

        (await WithScopeAsync(s => TempBasals(s).GetActiveAtAsync(Instant))).Should().BeNull();
        (await WithScopeAsync(s => TempBasals(s).GetActiveAtAsync(Instant.AddMilliseconds(-1)))).Should().NotBeNull();
    }

    [Fact]
    public async Task A_state_span_ending_exactly_at_the_instant_is_not_active_at_it()
    {
        await SeedAsync(db => db.StateSpans.Add(new StateSpanEntity
        {
            Id = Guid.CreateVersion7(), Category = "PumpMode", State = "Automatic", Source = "a",
            StartTimestamp = Instant.AddHours(-1), EndTimestamp = Instant,
        }));

        (await WithScopeAsync(s => StateSpans(s).GetActiveAtAsync(StateSpanCategory.PumpMode, null, Instant))).Should().BeNull();
        (await WithScopeAsync(s => StateSpans(s).GetActiveAtAsync(StateSpanCategory.PumpMode, null, Instant.AddMilliseconds(-1)))).Should().NotBeNull();
    }

    [Fact]
    public async Task An_alert_excursion_ending_exactly_at_the_instant_was_open_at_it()
    {
        var rule = new AlertRuleEntity { Id = Guid.CreateVersion7(), Name = "Low" };
        await SeedAsync(db =>
        {
            db.AlertRules.Add(rule);
            db.AlertExcursions.Add(new AlertExcursionEntity
            {
                Id = Guid.CreateVersion7(), AlertRuleId = rule.Id, StartedAt = Instant.AddHours(-1), EndedAt = Instant,
            });
        });

        (await WithScopeAsync(s => Alerts(s).HadExcursionOpenAtAsync(rule.Id, Instant, CancellationToken.None))).Should().BeTrue();
        (await WithScopeAsync(s => Alerts(s).HadExcursionOpenAtAsync(rule.Id, Instant.AddMilliseconds(1), CancellationToken.None))).Should().BeFalse();
    }

    private async Task SeedAsync(Action<NocturneDbContext> add)
    {
        await using var db = await _provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(_tenant);
        add(db);
        await db.SaveChangesAsync();
    }

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> read)
    {
        await using var scope = _provider.CreateAsyncScope();
        TenantReaderData.Resolve(scope.ServiceProvider, _tenant);
        return await read(scope.ServiceProvider);
    }

    private static TempBasalRepository TempBasals(IServiceProvider scope) => new(
        scope.GetRequiredService<INocturneDbContextFactory>(),
        new Mock<IDuplicateLinker>().Object,
        new Mock<IAuditContext>().Object,
        NullLogger<TempBasalRepository>.Instance);

    private static StateSpanRepository StateSpans(IServiceProvider scope) => new(
        scope.GetRequiredService<INocturneDbContextFactory>(),
        new Mock<IDuplicateLinker>().Object,
        new Mock<IAuditContext>().Object,
        NullLogger<StateSpanRepository>.Instance);

    private static AlertRepository Alerts(IServiceProvider scope) => new(scope.GetRequiredService<INocturneDbContextFactory>());
}
