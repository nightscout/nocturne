using FluentAssertions;
using Nocturne.Infrastructure.Data.Configuration;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// Behavioural assertions that the runtime app connection pool carries a server-side
/// <c>statement_timeout</c>, so a slow or expensive query is cancelled by PostgreSQL itself and
/// cannot pin a backend indefinitely — the hard backstop behind the client CommandTimeout.
///
/// Reuses the RLS completeness fixture purely for its migrated database and the nocturne_app
/// connection string; these tests build their own data source so the production wiring
/// (<see cref="PostgresRuntimeOptions.ApplyStatementTimeout"/>) is what is under test.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class StatementTimeoutTests
{
    private readonly RlsCompletenessFixture _fx;

    public StatementTimeoutTests(RlsCompletenessFixture fx)
    {
        _fx = fx;
    }

    private static NpgsqlDataSource BuildAppDataSource(string connectionString, int statementTimeoutSeconds)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        PostgresRuntimeOptions.ApplyStatementTimeout(
            builder.ConnectionStringBuilder,
            statementTimeoutSeconds);
        return builder.Build();
    }

    [Fact]
    public async Task RuntimePool_CarriesConfiguredStatementTimeout()
    {
        await using var dataSource = BuildAppDataSource(_fx.AppConnectionString, statementTimeoutSeconds: 2);
        await using var conn = await dataSource.OpenConnectionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SHOW statement_timeout";
        var value = (string?)await cmd.ExecuteScalarAsync();

        value.Should().Be("2s", "the runtime pool must carry the configured statement_timeout");
    }

    [Fact]
    public async Task RuntimePool_CancelsQueryExceedingStatementTimeout()
    {
        await using var dataSource = BuildAppDataSource(_fx.AppConnectionString, statementTimeoutSeconds: 1);
        await using var conn = await dataSource.OpenConnectionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_sleep(5)";
        // Keep Npgsql's own command timeout well above the 1s server cap so the cancellation
        // under test comes from PostgreSQL, not the client.
        cmd.CommandTimeout = 30;

        var act = () => cmd.ExecuteScalarAsync();

        var thrown = await act.Should().ThrowAsync<PostgresException>();
        thrown.Which.SqlState.Should().Be(
            "57014",
            "a statement exceeding statement_timeout must be cancelled by PostgreSQL (SQLSTATE 57014)");
    }

    [Fact]
    public async Task NonPositiveTimeout_LeavesServerDefault()
    {
        await using var dataSource = BuildAppDataSource(_fx.AppConnectionString, statementTimeoutSeconds: 0);
        await using var conn = await dataSource.OpenConnectionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SHOW statement_timeout";
        var value = (string?)await cmd.ExecuteScalarAsync();

        value.Should().Be("0", "a non-positive config value must leave the server default (no cap)");
    }
}
