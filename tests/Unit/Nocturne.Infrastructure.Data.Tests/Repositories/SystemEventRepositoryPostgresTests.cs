using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="SystemEventRepository"/> upserts on PostgreSQL as the runtime role, where the unique
/// index on <c>(tenant_id, original_id)</c> decides what two overlapping syncs leave behind.
/// </summary>
[Trait("Category", "Integration")]
public class SystemEventRepositoryPostgresTests(SystemEventRepositoryPostgresTests.Database database)
    : IClassFixture<SystemEventRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var created = await SharedPostgres.CreateMigratedDatabaseAsync("system_event_repo");
            AppConnectionString = created.AppConnectionString;
            MigratorConnectionString = created.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant, params IInterceptor[] interceptors) =>
        TestContexts.Context(database.AppConnectionString, tenant, interceptors);

    /// <summary>Runs <paramref name="competitor"/> once, just before the first save it sees.</summary>
    private sealed class CommitBeforeFirstSave(Func<Task> competitor) : SaveChangesInterceptor
    {
        private int _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                await competitor();

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private async Task<Guid> NewTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'system-events-{tenant:N}', 'system-events', true, now(), now());
            """, conn);
        await cmd.ExecuteNonQueryAsync();
        return tenant;
    }

    private SystemEventRepository Repository(Guid tenant, params IInterceptor[] interceptors) =>
        new(new SuppliedContextFactory(() => Context(tenant, interceptors)));

    private async Task<List<(string? OriginalId, string? Code)>> RowsAsync(Guid tenant)
    {
        await using var db = Context(tenant);
        return (await db.SystemEvents.AsNoTracking().Select(e => new { e.OriginalId, e.Code }).ToListAsync())
            .Select(e => (e.OriginalId, e.Code))
            .ToList();
    }

    private static SystemEvent Event(string? originalId, string code, long mills = 1_000) => new()
    {
        EventType = SystemEventType.Alarm,
        Category = SystemEventCategory.Pump,
        Code = code,
        Mills = mills,
        Source = "connector",
        OriginalId = originalId,
    };

    [Fact]
    public async Task An_upsert_that_loses_the_race_for_a_key_updates_the_winners_row_and_does_not_throw()
    {
        var tenant = await NewTenantAsync();
        var winner = Repository(tenant);
        var loser = Repository(tenant, new CommitBeforeFirstSave(
            () => winner.UpsertSystemEventAsync(Event("evt-1", "first-sync"))));

        var written = await loser.BulkUpsertAsync([Event("evt-1", "second-sync"), Event("evt-2", "other")]);

        written.Should().Be(2);
        var rows = await RowsAsync(tenant);
        rows.Where(r => r.OriginalId == "evt-1").Should().ContainSingle()
            .Which.Code.Should().Be("second-sync");
        rows.Where(r => r.OriginalId == "evt-2").Should().ContainSingle();
    }

    [Fact]
    public async Task Overlapping_upserts_of_one_key_from_many_syncs_leave_one_row()
    {
        var tenant = await NewTenantAsync();

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => Repository(tenant).UpsertSystemEventAsync(Event("evt-1", $"sync-{i}")))));

        (await RowsAsync(tenant)).Should().ContainSingle().Which.OriginalId.Should().Be("evt-1");
    }

    [Fact]
    public async Task One_key_is_one_row_in_each_tenant_that_syncs_it()
    {
        var first = await NewTenantAsync();
        var second = await NewTenantAsync();

        await Repository(first).UpsertSystemEventAsync(Event("evt-1", "first"));
        await Repository(second).UpsertSystemEventAsync(Event("evt-1", "second"));
        await Repository(second).UpsertSystemEventAsync(Event("evt-1", "second-again"));

        (await RowsAsync(first)).Should().ContainSingle().Which.Code.Should().Be("first");
        (await RowsAsync(second)).Should().ContainSingle().Which.Code.Should().Be("second-again");
    }

    [Fact]
    public async Task Events_without_a_key_are_each_stored()
    {
        var tenant = await NewTenantAsync();

        await Repository(tenant).BulkUpsertAsync(
        [
            Event(null, "unkeyed-1"),
            Event(null, "unkeyed-2"),
            Event("", "empty-1"),
            Event("", "empty-2"),
        ]);

        var rows = await RowsAsync(tenant);
        rows.Select(r => r.Code).Should()
            .BeEquivalentTo("unkeyed-1", "unkeyed-2", "empty-1", "empty-2");
        rows.Should().OnlyContain(r => r.OriginalId == null);
    }

    [Fact]
    public async Task A_database_that_holds_a_key_twice_is_refused_by_the_index()
    {
        var tenant = await NewTenantAsync();
        await Repository(tenant).UpsertSystemEventAsync(Event("evt-1", "stored"));

        await using var db = Context(tenant);
        db.SystemEvents.Add(SystemEventMapper.ToEntity(Event("evt-1", "second")));
        var save = () => db.SaveChangesAsync();

        (await save.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }
}
