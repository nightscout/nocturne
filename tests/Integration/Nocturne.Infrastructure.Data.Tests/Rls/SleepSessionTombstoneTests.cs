using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// A re-upload of a soft-deleted sleep session against the migrated schema, where the unique
/// <c>(tenant_id, source, original_id)</c> index counts soft-deleted rows too: a user tombstone keeps
/// the session deleted, and a system-swept one is replaced in place rather than colliding.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SleepSessionTombstoneTests(RlsCompletenessFixture fx)
{
    [Fact]
    public async Task UserTombstone_RefusesTheUpsertAndKeepsTheSessionDeleted()
    {
        await using var conn = await OpenForNewTenantAsync();
        var repository = await SeedTombstoneAsync(conn, "sleep-user-deleted", byUser: true);

        var upsert = () => repository.UpsertSessionAsync(Session("sleep-user-deleted"));

        await upsert.Should().ThrowAsync<RecreationBlockedException>();

        (await repository.CountSessionsAsync()).Should().Be(0);
        (await CountRowsAsync(conn, "sleep-user-deleted")).Should().Be(1);
    }

    [Fact]
    public async Task SystemTombstone_IsReplacedWithoutViolatingTheUniqueIndex()
    {
        await using var conn = await OpenForNewTenantAsync();
        var repository = await SeedTombstoneAsync(conn, "sleep-swept", byUser: false);

        await repository.UpsertSessionAsync(Session("sleep-swept"));

        (await repository.CountSessionsAsync()).Should().Be(1);
        (await CountRowsAsync(conn, "sleep-swept")).Should().Be(1);
    }

    [Fact]
    public async Task Update_OntoAKeyAUserTombstoneHolds_IsRefusedAndChangesNothing()
    {
        await using var conn = await OpenForNewTenantAsync();
        var repository = await SeedTombstoneAsync(conn, "sleep-moved-onto-user-deleted", byUser: true);
        var live = await repository.UpsertSessionAsync(Session("sleep-to-move"));

        var update = () => repository.UpdateSessionAsync(Guid.Parse(live.Id!), Session("sleep-moved-onto-user-deleted"));

        await update.Should().ThrowAsync<RecreationBlockedException>();
        (await CountRowsAsync(conn, "sleep-to-move")).Should().Be(1);
        (await CountRowsAsync(conn, "sleep-moved-onto-user-deleted")).Should().Be(1);
    }

    [Fact]
    public async Task Update_OntoAKeyALiveSessionHolds_IsRefused()
    {
        await using var conn = await OpenForNewTenantAsync();
        var repository = new SleepSessionRepository(new TestTenantDbContextFactory(Context(conn)));
        await repository.UpsertSessionAsync(Session("sleep-live-holder"));
        var live = await repository.UpsertSessionAsync(Session("sleep-to-move"));

        var update = () => repository.UpdateSessionAsync(Guid.Parse(live.Id!), Session("sleep-live-holder"));

        await update.Should().ThrowAsync<RecreationBlockedException>();
        (await repository.CountSessionsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Update_OntoAKeyASystemTombstoneHolds_ReplacesTheTombstone()
    {
        await using var conn = await OpenForNewTenantAsync();
        var repository = await SeedTombstoneAsync(conn, "sleep-moved-onto-swept", byUser: false);
        var live = await repository.UpsertSessionAsync(Session("sleep-to-move"));

        var updated = await repository.UpdateSessionAsync(Guid.Parse(live.Id!), Session("sleep-moved-onto-swept"));

        updated!.Id.Should().Be(live.Id);
        (await repository.CountSessionsAsync()).Should().Be(1);
        (await CountRowsAsync(conn, "sleep-to-move")).Should().Be(0);
        (await CountRowsAsync(conn, "sleep-moved-onto-swept")).Should().Be(1);
    }

    [Fact]
    public async Task Delete_SoftDeletesTheSession()
    {
        await using var conn = await OpenForNewTenantAsync();
        var repository = new SleepSessionRepository(new TestTenantDbContextFactory(Context(conn)));
        var created = await repository.UpsertSessionAsync(Session("sleep-to-delete"));

        (await repository.DeleteSessionAsync(Guid.Parse(created.Id!))).Should().BeTrue();

        (await repository.CountSessionsAsync()).Should().Be(0);
        (await CountRowsAsync(conn, "sleep-to-delete")).Should().Be(1);
    }

    private async Task<SleepSessionRepository> SeedTombstoneAsync(NpgsqlConnection conn, string originalId, bool byUser)
    {
        await using (var ctx = Context(conn))
        {
            var row = new SleepSessionEntity
            {
                Id = Guid.CreateVersion7(), TenantId = ctx.TenantId, OriginalId = originalId, Source = "Fitbit",
                StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
                Type = "Overnight", DetectionMethod = "Auto", DeletedAt = DateTime.UtcNow,
            };
            ctx.SleepSessions.Add(row);
            ctx.Entry(row).Property("DeletedByUser").CurrentValue = byUser;
            await ctx.SaveChangesAsync();
        }

        return new SleepSessionRepository(new TestTenantDbContextFactory(Context(conn)));
    }

    private static SleepSession Session(string originalId) => new()
    {
        StartTime = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Utc),
        EndTime = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc),
        Type = SleepSessionType.Overnight,
        DetectionMethod = SleepDetectionMethod.Auto,
        Source = SleepSource.Fitbit,
        DurationMs = 28_800_000,
        TotalSleepMs = 25_200_000,
        OriginalId = originalId,
    };

    private static async Task<long> CountRowsAsync(NpgsqlConnection conn, string originalId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sleep_sessions WHERE original_id = @id";
        cmd.Parameters.AddWithValue("id", originalId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private Guid _tenant;

    private NocturneDbContext Context(NpgsqlConnection conn) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>().UseNpgsql(conn).Options) { TenantId = _tenant };

    private async Task<NpgsqlConnection> OpenForNewTenantAsync()
    {
        _tenant = Guid.NewGuid();
        await using (var migrator = await fx.OpenMigratorConnectionAsync())
        await using (var cmd = migrator.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'sleep-tombstone-test', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", _tenant);
            cmd.Parameters.AddWithValue("slug", $"sleep-{_tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        var conn = await fx.OpenAppConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", _tenant.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        return conn;
    }
}
