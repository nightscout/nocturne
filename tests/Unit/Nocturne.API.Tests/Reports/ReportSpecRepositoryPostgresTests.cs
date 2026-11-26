using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Reports;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// A custom report update that sends the <c>updatedAt</c> it read, on PostgreSQL under the runtime role: it writes only
/// while the stored value is that one, and of two writes that read the same value one wins.
/// </summary>
[Trait("Category", "Integration")]
public class ReportSpecRepositoryPostgresTests(ReportSpecRepositoryPostgresTests.Database database)
    : IClassFixture<ReportSpecRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var db = await SharedPostgres.CreateMigratedDatabaseAsync("report_spec_concurrency");
            AppConnectionString = db.AppConnectionString;
            MigratorConnectionString = db.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;

        // Retrying, as the API's contexts are: that strategy refuses a transaction begun outside it.
        public DbContextOptions<NocturneDbContext> Options =>
            TestContexts.Options(AppConnectionString, npgsql: TestContexts.Retrying);

        public async Task<ReportSpecRepository> TenantAsync()
        {
            var tenant = Guid.NewGuid();
            await using (var conn = new NpgsqlConnection(MigratorConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                    VALUES (@id, @slug, 'report-spec-test', true, now(), now())
                    """;
                cmd.Parameters.AddWithValue("id", tenant);
                cmd.Parameters.AddWithValue("slug", $"report-spec-{tenant:N}");
                await cmd.ExecuteNonQueryAsync();
            }
            return new ReportSpecRepository(new NocturneDbContext(Options) { TenantId = tenant }.AsFactory());
        }
    }

    private const string Spec = """{"version":1,"panels":[]}""";

    [Fact]
    public async Task The_updated_at_a_save_returns_is_the_one_a_later_update_may_send()
    {
        var repository = await database.TenantAsync();
        var created = (await repository.CreateAsync("Mine", Spec, 1, null)).Report!;

        var renamed = await repository.UpdateAsync(created.Id, "Renamed", null, null, created.UpdatedAt);
        var again = await repository.UpdateAsync(created.Id, "Again", null, null, renamed.Report!.UpdatedAt);

        renamed.Status.Should().Be(SavedReportSpecWriteStatus.Saved);
        again.Status.Should().Be(SavedReportSpecWriteStatus.Saved);
        (await repository.GetAsync(created.Id))!.UpdatedAt.Should().Be(again.Report!.UpdatedAt, "what a save returns is what a read returns");
    }

    [Fact]
    public async Task A_stale_updated_at_writes_nothing_and_an_absent_one_writes()
    {
        var repository = await database.TenantAsync();
        var created = (await repository.CreateAsync("Mine", Spec, 1, null)).Report!;
        await repository.UpdateAsync(created.Id, "Theirs", null, null, created.UpdatedAt);

        var stale = await repository.UpdateAsync(created.Id, "Mine again", Spec, 1, created.UpdatedAt);
        (await repository.GetAsync(created.Id))!.Name.Should().Be("Theirs", "the stale write changed nothing");
        var blind = await repository.UpdateAsync(created.Id, "Mine again", null, null);

        stale.Status.Should().Be(SavedReportSpecWriteStatus.Changed);
        stale.Report.Should().BeNull();
        blind.Status.Should().Be(SavedReportSpecWriteStatus.Saved);
        (await repository.GetAsync(created.Id))!.Name.Should().Be("Mine again");
    }

    [Fact]
    public async Task Of_two_writes_that_read_the_same_updated_at_one_wins()
    {
        var repository = await database.TenantAsync();
        var created = (await repository.CreateAsync("Mine", Spec, 1, null)).Report!;

        var writes = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            Task.Run(() => repository.UpdateAsync(created.Id, $"Writer {i}", null, null, created.UpdatedAt))));

        writes.Count(w => w.Status == SavedReportSpecWriteStatus.Saved).Should().Be(1);
        writes.Count(w => w.Status == SavedReportSpecWriteStatus.Changed).Should().Be(3);
    }
}
