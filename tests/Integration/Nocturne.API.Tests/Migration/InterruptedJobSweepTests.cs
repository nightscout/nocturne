using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// The restart sweep settles every job a dead process left in a live state, and only those.
/// </summary>
[Trait("Category", "Integration")]
public class InterruptedJobSweepTests(InterruptedJobSweepPostgresFixture fixture)
    : IClassFixture<InterruptedJobSweepPostgresFixture>
{
    private static readonly Guid TenantId = InterruptedJobSweepPostgresFixture.TenantId;
    private static readonly Guid SourceId = InterruptedJobSweepPostgresFixture.SourceId;
    private readonly MigratedDatabase _database = fixture;

    /// <summary>A live migration run is resumed by its job, so the sweep leaves it live.</summary>
    [Fact]
    public async Task EveryMigrationState_IsLeftAlone()
    {
        var rows = Enum.GetNames<MigrationJobState>()
            .Select(name => (Id: Guid.CreateVersion7(), State: name))
            .ToList();
        await using (var conn = await OpenSuperuserAsync())
        {
            foreach (var (id, state) in rows)
            {
                await conn.ExecuteAsync($"""
                    INSERT INTO migration_runs (id, source_id, tenant_id, mode, created_at, started_at, state, entries_migrated, treatments_migrated, revision)
                    VALUES ('{id}', '{SourceId}', '{TenantId}', 'Api', now(), now(), '{state}', 0, 0, 0);
                    """);
            }
        }

        await Sweep();

        await using var read = await OpenSuperuserAsync();
        foreach (var (id, state) in rows)
        {
            var after = await read.ScalarAsync<string?>($"SELECT state FROM migration_runs WHERE id = '{id}'");
            after.Should().Be(state, $"a {state} run");
        }
    }

    [Fact]
    public async Task EveryLiveConnectorResetState_IsMarkedInterrupted_AndEveryOtherStateIsLeftAlone()
    {
        var rows = Enum.GetNames<ConnectorResetJobState>()
            .Select(name => (Id: Guid.CreateVersion7(), State: name))
            .ToList();
        await using (var conn = await OpenSuperuserAsync())
        {
            foreach (var (id, state) in rows)
            {
                await conn.ExecuteAsync($"""
                    INSERT INTO connector_reset_jobs (id, tenant_id, tenant_slug, state, created_at, total_connectors, completed_connectors)
                    VALUES ('{id}', '{TenantId}', 'sweep', '{state}', now(), 0, 0);
                    """);
            }
        }

        await Sweep();

        await using var read = await OpenSuperuserAsync();
        foreach (var (id, state) in rows)
        {
            var after = await read.ScalarAsync<string?>($"SELECT state FROM connector_reset_jobs WHERE id = '{id}'");
            var wasLive = Enum.Parse<ConnectorResetJobState>(state).IsLive();
            after.Should().Be(wasLive ? nameof(ConnectorResetJobState.Interrupted) : state, $"a {state} job");
        }
    }

    private Task Sweep() =>
        DatabaseInitializationExtensions.MarkInterruptedJobsAsync(
            _database.MigratorConnectionString,
            BackgroundJobLiveStates.ByTable,
            NullLogger.Instance);

    private Task<NpgsqlConnection> OpenSuperuserAsync() => OpenAsync(_database);

    internal static async Task<NpgsqlConnection> OpenAsync(MigratedDatabase database)
    {
        var conn = new NpgsqlConnection(database.SuperuserConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

public sealed class InterruptedJobSweepPostgresFixture() : MigratedDatabase("interrupted_sweep")
{
    internal static readonly Guid TenantId = Guid.Parse("44444444-4444-7444-8444-444444444444");
    internal static readonly Guid SourceId = Guid.Parse("55555555-5555-7555-8555-555555555555");

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var conn = await InterruptedJobSweepTests.OpenAsync(this);
        await conn.SeedTenantAsync(TenantId, "sweep", "Sweep");
        await conn.ExecuteAsync($"""
            INSERT INTO migration_sources (id, tenant_id, mode, source_identifier, created_at)
            VALUES ('{SourceId}', '{TenantId}', 'Api', 'https://source.example', now());
            """);
    }
}
