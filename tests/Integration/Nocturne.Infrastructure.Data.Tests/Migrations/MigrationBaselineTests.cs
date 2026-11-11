using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// The first migration is a baseline that keeps the id of the last migration it replaces. A
/// database whose history stops short of that id would have EF run the baseline over its existing
/// schema, so startup refuses it before applying anything.
/// </summary>
[Trait("Category", "Integration")]
public class MigrationBaselineTests(MigrationBaselineTests.Databases databases)
    : IClassFixture<MigrationBaselineTests.Databases>
{
    private const string OlderMigration = "20250725065111_InitialCreate";

    private static readonly (string Id, MigrationBaselineAttribute Marker) Baseline = ReadBaseline();

    /// <summary>
    /// One database per test: an unmigrated one whose history names a migration older than the
    /// baseline, one whose history names only a newer one, and a fresh install.
    /// </summary>
    public sealed class Databases : IAsyncLifetime
    {
        public string Older { get; private set; } = string.Empty;
        public string Newer { get; private set; } = string.Empty;
        public string Fresh { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            Older = (await SharedPostgres.CreateEmptyDatabaseAsync("baseline_older")).MigratorConnectionString;
            await RecordHistoryAsync(Older, OlderMigration);
            Newer = (await SharedPostgres.CreateEmptyDatabaseAsync("baseline_newer")).MigratorConnectionString;
            await RecordHistoryAsync(Newer, "29991231000000_FromTheFuture");
            Fresh = (await SharedPostgres.CreateMigratedDatabaseAsync("baseline_fresh")).MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task A_database_migrated_by_an_older_release_is_refused_and_left_untouched()
    {
        var migrate = () => DatabaseInitializationExtensions.RunMigrationsAsync(
            databases.Older, NullLogger.Instance, new TenantConnectionInterceptor());

        await migrate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*older than {Baseline.Marker.Release}*{OlderMigration}*"
                + $"image tag {Baseline.Marker.Release.TrimStart('v')})*");
        (await HistoryAsync(databases.Older)).Should().Equal(OlderMigration);
    }

    [Fact]
    public async Task A_database_migrated_by_a_newer_release_is_refused_as_a_downgrade()
    {
        var migrate = () => DatabaseInitializationExtensions.RunMigrationsAsync(
            databases.Newer, NullLogger.Instance, new TenantConnectionInterceptor());

        await migrate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*newer*");
    }

    [Fact]
    public async Task A_fresh_database_records_the_baseline_under_the_id_it_replaces_and_migrates_again_cleanly()
    {
        (await HistoryAsync(databases.Fresh)).First().Should().Be(Baseline.Id);

        var migrate = () => DatabaseInitializationExtensions.RunMigrationsAsync(
            databases.Fresh, NullLogger.Instance, new TenantConnectionInterceptor());
        await migrate.Should().NotThrowAsync();
    }

    private static (string, MigrationBaselineAttribute) ReadBaseline()
    {
        using var context = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(npgsql => npgsql.UseNocturneMigrations())
            .Options);
        var (id, type) = context.GetService<IMigrationsAssembly>().Migrations
            .MinBy(migration => migration.Key, StringComparer.Ordinal);

        return (id, type.GetCustomAttribute<MigrationBaselineAttribute>()
            ?? throw new InvalidOperationException($"The first migration, {id}, is not marked as a baseline."));
    }

    private static async Task RecordHistoryAsync(string connectionString, string migrationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" character varying(150) PRIMARY KEY,
                "ProductVersion" character varying(32) NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES (@id, '10.0.13');
            """,
            connection);
        command.Parameters.AddWithValue("id", migrationId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var applied = new List<string>();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }
}
