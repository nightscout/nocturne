using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Mappers;
using Npgsql;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RetireStateSpansOutsideCategories</c> over state spans stored under the removed
/// <c>TempBasal</c> and <c>BasalDelivery</c> categories in two tenants, then down and up again, on a
/// real PostgreSQL.
/// </summary>
public class RetireStateSpansOutsideCategoriesFixture : IAsyncLifetime
{
    private const string MigrationUnderTest = "20261027153246_RetireStateSpansOutsideCategories";

    /// <summary>The migration immediately before the one under test.</summary>
    private const string PriorMigration = "20261027130450_AddV4ListSortIndexes";

    private string _migratorConnectionString = string.Empty;
    private string _superuserConnectionString = string.Empty;

    internal static readonly DateTime SeededAt = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    internal static readonly Guid TempBasalInA = Guid.Parse("c0000000-0000-7000-8000-000000000001");
    internal static readonly Guid BasalDeliveryInA = Guid.Parse("c0000000-0000-7000-8000-000000000002");
    internal static readonly Guid PumpModeInA = Guid.Parse("c0000000-0000-7000-8000-000000000003");
    internal static readonly Guid UserDeletedTempBasalInA = Guid.Parse("c0000000-0000-7000-8000-000000000004");
    internal static readonly Guid TempBasalInB = Guid.Parse("c0000000-0000-7000-8000-000000000005");
    internal static readonly Guid OverrideInB = Guid.Parse("c0000000-0000-7000-8000-000000000006");

    internal Dictionary<Guid, Row> Seeded { get; } = [];
    internal Dictionary<Guid, Row> AfterUp { get; } = [];
    internal Dictionary<Guid, Row> AfterDown { get; } = [];
    internal Dictionary<Guid, Row> AfterSecondUp { get; } = [];

    /// <summary>
    /// Each row the migration's statement updated when run as the superuser, which bypasses RLS,
    /// with the tenant the loop had pinned at the time.
    /// </summary>
    internal List<(Guid Span, Guid Row, string Pinned)> UpdatesAsSuperuser { get; } = [];

    internal sealed record Row(Guid Tenant, string Category, DateTime? DeletedAt, bool DeletedByUser, DateTime UpdatedAt);

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "retire_state_span_categories");
        _migratorConnectionString = database.MigratorConnectionString;
        _superuserConnectionString = database.SuperuserConnectionString;

        await SeedAsync();
        await ReadAsync(Seeded);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterUp);
        await MigrateToAsync(PriorMigration);
        await ReadAsync(AfterDown);
        await MigrateToAsync(targetMigration: null);
        await ReadAsync(AfterSecondUp);
        await RunAsSuperuserAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? targetMigration)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;

        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task<string> UpSqlAsync()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsql => npgsql.UseNocturneMigrations())
            .Options;

        await using var context = new NocturneDbContext(options);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(
            assembly.Migrations[MigrationUnderTest], context.Database.ProviderName!);
        return migration.UpOperations.OfType<SqlOperation>().Single().Sql;
    }

    /// <summary>
    /// Makes both live retired spans live again and reruns the statement as the superuser, logging
    /// each update from a trigger, all in a transaction that is rolled back.
    /// </summary>
    private async Task RunAsSuperuserAsync()
    {
        var upSql = await UpSqlAsync();

        await using var conn = new NpgsqlConnection(_superuserConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = $$"""
                CREATE TEMP TABLE span_updates (span uuid, row_tenant uuid, pinned text);
                CREATE FUNCTION log_span_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO span_updates VALUES (NEW.id, NEW.tenant_id, current_setting('app.current_tenant_id', true));
                    RETURN NEW;
                END $$;
                UPDATE state_spans SET deleted_at = NULL
                WHERE id IN ('{{TempBasalInA}}', '{{TempBasalInB}}');
                CREATE TRIGGER log_span_update AFTER UPDATE ON state_spans
                    FOR EACH ROW EXECUTE FUNCTION log_span_update();
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using (var up = conn.CreateCommand())
        {
            up.CommandText = upSql;
            await up.ExecuteNonQueryAsync();
        }

        await using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT span, row_tenant, pinned FROM span_updates;";
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                UpdatesAsSuperuser.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
        }

        await transaction.RollbackAsync();
    }

    private async Task ReadAsync(Dictionary<Guid, Row> rows)
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
                pin.Parameters.AddWithValue("tenant", tenant.ToString());
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, category, deleted_at, deleted_by_user, updated_at FROM state_spans;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[reader.GetGuid(0)] = new Row(
                    tenant,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    reader.GetBoolean(3),
                    reader.GetDateTime(4));
        }
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, created_at, updated_at, deleted_at, deleted_by_user)
            VALUES
              ('{{TempBasalInA}}', '{{TenantA}}', 'TempBasal', 'Active', '2026-01-01T08:00:00Z', 'Loop',
               now(), '{{SeededAt:O}}', NULL, false),
              ('{{BasalDeliveryInA}}', '{{TenantA}}', 'BasalDelivery', 'Algorithm', '2026-03-01T08:00:00Z', 'Loop',
               now(), '{{SeededAt:O}}', NULL, false),
              ('{{PumpModeInA}}', '{{TenantA}}', 'PumpMode', 'Automatic', '2026-03-01T09:00:00Z', 'Loop',
               now(), '{{SeededAt:O}}', NULL, false),
              ('{{UserDeletedTempBasalInA}}', '{{TenantA}}', 'TempBasal', 'Active', '2026-01-02T08:00:00Z', 'Loop',
               now(), '{{SeededAt:O}}', '{{SeededAt:O}}', true);

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO state_spans
              (id, tenant_id, category, state, start_timestamp, source, created_at, updated_at, deleted_at, deleted_by_user)
            VALUES
              ('{{TempBasalInB}}', '{{TenantB}}', 'TempBasal', 'Active', '2026-01-01T08:00:00Z', 'AAPS',
               now(), '{{SeededAt:O}}', NULL, false),
              ('{{OverrideInB}}', '{{TenantB}}', 'Override', 'Custom', '2026-01-01T09:00:00Z', 'AAPS',
               now(), '{{SeededAt:O}}', NULL, false);

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="RetireStateSpansOutsideCategoriesFixture"/>
[Trait("Category", "Integration")]
public class RetireStateSpansOutsideCategoriesTests(RetireStateSpansOutsideCategoriesFixture fixture)
    : IClassFixture<RetireStateSpansOutsideCategoriesFixture>
{
    [Fact]
    public void No_live_span_in_any_tenant_carries_a_category_outside_the_enum()
    {
        var live = fixture.AfterUp.Values.Where(row => row.DeletedAt is null).ToList();

        live.Select(row => row.Tenant).Distinct().Should().HaveCount(2);
        live.Should().OnlyContain(row => StateSpanMapper.CategoryNames.Contains(row.Category));
    }

    [Theory]
    [MemberData(nameof(RetiredSpans))]
    public void A_live_retired_span_is_soft_deleted_by_the_system_without_moving_its_history_clock(Guid id)
    {
        var after = fixture.AfterUp[id];

        fixture.Seeded[id].DeletedAt.Should().BeNull();
        after.DeletedAt.Should().NotBeNull();
        after.DeletedByUser.Should().BeFalse();
        after.Category.Should().Be(fixture.Seeded[id].Category);
        after.UpdatedAt.Should().Be(RetireStateSpansOutsideCategoriesFixture.SeededAt);
    }

    public static TheoryData<Guid> RetiredSpans() =>
    [
        RetireStateSpansOutsideCategoriesFixture.TempBasalInA,
        RetireStateSpansOutsideCategoriesFixture.BasalDeliveryInA,
        RetireStateSpansOutsideCategoriesFixture.TempBasalInB,
    ];

    [Theory]
    [MemberData(nameof(UntouchedSpans))]
    public void Current_spans_and_a_user_deleted_retired_span_are_untouched(Guid id)
    {
        fixture.AfterUp[id].Should().Be(fixture.Seeded[id]);
    }

    public static TheoryData<Guid> UntouchedSpans() =>
    [
        RetireStateSpansOutsideCategoriesFixture.PumpModeInA,
        RetireStateSpansOutsideCategoriesFixture.OverrideInB,
        RetireStateSpansOutsideCategoriesFixture.UserDeletedTempBasalInA,
    ];

    [Fact]
    public void Run_as_a_role_that_bypasses_rls_each_tenant_is_updated_only_while_it_is_pinned()
    {
        fixture.UpdatesAsSuperuser.Select(u => u.Span).Should().BeEquivalentTo(
            [RetireStateSpansOutsideCategoriesFixture.TempBasalInA, RetireStateSpansOutsideCategoriesFixture.TempBasalInB]);
        fixture.UpdatesAsSuperuser.Should().OnlyContain(u => u.Pinned == u.Row.ToString());
    }

    [Fact]
    public void Down_leaves_the_spans_retired_and_up_again_changes_nothing()
    {
        fixture.AfterDown.Should().BeEquivalentTo(fixture.AfterUp);
        fixture.AfterSecondUp.Should().BeEquivalentTo(fixture.AfterUp);
    }
}
