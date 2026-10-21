using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// A v3 history client that reads while a bulk delete is still in flight must still receive that
/// delete's tombstones once it commits, though they are stamped before rows other transactions
/// committed in the meantime (#1834).
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class HistoryCommitVisibilityTests(V4GoldenFixture fixture)
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_read_during_a_bulk_delete_does_not_move_the_cursor_past_its_tombstones()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var history = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        var doomed = new List<Guid>();
        for (var i = 0; i < 3; i++)
            doomed.Add((await CreateAsync(scope, T0.AddMinutes(5 * i), "dexcom-connector")).Id);
        var cursor = HistoryCursor(await history.GetModifiedSinceAsync(0, 100), 0);

        await using var blocker = new NpgsqlConnection(fixture.AppConnectionString);
        await blocker.OpenAsync();
        await using var held = await HoldRowAsync(blocker, tenant, doomed[^1]);

        using var deleteScope = scope.ServiceProvider.CreateScope();
        var delete = deleteScope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>()
            .DeleteBySourceAsync("dexcom-connector");
        await PostgresLockWaits.UntilBlockedAsync(fixture.AppConnectionString, delete);

        using var liveScope = scope.ServiceProvider.CreateScope();
        var live = await CreateAsync(liveScope, T0.AddMinutes(20), "xdrip");

        var duringDelete = await history.GetModifiedSinceAsync(cursor, 100);
        cursor = HistoryCursor(duringDelete, cursor);

        await held.CommitAsync();
        (await delete.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(3);

        var afterDelete = await history.GetModifiedSinceAsync(cursor, 100);
        var delivered = duringDelete.Concat(afterDelete).ToList();

        delivered.Where(h => h.Deleted).Select(h => h.Record.Id).Should().BeEquivalentTo(doomed);
        delivered.Where(h => !h.Deleted).Select(h => h.Record.Id).Should().Contain(live.Id);
    }

    [Fact]
    public async Task An_open_transaction_that_has_written_nothing_does_not_hold_history_back()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var history = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

        await using var reader = new NpgsqlConnection(fixture.AppConnectionString);
        await reader.OpenAsync();
        await using var open = await reader.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM sensor_glucose", reader, open))
            await command.ExecuteScalarAsync();

        var written = await CreateAsync(scope, T0, "xdrip");

        (await history.GetModifiedSinceAsync(0, 100)).Select(h => h.Record.Id).Should().Contain(written.Id);
    }

    /// <remarks>
    /// The delete waits on an advisory lock inside the read that precedes its first write, so it
    /// holds no transaction id of its own unless it claimed one with its stamp, and no other session
    /// of the runtime role holds one.
    /// </remarks>
    [Fact]
    public async Task A_delete_held_between_its_stamp_and_its_first_write_holds_history_back()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var history = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var doomed = new List<Guid>();
        for (var i = 0; i < 3; i++)
            doomed.Add((await CreateAsync(scope, T0.AddMinutes(5 * i), "dexcom-connector")).Id);
        var cursor = HistoryCursor(await history.GetModifiedSinceAsync(0, 100), 0);

        await using var held = await HoldAdvisoryLockAsync();
        var delete = StalledDeleteAsync(scope, tenant);
        await PostgresLockWaits.UntilBlockedAsync(fixture.AppConnectionString, delete);

        var live = await CreateAsync(scope, T0.AddMinutes(20), "xdrip");
        var duringDelete = await history.GetModifiedSinceAsync(cursor, 100);
        cursor = HistoryCursor(duringDelete, cursor);

        await held.DisposeAsync();
        (await delete.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(3);

        var delivered = duringDelete.Concat(await history.GetModifiedSinceAsync(cursor, 100)).ToList();
        delivered.Where(h => h.Deleted).Select(h => h.Record.Id).Should().BeEquivalentTo(doomed);
        delivered.Where(h => !h.Deleted).Select(h => h.Record.Id).Should().Contain(live.Id);
    }

    [Fact]
    public async Task A_stalled_delete_holds_back_its_own_tenants_history_and_no_other()
    {
        var other = Guid.NewGuid();
        using (await fixture.BeginTenantScopeAsync(other)) { }
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        await CreateAsync(scope, T0, "dexcom-connector");

        await using var held = await HoldAdvisoryLockAsync();
        var delete = StalledDeleteAsync(scope, tenant);
        await PostgresLockWaits.UntilBlockedAsync(fixture.AppConnectionString, delete);
        DateTime deleteStart, own, others;
        try
        {
            deleteStart = await WaitingTransactionStartAsync();
            own = await HorizonForAsync(scope, tenant);
            others = await HorizonForAsync(scope, other);
        }
        finally
        {
            await held.DisposeAsync();
            await delete.WaitAsync(TimeSpan.FromSeconds(30));
        }

        own.Should().BeOnOrBefore(deleteStart);
        others.Should().BeAfter(deleteStart);
    }

    [Fact]
    public async Task A_runtime_role_writer_is_waited_for_and_a_migrator_writer_is_not()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);

        await using (var app = await OpenWriterAsync(fixture.AppConnectionString))
            (await HorizonForAsync(scope, tenant)).Should().BeOnOrBefore(app.Started);

        await using var migrator = await OpenWriterAsync(fixture.MigratorConnectionString);
        (await HorizonForAsync(scope, tenant)).Should().BeAfter(migrator.Started);
    }

    [Fact]
    public async Task The_commit_margin_keeps_the_horizon_that_far_behind_the_database_clock()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        await using var ctx = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(tenant);

        var horizon = await HistoryHorizon.ReadAsync(ctx, TimeSpan.FromSeconds(2), NullLogger.Instance, CancellationToken.None);
        var clock = await ctx.Database.SqlQueryRaw<DateTime>("SELECT clock_timestamp() AS \"Value\"").SingleAsync();

        horizon.Should().NotBeNull();
        horizon!.Value.Should().BeOnOrBefore(clock.AddSeconds(-2));
    }

    private Task<int> StalledDeleteAsync(IServiceScope scope, Guid tenant) => Task.Run(async () =>
    {
        await using var ctx = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(tenant);
        var rows = ctx.SensorGlucose.FromSqlRaw(
            $"SELECT * FROM sensor_glucose WHERE (SELECT count(*) FROM (SELECT pg_advisory_xact_lock_shared({StallKey})) l) = 1");
        return await ctx.AuditedSoftDeleteAsync(rows.Where(e => e.DataSource == "dexcom-connector"), null, "test");
    });

    private const int StallKey = 1834;

    private async Task<AdvisoryLock> HoldAdvisoryLockAsync()
    {
        var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT pg_advisory_lock({StallKey})", connection);
        await command.ExecuteNonQueryAsync();
        return new AdvisoryLock(connection);
    }

    /// <summary>A session-level lock, released before its connection goes back to the pool.</summary>
    private sealed class AdvisoryLock(NpgsqlConnection connection) : IAsyncDisposable
    {
        private bool _released;

        public async ValueTask DisposeAsync()
        {
            if (_released)
                return;
            _released = true;
            await using (var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({StallKey})", connection))
                await unlock.ExecuteNonQueryAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>The start of the transaction waiting on a lock, then a pause on the database clock past it.</summary>
    private async Task<DateTime> WaitingTransactionStartAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT xact_start FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock'
            """, connection);
        var start = (DateTime)(await command.ExecuteScalarAsync())!;
        await using var pause = new NpgsqlCommand("SELECT pg_sleep(0.005)", connection);
        await pause.ExecuteNonQueryAsync();
        return start;
    }

    private static async Task<DateTime> HorizonForAsync(IServiceScope scope, Guid tenant)
    {
        await using var ctx = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>()
            .CreateForTenantAsync(tenant);
        return (await HistoryHorizon.ReadAsync(ctx, TimeSpan.Zero, NullLogger.Instance, CancellationToken.None))!.Value;
    }

    private sealed class OpenWriter(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime started) : IAsyncDisposable
    {
        public DateTime Started { get; } = started;

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>A transaction holding an id, then a pause on the database clock past its start.</summary>
    private static async Task<OpenWriter> OpenWriterAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT pg_current_xact_id(), transaction_timestamp(); SELECT pg_sleep(0.005);", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var started = reader.GetFieldValue<DateTime>(1);
        await reader.DisposeAsync();
        return new OpenWriter(connection, transaction, started);
    }

    private static Task<SensorGlucose> CreateAsync(IServiceScope scope, DateTime at, string source) =>
        scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>().CreateAsync(
            new SensorGlucose { Timestamp = at, Mgdl = 120, DataSource = source, Device = source },
            WriteOrigin.Backfill);

    /// <summary>The cursor a v3 client keeps: the newest <c>srvModified</c> it was sent.</summary>
    private static long HistoryCursor(IReadOnlyList<HistoryRecord<SensorGlucose>> page, long cursor) =>
        page.Count == 0 ? cursor : page.Max(h => HistoryPage.ToMilliseconds(h.Record.ModifiedAt));

    /// <summary>Opens a transaction holding a row lock on <paramref name="id"/>, which stalls a delete of it.</summary>
    private static async Task<NpgsqlTransaction> HoldRowAsync(NpgsqlConnection connection, Guid tenant, Guid id)
    {
        var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT set_config('app.current_tenant_id', @tenant, true);
            SELECT 1 FROM sensor_glucose WHERE id = @id FOR UPDATE;
            """;
        command.Parameters.AddWithValue("tenant", tenant.ToString());
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
        return transaction;
    }
}
