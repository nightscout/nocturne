using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// A batch is written against the unique index on tenant, connector source and external entry id,
/// which only PostgreSQL enforces as production does.
/// </summary>
[Trait("Category", "Integration")]
public class ConnectorFoodEntryBatchDedupeTests(ConnectorFoodEntryBatchDedupeTests.Database database)
    : IClassFixture<ConnectorFoodEntryBatchDedupeTests.Database>
{
    private const string Source = "glooko-connector";

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("connector_food_batch_dedupe");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static ConnectorFoodEntryImport Import(string entryId, decimal carbs, string source = Source) => new()
    {
        ConnectorSource = source,
        ExternalEntryId = entryId,
        ConsumedAt = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
        MealName = "breakfast",
        Carbs = carbs,
    };

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant, npgsql: TestContexts.Retrying);

    private ConnectorFoodEntryRepository Repository(NocturneDbContext context) =>
        new(context.AsFactory(), NullLogger<ConnectorFoodEntryRepository>.Instance);

    [Fact]
    public async Task Two_imports_sharing_a_key_save_one_row_and_resolve_to_it()
    {
        var tenant = await SeedTenantAsync();
        await using var context = Context(tenant);

        var upsert = await Repository(context).UpsertAsync([Import("glooko-1", 10), Import("glooko-1", 25)]);

        var stored = await context.ConnectorFoodEntries.AsNoTracking().ToListAsync();
        stored.Should().ContainSingle();
        upsert.Entries.Should().ContainSingle().Which.Id.Should().Be(stored[0].Id);
        upsert.EntriesToMatch.Should().Equal(stored[0].Id);
    }

    [Fact]
    public async Task The_first_of_two_imports_sharing_a_key_supplies_the_values()
    {
        var tenant = await SeedTenantAsync();
        await using var context = Context(tenant);

        await Repository(context).UpsertAsync([Import("glooko-1", 10), Import("glooko-1", 25)]);

        (await context.ConnectorFoodEntries.AsNoTracking().SingleAsync()).Carbs.Should().Be(10);
    }

    [Fact]
    public async Task Keys_that_differ_only_by_surrounding_whitespace_are_one_key()
    {
        var tenant = await SeedTenantAsync();
        await using var context = Context(tenant);

        var upsert = await Repository(context).UpsertAsync([Import("glooko-1", 10), Import("  glooko-1 ", 25)]);

        var stored = await context.ConnectorFoodEntries.AsNoTracking().SingleAsync();
        stored.ExternalEntryId.Should().Be("glooko-1");
        upsert.Entries.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
    }

    [Fact]
    public async Task A_repeated_key_does_not_swallow_the_other_entries_of_the_batch()
    {
        var tenant = await SeedTenantAsync();
        await using var context = Context(tenant);

        var upsert = await Repository(context).UpsertAsync(
            [Import("glooko-1", 10), Import("glooko-2", 20), Import("glooko-1", 25), Import("glooko-3", 30)]);

        upsert.Entries.Select(e => e.ExternalEntryId).Should().Equal("glooko-1", "glooko-2", "glooko-3");
        (await context.ConnectorFoodEntries.AsNoTracking().CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task One_key_from_two_connector_sources_is_two_rows()
    {
        var tenant = await SeedTenantAsync();
        await using var context = Context(tenant);

        var upsert = await Repository(context).UpsertAsync(
            [Import("shared-1", 10), Import("shared-1", 25, source: "myfitnesspal-connector")]);

        upsert.Entries.Should().HaveCount(2);
        (await context.ConnectorFoodEntries.AsNoTracking().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_key_already_stored_is_updated_once_by_the_first_import_that_names_it()
    {
        var tenant = await SeedTenantAsync();
        await using var context = Context(tenant);
        var repository = Repository(context);
        await repository.UpsertAsync([Import("glooko-1", 5)]);

        var upsert = await repository.UpsertAsync([Import("glooko-1", 10), Import("glooko-1", 25)]);

        var stored = await context.ConnectorFoodEntries.AsNoTracking().SingleAsync();
        stored.Carbs.Should().Be(10);
        upsert.Entries.Should().ContainSingle().Which.Id.Should().Be(stored.Id);
    }

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'connector-food-batch-dedupe-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"food-batch-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }
}
