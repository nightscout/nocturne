using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RetireInvalidUploadedTreatmentRows</c> on a real PostgreSQL as the migrator, over tenants
/// that each hold one kind of live record that kept <c>isValid: false</c>, one that holds only records
/// the job must leave, and one with no records; reads, as each tenant, the legacy ids the job deletes
/// (<see cref="DecomposedTreatmentKeys.GetLiveInvalidLegacyIdsAsync"/>); then runs its SQL again, and
/// takes it down. The migration and the job each match the records in their own SQL, so one set of
/// rows holds both to the same records.
/// </summary>
public class RetireInvalidUploadedTreatmentRowsFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261124161251_MaskNightscoutSourceUrls";
    private const string Migration = "20261124222813_RetireInvalidUploadedTreatmentRows";

    internal static readonly Guid TempBasals = Tenant(1);
    internal static readonly Guid Activations = Tenant(2);
    internal static readonly Guid Overrides = Tenant(3);
    internal static readonly Guid Targets = Tenant(4);
    internal static readonly Guid Untouched = Tenant(5);
    internal static readonly Guid Empty = Tenant(6);

    private static readonly Guid[] Tenants = [TempBasals, Activations, Overrides, Targets, Untouched, Empty];

    private const string False = """{"isValid":false}""";
    private const string True = """{"isValid":true}""";

    private string _migrator = string.Empty;
    private string _app = string.Empty;

    internal Dictionary<Guid, IReadOnlyList<string>> JobFinds { get; } = [];

    internal List<(Guid Tenant, bool Due, int Attempts)> LedgerAfterUp { get; private set; } = [];
    internal List<(Guid Tenant, bool Due, int Attempts)> LedgerAfterSecondRun { get; private set; } = [];
    internal List<(Guid Tenant, bool Due, int Attempts)> LedgerAfterDown { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "retire_invalid_treatments");
        _migrator = database.MigratorConnectionString;
        _app = database.AppConnectionString;

        foreach (var tenant in Tenants)
            await NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES ('{tenant}', 'ri-{tenant:N}', 'ri', true, now(), now());
                """);

        await TempBasalAsync(TempBasals, "tb-false", False);
        await TempBasalAsync(TempBasals, "tb-false-2", False);
        await TempBasalAsync(TempBasals, "tb-true", True);
        await ActivationAsync(Activations, "pa-false", False);
        await SpanAsync(Overrides, "Override", "ov-false", False);
        await SpanAsync(Targets, "TemporaryTarget", "tt-false", False);

        await TempBasalAsync(Untouched, "u-true", True);
        await TempBasalAsync(Untouched, "u-absent", null);
        await TempBasalAsync(Untouched, "u-string", """{"isValid":"false"}""");
        await TempBasalAsync(Untouched, "u-user-deleted", False, deleted: true);
        await TempBasalAsync(Untouched, null, False);
        await ActivationAsync(Untouched, "u-pa-true", True);
        await ActivationAsync(Untouched, "u-pa-deleted", False, deleted: true);
        await SpanAsync(Untouched, "PumpMode", "u-pump-mode", False);
        await SpanAsync(Untouched, "Override", "u-ov-deleted", False, deleted: true);
        await SpanAsync(Untouched, "TemporaryTarget", "u-tt-true", True);
        await SpanAsync(Untouched, "TemporaryTarget", "u-tt-string", """{"isValid":"false"}""");
        await SpanAsync(Untouched, "Override", "u-ov-true", True);
        await SpanAsync(Untouched, "Override", "u-ov-absent", null);
        await SpanAsync(Untouched, "Motion", "u-motion", False);
        await SpanAsync(Untouched, "Override", null, False);
        await ActivationAsync(Untouched, "u-pa-absent", null);
        await ActivationAsync(Untouched, "u-pa-string", """{"isValid":"false"}""");
        await TempBasalAsync(Untouched, "tb-false", True);

        await MigrateToAsync(Migration);
        LedgerAfterUp = await LedgerAsync();

        foreach (var tenant in Tenants)
        {
            await using var db = new NocturneDbContext(Options(_app)) { TenantId = tenant };
            JobFinds[tenant] = await new DecomposedTreatmentKeys(db.AsFactory(), TimeProvider.System)
                .GetLiveInvalidLegacyIdsAsync(CancellationToken.None);
        }

        await NpgsqlTestCommands.ExecuteAsync(_migrator, RetireInvalidUploadedTreatmentRows.RequestSql);
        LedgerAfterSecondRun = await LedgerAsync();

        await MigrateToAsync(PriorMigration);
        LedgerAfterDown = await LedgerAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task TempBasalAsync(Guid tenant, string? legacyId, string? kept, bool deleted = false) =>
        NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, origin, legacy_id, additional_properties, deleted_at, deleted_by_user)
            VALUES ('{Guid.CreateVersion7()}', '{tenant}', now(), 'Algorithm', {Text(legacyId)}, {Json(kept)},
                    {(deleted ? "now()" : "NULL")}, {deleted});
            """);

    private Task ActivationAsync(Guid tenant, string legacyId, string? kept, bool deleted = false) =>
        NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            INSERT INTO profile_activations
                (id, tenant_id, start_timestamp, phase, origin, base_profile_name, percentage, timeshift_ms,
                 snapshot_is_customized, legacy_id, additional_properties, deleted_at, deleted_by_user)
            VALUES ('{Guid.CreateVersion7()}', '{tenant}', now(), 'Requested', 'Manual', 'Default', 100, 0,
                    false, '{legacyId}', {Json(kept)}, {(deleted ? "now()" : "NULL")}, {deleted});
            """);

    private Task SpanAsync(Guid tenant, string category, string? legacyId, string? kept, bool deleted = false) =>
        NpgsqlTestCommands.ExecuteAsync(_migrator, $"""
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            INSERT INTO state_spans (id, tenant_id, category, state, start_timestamp, original_id, metadata, deleted_at, deleted_by_user)
            VALUES ('{Guid.CreateVersion7()}', '{tenant}', '{category}', 'Custom', now(), {Text(legacyId)}, {Json(kept)},
                    {(deleted ? "now()" : "NULL")}, {deleted});
            """);

    private static string Text(string? value) => value is null ? "NULL" : $"'{value}'";

    private static string Json(string? json) => json is null ? "NULL" : $"'{json}'::jsonb";

    private static Guid Tenant(int n) => Guid.Parse($"c{n}{n}{n}{n}{n}{n}{n}-{n}{n}{n}{n}-7{n}{n}{n}-8{n}{n}{n}-{n}{n}{n}{n}{n}{n}{n}{n}4121");

    private async Task MigrateToAsync(string target)
    {
        await using var context = new NocturneDbContext(Options(_migrator));
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private static DbContextOptions<NocturneDbContext> Options(string connectionString) =>
        TestContexts.MigrationsOptions(connectionString);

    private async Task<List<(Guid Tenant, bool Due, int Attempts)>> LedgerAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT tenant_id, next_due_at IS NOT NULL, attempts FROM job_ledger WHERE job = '{RetireInvalidUploadedTreatmentRows.JobName}' ORDER BY tenant_id",
            conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid, bool, int)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetGuid(0), reader.GetBoolean(1), reader.GetInt32(2)));
        return rows;
    }
}

[Trait("Category", "Integration")]
public class RetireInvalidUploadedTreatmentRowsTests(RetireInvalidUploadedTreatmentRowsFixture fixture)
    : IClassFixture<RetireInvalidUploadedTreatmentRowsFixture>
{
    private static readonly (Guid Tenant, bool Due, int Attempts)[] Affected =
    [
        .. new[]
            {
                RetireInvalidUploadedTreatmentRowsFixture.TempBasals,
                RetireInvalidUploadedTreatmentRowsFixture.Activations,
                RetireInvalidUploadedTreatmentRowsFixture.Overrides,
                RetireInvalidUploadedTreatmentRowsFixture.Targets,
            }
            .Order()
            .Select(tenant => (tenant, true, 0)),
    ];

    [Fact]
    public void Up_requests_the_job_once_for_each_tenant_holding_a_live_record_that_kept_isValid_false() =>
        fixture.LedgerAfterUp.Should().Equal(Affected);

    [Fact]
    public void The_job_finds_exactly_the_live_records_that_kept_the_boolean_false_in_its_tenant()
    {
        fixture.JobFinds.Should().BeEquivalentTo(new Dictionary<Guid, IReadOnlyList<string>>
        {
            [RetireInvalidUploadedTreatmentRowsFixture.TempBasals] = ["tb-false", "tb-false-2"],
            [RetireInvalidUploadedTreatmentRowsFixture.Activations] = ["pa-false"],
            [RetireInvalidUploadedTreatmentRowsFixture.Overrides] = ["ov-false"],
            [RetireInvalidUploadedTreatmentRowsFixture.Targets] = ["tt-false"],
            [RetireInvalidUploadedTreatmentRowsFixture.Untouched] = [],
            [RetireInvalidUploadedTreatmentRowsFixture.Empty] = [],
        });
    }

    [Fact]
    public void The_migration_requests_the_job_for_exactly_the_tenants_it_finds_records_in() =>
        fixture.LedgerAfterUp.Select(r => r.Tenant).Should().BeEquivalentTo(
            fixture.JobFinds.Where(f => f.Value.Count > 0).Select(f => f.Key));

    [Fact]
    public void Running_it_again_leaves_one_due_row_per_tenant() =>
        fixture.LedgerAfterSecondRun.Should().Equal(Affected);

    [Fact]
    public void Down_removes_the_requests() => fixture.LedgerAfterDown.Should().BeEmpty();
}
