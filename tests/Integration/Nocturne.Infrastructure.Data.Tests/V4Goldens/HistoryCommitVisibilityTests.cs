using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
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
