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

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Seeds, repository and read helpers shared by the connector food entry tests that run on real Postgres.
/// </summary>
internal static class ConnectorFoodEntryRaceData
{
    public const string Connector = "race-connector";

    public static async Task<Guid> SeedTenantAsync(RlsCompletenessFixture fx, string name)
    {
        var tenant = Guid.NewGuid();
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, @name, true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{name}-{tenant:N}"));
        cmd.Parameters.Add(new NpgsqlParameter("@name", name));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    public static async Task<Guid> SeedCarbIntakeAsync(ServiceProvider provider, Guid tenant)
    {
        var id = Guid.CreateVersion7();
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        db.CarbIntakes.Add(new CarbIntakeEntity { Id = id, Timestamp = DateTime.UtcNow, Carbs = 40 });
        await db.SaveChangesAsync();
        return id;
    }

    public static async Task<List<Guid>> SeedEntriesAsync(ServiceProvider provider, Guid tenant, int count)
    {
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        var entries = Enumerable.Range(0, count)
            .Select(i => new ConnectorFoodEntryEntity
            {
                Id = Guid.CreateVersion7(),
                ConnectorSource = Connector,
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

    public static ConnectorFoodEntryRepository Repository(IServiceProvider scope, Guid tenant)
    {
        TenantReaderData.Resolve(scope, tenant);
        return new ConnectorFoodEntryRepository(
            scope.GetRequiredService<INocturneDbContextFactory>(),
            NullLogger<ConnectorFoodEntryRepository>.Instance);
    }

    public static TreatmentFood Row(Guid carbIntakeId) => new()
    {
        Id = Guid.CreateVersion7(),
        CarbIntakeId = carbIntakeId,
        Portions = 1,
        Carbs = 12,
    };

    public static async Task<int> RowsAsync(ServiceProvider provider, Guid tenant, Guid carbIntake)
    {
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        return await db.TreatmentFoods.CountAsync(t => t.CarbIntakeId == carbIntake);
    }

    public static async Task<(ConnectorFoodEntryStatus Status, Guid? MatchedCarbIntakeId)> StoredAsync(
        ServiceProvider provider, Guid tenant, Guid entry)
    {
        await using var db = await provider.GetRequiredService<INocturneDbContextFactory>().CreateForTenantAsync(tenant);
        var stored = await db.ConnectorFoodEntries
            .AsNoTracking()
            .Where(e => e.Id == entry)
            .Select(e => new { e.Status, e.MatchedCarbIntakeId })
            .SingleAsync();
        return (stored.Status, stored.MatchedCarbIntakeId);
    }
}
