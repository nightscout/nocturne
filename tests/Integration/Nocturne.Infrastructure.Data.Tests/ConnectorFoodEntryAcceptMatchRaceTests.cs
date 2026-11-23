using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <see cref="ConnectorFoodEntryRepository.AcceptMatchAsync"/> against real Postgres under the runtime
/// role and Row Level Security: each accept runs in a scope of its own, as two requests would.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class ConnectorFoodEntryAcceptMatchRaceTests
{
    private const int Entries = 12;

    private readonly RlsCompletenessFixture _fx;

    public ConnectorFoodEntryAcceptMatchRaceTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task Two_accepts_racing_for_one_entry_and_one_carb_intake_write_one_row()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var tenant = await SeedTenantAsync(provider);
        var carbIntake = await SeedCarbIntakeAsync(provider, tenant);
        var entries = await SeedEntriesAsync(provider, tenant);

        foreach (var entry in entries)
        {
            var outcomes = await RaceAsync(provider, tenant, entry, carbIntake, carbIntake);

            outcomes.Should().BeEquivalentTo([AcceptMatchOutcome.Linked, AcceptMatchOutcome.AlreadyLinked]);
        }

        (await RowsAsync(provider, tenant, carbIntake)).Should().Be(Entries, "one row per entry, not one per accept");
    }

    [Fact]
    public async Task Two_accepts_racing_for_one_entry_and_two_carb_intakes_link_it_once_and_refuse_the_other()
    {
        await using var provider = TenantReaderData.BuildProvider(_fx.AppConnectionString);
        var tenant = await SeedTenantAsync(provider);
        var first = await SeedCarbIntakeAsync(provider, tenant);
        var second = await SeedCarbIntakeAsync(provider, tenant);
        var entries = await SeedEntriesAsync(provider, tenant);

        foreach (var entry in entries)
        {
            var outcomes = await RaceAsync(provider, tenant, entry, first, second);

            outcomes.Should().BeEquivalentTo([AcceptMatchOutcome.Linked, AcceptMatchOutcome.AlreadyResolved]);
        }

        (await RowsAsync(provider, tenant, first) + await RowsAsync(provider, tenant, second))
            .Should().Be(Entries, "each entry is on exactly one carb intake");
    }

    private static async Task<AcceptMatchOutcome[]> RaceAsync(
        ServiceProvider provider, Guid tenant, Guid entry, Guid carbIntakeA, Guid carbIntakeB)
    {
        await using var scopeA = provider.CreateAsyncScope();
        await using var scopeB = provider.CreateAsyncScope();
        var a = Repository(scopeA.ServiceProvider, tenant);
        var b = Repository(scopeB.ServiceProvider, tenant);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<AcceptMatchOutcome> Accept(ConnectorFoodEntryRepository repository, Guid carbIntake)
        {
            await start.Task;
            return await repository.AcceptMatchAsync(entry, Row(carbIntake));
        }

        var racing = new[] { Task.Run(() => Accept(a, carbIntakeA)), Task.Run(() => Accept(b, carbIntakeB)) };
        start.SetResult();
        return await Task.WhenAll(racing).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static ConnectorFoodEntryRepository Repository(IServiceProvider scope, Guid tenant)
    {
        TenantReaderData.Resolve(scope, tenant);
        return new ConnectorFoodEntryRepository(
            scope.GetRequiredService<INocturneDbContextFactory>(),
            NullLogger<ConnectorFoodEntryRepository>.Instance);
    }

    private static TreatmentFood Row(Guid carbIntakeId) => new()
    {
        Id = Guid.CreateVersion7(),
        CarbIntakeId = carbIntakeId,
        Portions = 1,
        Carbs = 12,
    };

    private async Task<Guid> SeedTenantAsync(ServiceProvider provider)
    {
        var tenant = Guid.NewGuid();
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'accept-match-race', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"accept-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    private static async Task<Guid> SeedCarbIntakeAsync(ServiceProvider provider, Guid tenant)
    {
        var id = Guid.CreateVersion7();
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        db.CarbIntakes.Add(new CarbIntakeEntity { Id = id, Timestamp = DateTime.UtcNow, Carbs = 40 });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<List<Guid>> SeedEntriesAsync(ServiceProvider provider, Guid tenant)
    {
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        var entries = Enumerable.Range(0, Entries)
            .Select(i => new ConnectorFoodEntryEntity
            {
                Id = Guid.CreateVersion7(),
                ConnectorSource = "race-connector",
                ExternalEntryId = $"entry-{i}",
                ExternalFoodId = "food-1",
                ConsumedAt = DateTimeOffset.UtcNow,
                Carbs = 12,
                Servings = 1,
            })
            .ToList();
        db.ConnectorFoodEntries.AddRange(entries);
        await db.SaveChangesAsync();
        return entries.ConvertAll(e => e.Id);
    }

    private static async Task<int> RowsAsync(ServiceProvider provider, Guid tenant, Guid carbIntake)
    {
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        return await db.TreatmentFoods.CountAsync(t => t.CarbIntakeId == carbIntake);
    }
}
