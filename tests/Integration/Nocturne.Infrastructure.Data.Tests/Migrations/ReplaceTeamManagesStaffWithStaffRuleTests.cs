using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>ReplaceTeamManagesStaffWithStaffRule</c> on a real PostgreSQL as the migrator over a database
/// holding one access where the team decides its staff and one where the owner approves them. A second
/// database runs it up, trusts the team at one access, and runs it back down.
/// </summary>
public class ReplaceTeamManagesStaffWithStaffRuleFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261125171958_AddAlertExcursionOwedIndex";

    private string _migrator = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    internal Guid TeamDecidesAccess { get; } = Guid.CreateVersion7();

    internal Guid OwnerApprovesAccess { get; } = Guid.CreateVersion7();

    internal Guid DownTeamDecides { get; } = Guid.CreateVersion7();

    internal Guid DownOwnerApproves { get; } = Guid.CreateVersion7();

    internal Guid DownTrusted { get; } = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "staff_rule");
        _migrator = database.MigratorConnectionString;
        await ExecuteAsync(_migrator, AccessRows((TeamDecidesAccess, true), (OwnerApprovesAccess, false)));
        await MigrateAsync(_migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "staff_rule_down");
        DownMigrator = down.MigratorConnectionString;
        await ExecuteAsync(DownMigrator, AccessRows((DownTeamDecides, true), (DownOwnerApproves, false), (DownTrusted, true)));
        await MigrateAsync(DownMigrator);
        await ExecuteAsync(DownMigrator, $"UPDATE team_tenant_access SET staff_rule = 'team_trusted' WHERE id = '{DownTrusted}'");
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string AccessRows(params (Guid Access, bool TeamManagesStaff)[] rows) =>
        string.Join('\n', rows.Select(row =>
        {
            var tenant = Guid.CreateVersion7();
            var team = Guid.CreateVersion7();
            return $"""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES ('{tenant}', 'rule-{tenant:N}', 'Site', true, now(), now());
                INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');
                INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours, team_manages_staff)
                VALUES ('{row.Access}', '{team}', '{tenant}', false, {(row.TeamManagesStaff ? "true" : "false")});
                """;
        }));

    internal async Task<(Guid Tenant, Guid Team)> NewSiteAndTeamAsync()
    {
        var tenant = Guid.CreateVersion7();
        var team = Guid.CreateVersion7();
        await ExecuteAsync(_migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'rule-{tenant:N}', 'Site', true, now(), now());
            INSERT INTO teams (id, name) VALUES ('{team}', 'Clinic');
            """);
        return (tenant, team);
    }

    internal Task<int> NewAccessAsync(Guid tenant, Guid team, string staffRule) => ExecuteAsync(_migrator, $"""
        INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours, staff_rule)
        VALUES ('{Guid.CreateVersion7()}', '{team}', '{tenant}', false, '{staffRule}');
        """);

    internal Task<int> NewAccessWithoutRuleAsync(Guid tenant, Guid team) => ExecuteAsync(_migrator, $"""
        INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
        VALUES ('{Guid.CreateVersion7()}', '{team}', '{tenant}', false);
        """);

    internal Task<long> ScalarAsync(string sql) => ScalarAsync(_migrator, sql);

    internal static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<int> ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class ReplaceTeamManagesStaffWithStaffRuleTests(ReplaceTeamManagesStaffWithStaffRuleFixture fixture)
    : IClassFixture<ReplaceTeamManagesStaffWithStaffRuleFixture>
{
    [Fact]
    public async Task An_access_where_the_team_managed_its_staff_lets_the_team_decide_and_one_where_it_did_not_has_the_owner_approve()
    {
        (await fixture.ScalarAsync($"""
            SELECT count(*) FROM team_tenant_access
             WHERE (id = '{fixture.TeamDecidesAccess}' AND staff_rule = 'team_decides')
                OR (id = '{fixture.OwnerApprovesAccess}' AND staff_rule = 'owner_approves')
            """)).Should().Be(2);
        (await fixture.ScalarAsync("""
            SELECT count(*) FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = 'team_tenant_access' AND column_name = 'team_manages_staff'
            """)).Should().Be(0);
    }

    [Theory]
    [InlineData("owner_approves")]
    [InlineData("team_decides")]
    [InlineData("team_trusted")]
    public async Task Each_rule_may_be_stored(string staffRule)
    {
        var (tenant, team) = await fixture.NewSiteAndTeamAsync();

        await fixture.NewAccessAsync(tenant, team, staffRule);

        (await fixture.ScalarAsync($"SELECT count(*) FROM team_tenant_access WHERE team_id = '{team}' AND staff_rule = '{staffRule}'"))
            .Should().Be(1);
    }

    [Fact]
    public async Task An_access_written_without_a_rule_lets_the_team_decide()
    {
        var (tenant, team) = await fixture.NewSiteAndTeamAsync();

        await fixture.NewAccessWithoutRuleAsync(tenant, team);

        (await fixture.ScalarAsync($"SELECT count(*) FROM team_tenant_access WHERE team_id = '{team}' AND staff_rule = 'team_decides'"))
            .Should().Be(1);
    }

    [Theory]
    [InlineData("everyone")]
    [InlineData("")]
    public async Task An_unknown_rule_is_refused(string staffRule)
    {
        var (tenant, team) = await fixture.NewSiteAndTeamAsync();

        (await fixture.Invoking(f => f.NewAccessAsync(tenant, team, staffRule)).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Down_restores_the_switch_with_the_team_managing_its_staff_unless_the_owner_approved_and_drops_the_rule()
    {
        (await ReplaceTeamManagesStaffWithStaffRuleFixture.ScalarAsync(fixture.DownMigrator, $"""
            SELECT count(*) FROM team_tenant_access
             WHERE (id = '{fixture.DownTeamDecides}' AND team_manages_staff)
                OR (id = '{fixture.DownTrusted}' AND team_manages_staff)
                OR (id = '{fixture.DownOwnerApproves}' AND NOT team_manages_staff)
            """)).Should().Be(3);
        (await ReplaceTeamManagesStaffWithStaffRuleFixture.ScalarAsync(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = 'team_tenant_access' AND column_name = 'staff_rule'
            """)).Should().Be(0);
        (await ReplaceTeamManagesStaffWithStaffRuleFixture.ScalarAsync(fixture.DownMigrator,
            "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_team_tenant_access_staff_rule'")).Should().Be(0);
    }
}
