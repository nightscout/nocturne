using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Nocturne.Tests.Shared.Logging;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A migration that decides something at runtime reports it with <c>RAISE NOTICE</c> and nothing
/// else: the two that skip work — the redundant-foreign-key drop that cannot take its lock, and
/// the <c>ix_linked_records_type_timestamp</c> drop whose replacement is missing — still complete,
/// so they take their history row and never run again. The notice is the whole record.
/// <para>
/// It is also silent by default at both ends. The server logs at <c>warning</c>, and Npgsql raises
/// a notice as an event on the executing <see cref="NpgsqlConnection"/>, so a subscription placed
/// anywhere else compiles, runs, and captures nothing.
/// </para>
/// </summary>
/// <remarks>
/// Reuses the seedless RLS fixture for a migrator connection string; raises its own notice rather
/// than running a migration.
/// </remarks>
[Collection("RLS completeness")]
[Trait("Category", "Integration")]
public class MigrationNoticeForwardingTests
{
    private const string Message = "notice forwarding is wired";

    private readonly RlsCompletenessFixture _fixture;

    public MigrationNoticeForwardingTests(RlsCompletenessFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ANoticeRaisedOnTheMigratorContext_ReachesTheLogger()
    {
        var logger = new ListLogger();

        await using var dataSource =
            new NpgsqlDataSourceBuilder(_fixture.MigratorConnectionString).Build();

        using var context = DatabaseInitializationExtensions.CreateMigratorContext(
            dataSource,
            new TenantConnectionInterceptor(),
            logger);

        await context.Database.ExecuteSqlRawAsync(
            $"DO $$ BEGIN RAISE NOTICE '{Message}'; END $$;");

        logger.Messages.Should().ContainMatch($"*{Message}*",
            "a migration that skips work says so only by raising a notice, and the skip is "
            + "permanent — so a notice that reaches no log leaves the skip unobservable");
    }

    /// <summary>
    /// EF reopens the connection around every <c>suppressTransaction</c> boundary, and both new
    /// migrations cross several. A subscription that does not survive that would capture the first
    /// notice and lose the rest.
    /// </summary>
    [Fact]
    public async Task NoticesSurviveTheOpenAndCloseAroundEachSuppressedTransaction()
    {
        var logger = new ListLogger();

        await using var dataSource =
            new NpgsqlDataSourceBuilder(_fixture.MigratorConnectionString).Build();

        using var context = DatabaseInitializationExtensions.CreateMigratorContext(
            dataSource,
            new TenantConnectionInterceptor(),
            logger);

        for (var i = 0; i < 3; i++)
        {
            // DO blocks take no bind parameters; both holes are test constants.
            var raise = $"DO $$ BEGIN RAISE NOTICE '{Message} {i}'; END $$;";
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync(raise);
            await context.Database.CloseConnectionAsync();
        }

        logger.Messages.Should().HaveCount(3, "each cycle raises one notice and all three have to arrive");
    }
}
