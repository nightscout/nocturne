using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <see cref="DeduplicationService.PruneSingletonLinksAsync"/> and the reads that stand in for what it
/// deletes, against real Postgres under the runtime role and Row Level Security.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SingletonLinkPrunePostgresTests(RlsCompletenessFixture fx)
{
    private static readonly DateTime Event = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Prune_deletes_old_reconciled_singletons_and_keeps_groups()
    {
        var tenant = await SeedTenantAsync();
        await using var provider = BuildProvider();
        await using var context = await ContextAsync(provider, tenant);
        var service = Service(context);

        var singles = new List<Guid>();
        for (var i = 0; i < 3; i++)
            singles.Add(await IngestGlucoseAsync(context, service, Event.AddHours(i), "dexcom-connector", 100 + i));
        var reading = await IngestGlucoseAsync(context, service, Event.AddHours(5), "dexcom-connector", 150);
        var copy = await IngestGlucoseAsync(context, service, Event.AddHours(5).AddSeconds(10), "glooko-connector", 150);
        await AgeAsync(context);

        (await service.PruneSingletonLinksAsync(2, 10)).Should().Be(3);

        var linked = await context.LinkedRecords.Select(l => l.RecordId).ToListAsync();
        linked.Should().BeEquivalentTo([reading, copy]);
    }

    [Fact]
    public async Task A_copy_arriving_after_the_prune_joins_the_pruned_reading()
    {
        var tenant = await SeedTenantAsync();
        await using var provider = BuildProvider();
        await using var context = await ContextAsync(provider, tenant);
        var service = Service(context);

        var reading = await IngestGlucoseAsync(context, service, Event, "dexcom-connector", 120);
        await AgeAsync(context);
        (await service.PruneSingletonLinksAsync(100, 1)).Should().Be(1);

        var copy = await IngestGlucoseAsync(context, service, Event.AddSeconds(10), "glooko-connector", 121);

        var links = await context.LinkedRecords.AsNoTracking().ToListAsync();
        links.Should().HaveCount(2);
        links.Select(l => l.CanonicalId).Distinct().Should().ContainSingle();
        links.Single(l => l.IsPrimary).RecordId.Should().Be(reading);
        links.Single(l => !l.IsPrimary).RecordId.Should().Be(copy);
    }

    [Fact]
    public async Task Prune_waits_for_a_dedup_pass_holding_the_tenant_lock()
    {
        var tenant = await SeedTenantAsync();
        await using var provider = BuildProvider();
        await using var context = await ContextAsync(provider, tenant);
        var service = Service(context);
        await IngestGlucoseAsync(context, service, Event, "dexcom-connector", 120);
        await AgeAsync(context);

        await using var holder = await ContextAsync(provider, tenant);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = holder.ExecuteInTransactionAsync(async ct =>
        {
            await holder.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({DeduplicationService.WriteLockKey(tenant)}, 0))", ct);
            locked.SetResult();
            await release.Task;
            return true;
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var prune = service.PruneSingletonLinksAsync(100, 1);
        await PostgresLockWaits.UntilBlockedAsync(fx.AppConnectionString, prune);
        prune.IsCompleted.Should().BeFalse("the dedup write lock is held");

        release.SetResult();
        await holding.WaitAsync(TimeSpan.FromSeconds(30));
        (await prune.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(1);
    }

    private static DeduplicationService Service(NocturneDbContext context) =>
        new(context.AsFactory(), new Mock<IServiceScopeFactory>().Object, NullLogger<DeduplicationService>.Instance);

    private static async Task<Guid> IngestGlucoseAsync(
        NocturneDbContext context, DeduplicationService service, DateTime at, string source, double mgdl)
    {
        var entity = new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(), TenantId = context.TenantId, Timestamp = at, DataSource = source, Mgdl = mgdl,
        };
        context.SensorGlucose.Add(entity);
        await context.SaveChangesAsync();
        await service.DeduplicateBatchAsync(
            RecordType.SensorGlucose,
            [new DeduplicationInput(
                entity.Id, new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeMilliseconds(), source,
                MatchCriteriaMapper.From(entity))]);
        return entity.Id;
    }

    /// <summary>Stamps the tenant's readings and links two days old and the cursor an hour ago.</summary>
    private static async Task AgeAsync(NocturneDbContext context)
    {
        var old = DateTime.UtcNow.AddDays(-2);
        await context.SensorGlucose.ExecuteUpdateAsync(s => s.SetProperty(e => e.SysCreatedAt, old));
        await context.LinkedRecords.ExecuteUpdateAsync(s => s.SetProperty(l => l.SysCreatedAt, old));
        await DeduplicationService.SetCursorAsync(
            context, new ReconcileCursor(DateTime.UtcNow.AddHours(-1), Guid.Empty), CancellationToken.None);
        context.ChangeTracker.Clear();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(fx.AppConnectionString, configuration: null);
        return services.BuildServiceProvider();
    }

    private static async Task<NocturneDbContext> ContextAsync(ServiceProvider provider, Guid tenantId)
    {
        var context = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        context.TenantId = tenantId;
        return context;
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'singleton-prune-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"prune-{tenantId:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenantId;
    }
}
