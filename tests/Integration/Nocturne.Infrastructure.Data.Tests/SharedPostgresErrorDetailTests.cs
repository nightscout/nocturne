using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// A failure on a test database names its statements and relations: a CI deadlock is read from its
/// <c>Process N waits for ...</c> detail, which Npgsql redacts unless the connection asks for it.
/// </summary>
[Trait("Category", "Integration")]
public class SharedPostgresErrorDetailTests(SharedPostgresErrorDetailTests.Database database)
    : IClassFixture<SharedPostgresErrorDetailTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public TestDatabase Created { get; private set; } = null!;

        public async Task InitializeAsync() =>
            Created = await SharedPostgres.CreateEmptyDatabaseAsync("error_detail");

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private const string Detail = "the detail Postgres sends";

    [Fact]
    public async Task EveryConnectionAFixtureHandsOut_KeepsThePostgresErrorDetail()
    {
        var connectionStrings = new Dictionary<string, string>
        {
            ["migrator"] = database.Created.MigratorConnectionString,
            ["app"] = database.Created.AppConnectionString,
            ["superuser"] = database.Created.SuperuserConnectionString,
        };

        foreach (var (role, connectionString) in connectionStrings)
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var raise = new NpgsqlCommand(
                $"DO $$ BEGIN RAISE EXCEPTION 'failed' USING DETAIL = '{Detail}'; END $$", conn);

            var failure = (await raise.Invoking(r => r.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>()).Which;

            failure.Detail.Should().Be(Detail, $"the {role} connection reports what Postgres sent");
        }
    }
}
