using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models.Health;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="GoogleHealthWorkRepository"/> on PostgreSQL under the runtime role and Row Level
/// Security: concurrent writes of the two parts, which wait on each other's upsert rather than fail or
/// overwrite each other, and the tenant isolation of the row.
/// </summary>
[Trait("Category", "Integration")]
public class GoogleHealthWorkRepositoryPostgresTests(GoogleHealthWorkRepositoryPostgresTests.Database database)
    : IClassFixture<GoogleHealthWorkRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("google_health_work_repo");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private GoogleHealthWorkRepository Work(Guid tenant) => new(new SuppliedContextFactory(() => Context(tenant)));

    private async Task<Guid> SeedTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES (@id, @slug, 'google-health-work-test', true, now(), now())
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("@slug", $"ghwork-{tenant:N}"));
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    [Fact]
    public async Task Concurrent_first_writes_of_both_parts_land_in_one_row()
    {
        var tenant = await SeedTenantAsync();
        var progress = new GoogleHealthSyncProgress(GoogleHealthSyncPhase.Queued, null, 0, 4, 0, WorkerOwned: true);
        var disconnect = new GoogleHealthDisconnectRequest(Guid.NewGuid(), Guid.NewGuid());

        await Task.WhenAll(
            Work(tenant).SetProgressAsync(progress, CancellationToken.None),
            Work(tenant).SetDisconnectAsync(disconnect, CancellationToken.None));

        (await Work(tenant).GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(progress, disconnect));
        await using var db = Context(tenant);
        (await db.GoogleHealthWork.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Interleaved_progress_and_disconnect_writes_keep_each_part_its_own_last_value()
    {
        var tenant = await SeedTenantAsync();
        var progresses = Enumerable.Range(1, 20)
            .Select(pages => new GoogleHealthSyncProgress(GoogleHealthSyncPhase.Reading, "heart-rate", 1, 4, pages, WorkerOwned: true))
            .ToList();
        var disconnects = Enumerable.Range(0, 20)
            .Select(_ => new GoogleHealthDisconnectRequest(Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        await Task.WhenAll(
            Task.Run(async () =>
            {
                foreach (var progress in progresses)
                    await Work(tenant).SetProgressAsync(progress, CancellationToken.None);
            }),
            Task.Run(async () =>
            {
                foreach (var disconnect in disconnects)
                    await Work(tenant).SetDisconnectAsync(disconnect, CancellationToken.None);
            }));

        (await Work(tenant).GetAsync(CancellationToken.None))
            .Should().Be(new GoogleHealthWork(progresses[^1], disconnects[^1]));

        await Task.WhenAll(
            Work(tenant).SetProgressAsync(null, CancellationToken.None),
            Work(tenant).SetDisconnectAsync(disconnects[0], CancellationToken.None));

        (await Work(tenant).GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(null, disconnects[0]));
    }

    [Fact]
    public async Task A_tenant_sees_and_changes_only_its_own_row()
    {
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        var progress = new GoogleHealthSyncProgress(GoogleHealthSyncPhase.Reading, "steps", 1, 4, 3);
        await Work(a).SetProgressAsync(progress, CancellationToken.None);

        (await Work(b).GetAsync(CancellationToken.None)).Should().Be(GoogleHealthWork.None);
        await Work(b).SetProgressAsync(null, CancellationToken.None);

        (await Work(a).GetAsync(CancellationToken.None)).Should().Be(new GoogleHealthWork(progress, null));
        (await Work(b).GetAsync(CancellationToken.None)).Should().Be(GoogleHealthWork.None);
    }
}
