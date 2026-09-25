using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// <see cref="AlertRuleRearm"/> clears the hold through the scoped context, whose tenant Row Level
/// Security reads. That context takes the tenant only if one is resolved when the scope first builds it.
/// </summary>
[Trait("Category", "Unit")]
public class AlertRuleRearmWiringTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Rule = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private sealed class SettableTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;
        public void SetTenant(TenantContext context) => Context = context;
    }

    private static ServiceProvider Provider(string database, SettableTenantAccessor accessor)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<NocturneDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddDataServices();
        services.AddAlertRepositories();
        services.AddScoped<ITenantAccessor>(_ => accessor);
        services.AddSingleton<AlertRuleEvaluationGate>();
        services.AddScoped<AlertRuleRearm>();
        return services.BuildServiceProvider();
    }

    private static async Task<NocturneDbContext> TenantContextAsync(ServiceProvider provider)
    {
        var db = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        db.TenantId = Tenant;
        return db;
    }

    private static async Task SeedHoldAsync(ServiceProvider provider)
    {
        await using var seed = await TenantContextAsync(provider);
        seed.AlertTrackerState.Add(new AlertTrackerStateEntity
        {
            AlertRuleId = Rule,
            TenantId = Tenant,
            State = "idle",
            AwaitingRearm = true,
            UpdatedAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync();
    }

    private static async Task<bool> AwaitingRearmAsync(ServiceProvider provider)
    {
        await using var check = await TenantContextAsync(provider);
        return (await check.AlertTrackerState.SingleAsync(s => s.AlertRuleId == Rule)).AwaitingRearm;
    }

    [Fact]
    public async Task The_clear_runs_on_a_context_carrying_the_scope_tenant()
    {
        var accessor = new SettableTenantAccessor();
        await using var provider = Provider(Guid.NewGuid().ToString(), accessor);
        await SeedHoldAsync(provider);

        using var scope = provider.CreateScope();
        accessor.SetTenant(new TenantContext(Tenant, "t", "T", true, IsDemo: false));
        var rearm = scope.ServiceProvider.GetRequiredService<AlertRuleRearm>();
        scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId.Should().Be(Tenant,
            "the tracker repository the clear writes through shares this scoped context");

        await rearm.ClearAsync([Rule], CancellationToken.None);

        (await AwaitingRearmAsync(provider)).Should().BeFalse();
    }

    [Fact]
    public async Task A_scope_whose_context_was_built_before_the_tenant_clears_nothing()
    {
        var accessor = new SettableTenantAccessor();
        await using var provider = Provider(Guid.NewGuid().ToString(), accessor);
        await SeedHoldAsync(provider);

        using var scope = provider.CreateScope();
        var rearm = scope.ServiceProvider.GetRequiredService<AlertRuleRearm>();
        accessor.SetTenant(new TenantContext(Tenant, "t", "T", true, IsDemo: false));

        await rearm.ClearAsync([Rule], CancellationToken.None);

        (await AwaitingRearmAsync(provider)).Should().BeTrue(
            "every caller must set the tenant before its scope first resolves the rearm service");
    }
}
