using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.API.Tests.Services.Jobs;

/// <summary>
/// Runs <c>AddJobLedger</c> over rows in the two tables it replaces on a real PostgreSQL, then down
/// and up again.
/// </summary>
public sealed class AddJobLedgerMigrationFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261030103950_AddTenantHandedOverAt";

    internal static readonly Guid Completed = Guid.Parse("a1111111-1111-7111-8111-111111113155");
    internal static readonly Guid Pending = Guid.Parse("b2222222-2222-7222-8222-222222223155");
    internal static readonly DateTime CompletedAt = new(2026, 10, 28, 1, 2, 3, DateTimeKind.Utc);
    internal static readonly DateTime RequestedAt = new(2026, 10, 29, 4, 5, 6, DateTimeKind.Utc);

    private string _migrator = string.Empty;

    internal List<(string Job, Guid Tenant, DateTime? CompletedAt, DateTime? DueAt, int Attempts, Guid Id)> LedgerAfterUp { get; private set; } = [];
    internal List<string> OldTablesAfterUp { get; private set; } = [];
    internal List<(string Backfill, Guid Tenant, DateTime CompletedAt)> CompletionsAfterDown { get; private set; } = [];
    internal List<(Guid Tenant, string Job, DateTime RequestedAt)> PendingAfterDown { get; private set; } = [];
    internal long LedgerRowsAfterSecondUp { get; private set; }

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_job_ledger");
        _migrator = database.MigratorConnectionString;
        await QueryAsync($"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{Completed}', 'completed', 'Completed', true, now(), now()),
              ('{Pending}', 'pending', 'Pending', false, now(), now());
            INSERT INTO backfill_completions (backfill, tenant_id, completed_at)
              VALUES ('aps-snapshot-curves', '{Completed}', '{CompletedAt:O}');
            INSERT INTO pending_tenant_jobs (tenant_id, job, requested_at)
              VALUES ('{Pending}', 'profile-versions-moved', '{RequestedAt:O}');
            """, _ => 0);

        await MigrateToAsync(null);
        LedgerAfterUp = await QueryAsync(
            "SELECT job, tenant_id, last_completed_at, next_due_at, attempts, id FROM job_ledger ORDER BY job",
            r => (r.GetString(0), r.GetGuid(1),
                r.IsDBNull(2) ? (DateTime?)null : r.GetDateTime(2),
                r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3),
                r.GetInt32(4), r.GetGuid(5)));
        OldTablesAfterUp = await QueryAsync(
            "SELECT table_name FROM information_schema.tables WHERE table_name IN ('backfill_completions', 'pending_tenant_jobs')",
            r => r.GetString(0));

        await MigrateToAsync(PriorMigration);
        CompletionsAfterDown = await QueryAsync(
            "SELECT backfill, tenant_id, completed_at FROM backfill_completions",
            r => (r.GetString(0), r.GetGuid(1), r.GetDateTime(2)));
        PendingAfterDown = await QueryAsync(
            "SELECT tenant_id, job, requested_at FROM pending_tenant_jobs",
            r => (r.GetGuid(0), r.GetString(1), r.GetDateTime(2)));

        await MigrateToAsync(null);
        LedgerRowsAfterSecondUp = (await QueryAsync("SELECT count(*) FROM job_ledger", r => r.GetInt64(0))).Single();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? target)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migrator, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
            rows.Add(read(reader));
        return rows;
    }
}

[Trait("Category", "Integration")]
public class AddJobLedgerMigrationTests(AddJobLedgerMigrationFixture fixture) : IClassFixture<AddJobLedgerMigrationFixture>
{
    [Fact]
    public void Up_moves_a_completion_into_a_finished_row_and_a_pending_job_into_a_due_one()
    {
        fixture.LedgerAfterUp.Select(r => (r.Job, r.Tenant, r.CompletedAt, r.DueAt, r.Attempts)).Should().Equal(
            ("aps-snapshot-curves", AddJobLedgerMigrationFixture.Completed, (DateTime?)AddJobLedgerMigrationFixture.CompletedAt, (DateTime?)null, 0),
            ("profile-versions-moved", AddJobLedgerMigrationFixture.Pending, (DateTime?)null, (DateTime?)AddJobLedgerMigrationFixture.RequestedAt, 0));
        fixture.LedgerAfterUp.Should().OnlyContain(r => r.Id.Version == 7);
    }

    [Fact]
    public void Up_drops_the_old_tables() => fixture.OldTablesAfterUp.Should().BeEmpty();

    [Fact]
    public void Down_puts_the_finished_backfill_and_the_pending_job_back()
    {
        fixture.CompletionsAfterDown.Should().Equal(
            [("aps-snapshot-curves", AddJobLedgerMigrationFixture.Completed, AddJobLedgerMigrationFixture.CompletedAt)]);
        fixture.PendingAfterDown.Should().Equal(
            [(AddJobLedgerMigrationFixture.Pending, "profile-versions-moved", AddJobLedgerMigrationFixture.RequestedAt)]);
    }

    [Fact]
    public void Up_after_down_moves_them_again() => fixture.LedgerRowsAfterSecondUp.Should().Be(2);
}
