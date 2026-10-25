using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

[Trait("Category", "Integration")]
public class MigrationCeilingViewTests(MigrationCeilingViewTests.Database database)
    : IClassFixture<MigrationCeilingViewTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync() =>
            MigratorConnectionString =
                (await SharedPostgres.CreateMigratedDatabaseAsync("migration_ceiling_no_revert")).MigratorConnectionString;

        public Task DisposeAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task A_ceiling_below_the_applied_migrations_reverts_nothing()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(database.MigratorConnectionString).Build();
        var all = await AppliedAsync(dataSource);

        using (var context = DatabaseInitializationExtensions.CreateMigratorContext(
            dataSource, new TenantConnectionInterceptor(), NullLogger.Instance, ceiling: all[1]))
        {
            context.Database.GetMigrations().Should().Equal(all[0], all[1]);
            await context.Database.MigrateAsync();
        }

        (await AppliedAsync(dataSource)).Should().Equal(all,
            "another instance can apply past this one's ceiling between two of its calls");
    }

    private static async Task<List<string>> AppliedAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"");
        await using var reader = await command.ExecuteReaderAsync();
        var applied = new List<string>();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }
}
