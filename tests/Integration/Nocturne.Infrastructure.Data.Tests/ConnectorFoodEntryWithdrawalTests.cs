using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using static Nocturne.Infrastructure.Data.Tests.ConnectorFoodEntryRaceData;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <see cref="Nocturne.Infrastructure.Data.Repositories.ConnectorFoodEntryRepository.MarkMissingAsDeletedAsync"/>
/// against real Postgres under the runtime role and Row Level Security: it withdraws the pending entries
/// of one connector's window that the connector no longer reports, and no others.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ConnectorFoodEntryWithdrawalTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 9, 30, 0, TimeSpan.Zero);

    private readonly RlsCompletenessFixture _fx;

    public ConnectorFoodEntryWithdrawalTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task Withdraws_only_the_pending_entries_of_the_window_that_the_connector_stopped_reporting()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var tenant = await SeedTenantAsync(_fx, "withdrawal");
        var gone = await SeedAsync(provider, tenant, "gone", ConnectorFoodEntryStatus.Pending);
        var kept = await SeedAsync(provider, tenant, "kept", ConnectorFoodEntryStatus.Pending);
        var matched = await SeedAsync(provider, tenant, "matched", ConnectorFoodEntryStatus.Matched);
        var dismissed = await SeedAsync(provider, tenant, "dismissed", ConnectorFoodEntryStatus.Standalone);
        var outside = await SeedAsync(provider, tenant, "outside", ConnectorFoodEntryStatus.Pending, Now.AddDays(-9));
        var other = await SeedAsync(provider, tenant, "other", ConnectorFoodEntryStatus.Pending, source: "other-connector");
        await using var scope = provider.CreateAsyncScope();

        var withdrawn = await Repository(scope.ServiceProvider, tenant).MarkMissingAsDeletedAsync(
            Connector, Now.AddDays(-1), Now.AddDays(1), new HashSet<string> { "kept" }, Now);

        withdrawn.Should().Equal(gone);
        (await StoredAsync(provider, tenant, gone)).Status.Should().Be(ConnectorFoodEntryStatus.Deleted);
        (await ResolvedAtAsync(provider, tenant, gone)).Should().Be(Now);
        (await StoredAsync(provider, tenant, kept)).Status.Should().Be(ConnectorFoodEntryStatus.Pending);
        (await StoredAsync(provider, tenant, matched)).Status.Should().Be(ConnectorFoodEntryStatus.Matched);
        (await StoredAsync(provider, tenant, dismissed)).Status.Should().Be(ConnectorFoodEntryStatus.Standalone);
        (await StoredAsync(provider, tenant, outside)).Status.Should().Be(ConnectorFoodEntryStatus.Pending);
        (await StoredAsync(provider, tenant, other)).Status.Should().Be(ConnectorFoodEntryStatus.Pending);
    }

    [Fact]
    public async Task Reports_nothing_when_the_connector_still_reports_every_pending_entry()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var tenant = await SeedTenantAsync(_fx, "withdrawal");
        var kept = await SeedAsync(provider, tenant, "kept", ConnectorFoodEntryStatus.Pending);
        await using var scope = provider.CreateAsyncScope();

        var withdrawn = await Repository(scope.ServiceProvider, tenant).MarkMissingAsDeletedAsync(
            Connector, Now.AddDays(-1), Now.AddDays(1), new HashSet<string> { "kept" }, Now);

        withdrawn.Should().BeEmpty();
        (await StoredAsync(provider, tenant, kept)).Status.Should().Be(ConnectorFoodEntryStatus.Pending);
    }

    [Fact]
    public async Task A_commit_that_landed_but_reported_failure_still_reports_every_entry_it_withdrew()
    {
        var tenant = await SeedTenantAsync(_fx, "withdrawal");
        await using var connection = new NpgsqlConnection(_fx.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var guc = new NpgsqlCommand("SELECT set_config('app.current_tenant_id', @t, false)", connection))
        {
            guc.Parameters.AddWithValue("t", tenant.ToString());
            await guc.ExecuteScalarAsync();
        }

        var fault = new FirstTransactionFault(TransactionFault.AfterCommit);
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(connection, o => o.ExecutionStrategy(d => new RetryOnTransientFault(d)))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(fault)
            .Options;
        var gone = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var plain = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var seed = new NocturneDbContext(plain) { TenantId = tenant })
        {
            seed.ConnectorFoodEntries.AddRange(gone.Select((id, i) => new ConnectorFoodEntryEntity
            {
                Id = id,
                ConnectorSource = Connector,
                ExternalEntryId = $"gone-{i}",
                ExternalFoodId = "food-1",
                ConsumedAt = Now,
                Carbs = 12,
                Servings = 1,
            }));
            await seed.SaveChangesAsync();
        }

        await using var retrying = new NocturneDbContext(options) { TenantId = tenant };
        var repository = new ConnectorFoodEntryRepository(
            new TestNocturneDbContextFactory(retrying),
            NullLogger<ConnectorFoodEntryRepository>.Instance);
        var resolvedAt = Now.AddTicks(7);

        var withdrawn = await repository.MarkMissingAsDeletedAsync(
            Connector, Now.AddDays(-1), Now.AddDays(1), new HashSet<string>(), resolvedAt);

        fault.Fired.Should().BeTrue();
        withdrawn.Should().BeEquivalentTo(gone);
        await using var check = new NocturneDbContext(options) { TenantId = tenant };
        var stored = await check.ConnectorFoodEntries.AsNoTracking().Where(e => gone.Contains(e.Id)).ToListAsync();
        stored.Should().OnlyContain(e => e.Status == ConnectorFoodEntryStatus.Deleted);
    }

    private static async Task<Guid> SeedAsync(
        ServiceProvider provider,
        Guid tenant,
        string externalEntryId,
        ConnectorFoodEntryStatus status,
        DateTimeOffset? consumedAt = null,
        string source = Connector)
    {
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        var entry = new ConnectorFoodEntryEntity
        {
            Id = Guid.CreateVersion7(),
            ConnectorSource = source,
            ExternalEntryId = externalEntryId,
            ExternalFoodId = "food-1",
            ConsumedAt = consumedAt ?? Now,
            Carbs = 12,
            Servings = 1,
            Status = status,
        };
        db.ConnectorFoodEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private static async Task<DateTimeOffset?> ResolvedAtAsync(ServiceProvider provider, Guid tenant, Guid entry)
    {
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        return await db.ConnectorFoodEntries
            .AsNoTracking()
            .Where(e => e.Id == entry)
            .Select(e => e.ResolvedAt)
            .SingleAsync();
    }
}
