using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>AddTeamFlagRules</c> on a real PostgreSQL as the migrator, then reads and writes a team's rules as the runtime
/// role pinned to its manager, its staff, a non-member, a tenant and nobody, and drives <see cref="FlagRuleRepository"/>
/// and <see cref="TeamRepository.GetTeamsAndTheirPeopleAsync"/> under it. A second database runs it up and back down.
/// </summary>
public class AddTeamFlagRulesFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261127040125_AddFlagRules";

    internal string Migrator { get; private set; } = string.Empty;

    internal string App { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_flag_rules");
        Migrator = database.MigratorConnectionString;
        App = database.AppConnectionString;
        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "add_team_flag_rules_down");
        DownMigrator = down.MigratorConnectionString;
        await MigrateAsync(DownMigrator);
        await MigrateAsync(DownMigrator, PriorMigration);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal async Task<Guid> NewSubjectAsync()
    {
        var subject = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES ('{subject}', 'team-rules-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<Guid> NewTeamAsync(string name = "Ward 4")
    {
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO teams (id, name) VALUES ('{team}', '{name}');");
        return team;
    }

    internal async Task<Guid> NewMemberAsync(Guid team, Guid subject, string kind, string endsAt = "NULL")
    {
        var member = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_members (id, team_id, subject_id, kind, limit_to_24_hours, ends_at)
            VALUES ('{member}', '{team}', '{subject}', '{kind}', false, {endsAt});
            """);
        return member;
    }

    internal async Task<Guid> NewSiteAsync()
    {
        var tenant = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'tfr-{tenant:N}', 'Site', true, now(), now());
            """);
        return tenant;
    }

    internal async Task GiveAccessAsync(Guid team, Guid tenant, string accessEndsAt = "NULL")
    {
        await using var conn = await NpgsqlTestCommands.OpenAsync(Migrator);
        await conn.PinTenantAsync(tenant);
        await conn.ExecuteAsync($"""
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours, access_ends_at)
            VALUES ('{Guid.CreateVersion7()}', '{team}', '{tenant}', false, {accessEndsAt});
            """);
    }

    internal async Task NarrowAsync(Guid team, Guid member, Guid tenant)
    {
        var group = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_groups (id, team_id, name) VALUES ('{group}', '{team}', 'Birch');
            INSERT INTO team_group_people (id, team_group_id, tenant_id) VALUES ('{Guid.CreateVersion7()}', '{group}', '{tenant}');
            INSERT INTO team_group_staff (id, team_group_id, team_id, team_member_id) VALUES ('{Guid.CreateVersion7()}', '{group}', '{team}', '{member}');
            """);
    }

    internal FlagRuleRepository Repository() => new(new SuppliedContextFactory(() => TestContexts.Context(App)));

    internal TeamRepository Teams() => new(new SuppliedContextFactory(() => TestContexts.Context(App)));

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class AddTeamFlagRulesTests(AddTeamFlagRulesFixture fixture) : IClassFixture<AddTeamFlagRulesFixture>
{
    private static readonly FlagRuleDraft Draft =
        new("glucose_statistics", "timeBelow", 14, FlagRuleComparison.Above, 4, NotificationUrgency.Warn);

    /// <summary>A team with a manager and a staff member, and a subject who belongs to no team.</summary>
    private sealed record Ward(Guid Team, Guid Manager, Guid Staff, Guid Stranger);

    private async Task<Ward> WardAsync()
    {
        var team = await fixture.NewTeamAsync();
        var manager = await fixture.NewSubjectAsync();
        var staff = await fixture.NewSubjectAsync();
        await fixture.NewMemberAsync(team, manager, "manager");
        await fixture.NewMemberAsync(team, staff, "staff");
        return new Ward(team, manager, staff, await fixture.NewSubjectAsync());
    }

    private static DateTime Now => DateTime.UtcNow;

    private static async Task ShouldViolateAsync(Func<Task> write, string sqlState) =>
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(sqlState);

    private static string Insert(Guid? subject, Guid? team) => $"""
        INSERT INTO flag_rules (id, subject_id, team_id, dataset, field, days, comparison, value, level)
        VALUES ('{Guid.CreateVersion7()}', {Literal(subject)}, {Literal(team)}, 'glucose_statistics', 'timeBelow', 14, 'above', 4, 'warn')
        """;

    private static string Literal(Guid? id) => id is { } value ? $"'{value}'" : "NULL";

    [Fact]
    public async Task The_table_keeps_the_strict_subject_pin_and_gains_a_team_pin_and_restrictive_manager_writes()
    {
        var policies = await NpgsqlTestCommands.QueryAsync(fixture.Migrator, """
            SELECT p.polname || ':' || p.polcmd::text || ':' || p.polpermissive::text FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
             WHERE c.relname = 'flag_rules' ORDER BY p.polname
            """, r => r.GetString(0));

        policies.Should().Equal("manager_delete:d:false", "manager_update:w:false", "subject_pin:*:true", "team_pin:*:true");
    }

    [Fact]
    public async Task A_manager_reads_adds_changes_and_deletes_the_teams_rules()
    {
        var ward = await WardAsync();
        var manager = Pin.Subject(ward.Manager);
        var count = $"SELECT count(*) FROM flag_rules WHERE team_id = '{ward.Team}'";

        await ExecuteAsync(manager, Insert(null, ward.Team));
        (await ScalarAsync(manager, count)).Should().Be(1);
        (await RowsAsync(manager, $"UPDATE flag_rules SET value = 9 WHERE team_id = '{ward.Team}'")).Should().Be(1);
        (await RowsAsync(manager, $"DELETE FROM flag_rules WHERE team_id = '{ward.Team}'")).Should().Be(1);
        (await ScalarAsync(manager, count)).Should().Be(0);
    }

    [Fact]
    public async Task Staff_read_the_teams_rules_and_cannot_add_change_or_delete_one()
    {
        var ward = await WardAsync();
        await ExecuteAsync(Pin.Subject(ward.Manager), Insert(null, ward.Team));
        var staff = Pin.Subject(ward.Staff);
        var count = $"SELECT count(*) FROM flag_rules WHERE team_id = '{ward.Team}' AND value = 4";

        (await ScalarAsync(staff, count)).Should().Be(1);
        await ShouldViolateAsync(() => ExecuteAsync(staff, Insert(null, ward.Team)), PostgresErrorCodes.InsufficientPrivilege);
        (await RowsAsync(staff, $"UPDATE flag_rules SET value = 9 WHERE team_id = '{ward.Team}'")).Should().Be(0);
        (await RowsAsync(staff, $"DELETE FROM flag_rules WHERE team_id = '{ward.Team}'")).Should().Be(0);
        (await ScalarAsync(Pin.Subject(ward.Manager), count)).Should().Be(1);
    }

    [Fact]
    public async Task A_non_member_a_tenant_context_and_a_context_with_no_subject_read_and_write_no_team_rule()
    {
        var ward = await WardAsync();
        await ExecuteAsync(Pin.Subject(ward.Manager), Insert(null, ward.Team));
        var count = $"SELECT count(*) FROM flag_rules WHERE team_id = '{ward.Team}'";

        (await ScalarAsync(Pin.Subject(ward.Stranger), count)).Should().Be(0);
        (await ScalarAsync(Pin.Tenant(Guid.CreateVersion7()), count)).Should().Be(0);
        (await ScalarAsync(Pin.Subject(null), count)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.Migrator, count)).Should().Be(0, "the migrator obeys the forced policy");
        await ShouldViolateAsync(() => ExecuteAsync(Pin.Subject(ward.Stranger), Insert(null, ward.Team)), PostgresErrorCodes.InsufficientPrivilege);
        await ShouldViolateAsync(() => ExecuteAsync(Pin.Tenant(Guid.CreateVersion7()), Insert(null, ward.Team)), PostgresErrorCodes.InsufficientPrivilege);
        (await RowsAsync(Pin.Subject(ward.Stranger), $"DELETE FROM flag_rules WHERE team_id = '{ward.Team}'")).Should().Be(0);
    }

    [Fact]
    public async Task A_manager_cannot_move_a_rule_to_a_team_they_only_staff()
    {
        var ward = await WardAsync();
        var other = await fixture.NewTeamAsync("Ward 5");
        await fixture.NewMemberAsync(other, ward.Manager, "staff");
        await ExecuteAsync(Pin.Subject(ward.Manager), Insert(null, ward.Team));

        await ShouldViolateAsync(
            () => ExecuteAsync(Pin.Subject(ward.Manager), $"UPDATE flag_rules SET team_id = '{other}' WHERE team_id = '{ward.Team}'"),
            PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_personal_rule_stays_its_owners_alone_for_a_manager_and_staff_of_their_team()
    {
        var ward = await WardAsync();
        await ExecuteAsync(Pin.Subject(ward.Staff), Insert(ward.Staff, null));
        var own = $"SELECT count(*) FROM flag_rules WHERE subject_id = '{ward.Staff}'";

        (await ScalarAsync(Pin.Subject(ward.Manager), own)).Should().Be(0);
        (await RowsAsync(Pin.Subject(ward.Manager), $"DELETE FROM flag_rules WHERE subject_id = '{ward.Staff}'")).Should().Be(0);
        (await RowsAsync(Pin.Subject(ward.Staff), $"UPDATE flag_rules SET value = 9 WHERE subject_id = '{ward.Staff}'")).Should().Be(1);
        (await RowsAsync(Pin.Subject(ward.Staff), $"DELETE FROM flag_rules WHERE subject_id = '{ward.Staff}'")).Should().Be(1);
        await ShouldViolateAsync(() => ExecuteAsync(Pin.Subject(ward.Manager), Insert(ward.Staff, null)), PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_repository_lets_a_manager_write_and_any_live_member_read_and_refuses_everyone_else_as_not_found()
    {
        var ward = await WardAsync();
        var ended = await fixture.NewSubjectAsync();
        await fixture.NewMemberAsync(ward.Team, ended, "staff", "now() - interval '1 day'");
        var rules = fixture.Repository();

        var made = await rules.CreateForTeamAsync(ward.Team, ward.Manager, Draft, CancellationToken.None);
        made.Change.Should().Be(TeamFlagRuleChange.Changed);
        var rule = made.Rule!;

        (await rules.CreateForTeamAsync(ward.Team, ward.Staff, Draft, CancellationToken.None)).Change.Should().Be(TeamFlagRuleChange.NotFound);
        (await rules.UpdateForTeamAsync(ward.Team, ward.Staff, rule.Id, Draft with { Value = 9 }, CancellationToken.None))
            .Change.Should().Be(TeamFlagRuleChange.NotFound);
        (await rules.DeleteForTeamAsync(ward.Team, ward.Staff, rule.Id, CancellationToken.None)).Should().Be(TeamFlagRuleChange.NotFound);
        (await rules.CreateForTeamAsync(ward.Team, ward.Stranger, Draft, CancellationToken.None)).Change.Should().Be(TeamFlagRuleChange.NotFound);

        (await rules.GetOfTeamAsync(ward.Team, ward.Staff, Now, CancellationToken.None)).Should().ContainSingle().Which.Id.Should().Be(rule.Id);
        (await rules.GetOfTeamAsync(ward.Team, ward.Manager, Now, CancellationToken.None)).Should().ContainSingle();
        (await rules.GetOfTeamAsync(ward.Team, ended, Now, CancellationToken.None)).Should().BeNull();
        (await rules.GetOfTeamAsync(ward.Team, ward.Stranger, Now, CancellationToken.None)).Should().BeNull();
        (await rules.GetOfTeamsAsync(ward.Staff, [ward.Team], CancellationToken.None))[ward.Team].Should().ContainSingle();
        (await rules.GetOfSubjectAsync(ward.Manager, CancellationToken.None)).Should().BeEmpty("a team's rule is no one's own");

        var changed = await rules.UpdateForTeamAsync(ward.Team, ward.Manager, rule.Id, Draft with { Value = 9 }, CancellationToken.None);
        changed.Rule!.Value.Should().Be(9);
        (await rules.DeleteForTeamAsync(ward.Team, ward.Manager, rule.Id, CancellationToken.None)).Should().Be(TeamFlagRuleChange.Changed);
        (await rules.GetOfTeamAsync(ward.Team, ward.Manager, Now, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_repository_holds_a_team_to_its_limit_and_refuses_the_next()
    {
        var ward = await WardAsync();
        var rules = fixture.Repository();

        for (var i = 0; i < FlagRuleLimits.PerTeam; i++)
            (await rules.CreateForTeamAsync(ward.Team, ward.Manager, Draft with { Value = i }, CancellationToken.None))
                .Change.Should().Be(TeamFlagRuleChange.Changed);
        var refused = await rules.CreateForTeamAsync(ward.Team, ward.Manager, Draft, CancellationToken.None);

        refused.Should().Be(new TeamFlagRuleWrite(TeamFlagRuleChange.LimitReached, null));
        (await rules.GetOfTeamAsync(ward.Team, ward.Staff, Now, CancellationToken.None))!.Select(r => r.Value)
            .Should().Equal(Enumerable.Range(0, FlagRuleLimits.PerTeam).Select(i => (double)i), "rules are listed oldest first");
    }

    [Fact]
    public async Task Creates_at_once_by_two_managers_never_pass_the_teams_limit_together()
    {
        var ward = await WardAsync();
        var second = await fixture.NewSubjectAsync();
        await fixture.NewMemberAsync(ward.Team, second, "manager");
        var rules = fixture.Repository();
        for (var i = 0; i < FlagRuleLimits.PerTeam - 5; i++)
            await rules.CreateForTeamAsync(ward.Team, ward.Manager, Draft, CancellationToken.None);
        using var start = new SemaphoreSlim(0);

        var creates = Enumerable.Range(0, 20).Select(async i =>
        {
            await start.WaitAsync();
            return await fixture.Repository().CreateForTeamAsync(ward.Team, i % 2 == 0 ? ward.Manager : second, Draft, CancellationToken.None);
        }).ToList();
        start.Release(creates.Count);
        var made = await Task.WhenAll(creates);

        made.Count(r => r.Change == TeamFlagRuleChange.Changed).Should().Be(5);
        made.Count(r => r.Change == TeamFlagRuleChange.LimitReached).Should().Be(15);
        (await rules.GetOfTeamAsync(ward.Team, ward.Manager, Now, CancellationToken.None)).Should().HaveCount(FlagRuleLimits.PerTeam);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task A_manager_removed_while_their_write_waits_on_the_teams_manager_lock_is_answered_not_found(string write)
    {
        var ward = await WardAsync();
        var rules = fixture.Repository();
        var rule = (await rules.CreateForTeamAsync(ward.Team, ward.Manager, Draft, CancellationToken.None)).Rule!;
        await using var holder = TestContexts.Context(fixture.App);
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.LockTeamManagersAsync(ward.Team, CancellationToken.None);

        var waiting = write switch
        {
            "create" => ChangeOf(fixture.Repository().CreateForTeamAsync(ward.Team, ward.Manager, Draft, CancellationToken.None)),
            "update" => ChangeOf(fixture.Repository().UpdateForTeamAsync(
                ward.Team, ward.Manager, rule.Id, Draft with { Value = 9 }, CancellationToken.None)),
            _ => fixture.Repository().DeleteForTeamAsync(ward.Team, ward.Manager, rule.Id, CancellationToken.None),
        };
        await PostgresLockWaits.UntilBlockedByAsync(
            fixture.Migrator, ((NpgsqlConnection)holder.Database.GetDbConnection()).ProcessID, waiting);
        await NpgsqlTestCommands.ExecuteAsync(fixture.Migrator,
            $"UPDATE team_members SET kind = 'staff' WHERE team_id = '{ward.Team}' AND subject_id = '{ward.Manager}'");
        await held.RollbackAsync();

        (await waiting.WaitAsync(Eventually.DefaultPatience)).Should().Be(TeamFlagRuleChange.NotFound);
        (await rules.GetOfTeamAsync(ward.Team, ward.Staff, Now, CancellationToken.None)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { rule.Id, Value = 4.0 });
    }

    private static async Task<TeamFlagRuleChange> ChangeOf(Task<TeamFlagRuleWrite> write) => (await write).Change;

    [Fact]
    public async Task Each_change_to_a_teams_rule_writes_a_team_history_row_with_the_rule_and_an_edit_its_before_and_after()
    {
        var ward = await WardAsync();
        var rules = fixture.Repository();

        var rule = (await rules.CreateForTeamAsync(ward.Team, ward.Manager, Draft, CancellationToken.None)).Rule!;
        await rules.UpdateForTeamAsync(ward.Team, ward.Manager, rule.Id, Draft with { Value = 9, Level = NotificationUrgency.Hazard }, CancellationToken.None);
        await rules.DeleteForTeamAsync(ward.Team, ward.Manager, rule.Id, CancellationToken.None);
        await rules.UpdateForTeamAsync(ward.Team, ward.Staff, rule.Id, Draft, CancellationToken.None);

        var rows = await NpgsqlTestCommands.QueryAsync(fixture.Migrator, $"""
            SELECT event_type, details->>'kind', tenant_id IS NULL, target_subject_id IS NULL,
                   coalesce(details->'old_flag_rule'->>'value', '-') || '/' || coalesce(details->'old_flag_rule'->>'level', '-'),
                   (details->'flag_rule'->>'value') || '/' || (details->'flag_rule'->>'level'), details->'flag_rule'->>'id'
              FROM auth_audit_log WHERE team_id = '{ward.Team}' ORDER BY id
            """, r => (r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetString(4), r.GetString(5), r.GetString(6)));

        var id = rule.Id.ToString();
        rows.Should().Equal(
            ("team_flag_rule_created", "team", true, true, "-/-", "4/warn", id),
            ("team_flag_rule_changed", "team", true, true, "4/warn", "9/hazard", id),
            ("team_flag_rule_deleted", "team", true, true, "-/-", "9/hazard", id));
    }

    [Fact]
    public async Task Each_team_of_a_staff_member_comes_with_every_live_site_of_the_team_whatever_groups_narrow_them()
    {
        var ward = await WardAsync();
        var narrowed = await fixture.NewSubjectAsync();
        var place = await fixture.NewMemberAsync(ward.Team, narrowed, "staff");
        var birch = await fixture.NewSiteAsync();
        var oak = await fixture.NewSiteAsync();
        var gone = await fixture.NewSiteAsync();
        await fixture.GiveAccessAsync(ward.Team, birch);
        await fixture.GiveAccessAsync(ward.Team, oak);
        await fixture.GiveAccessAsync(ward.Team, gone, "now() - interval '1 day'");
        await fixture.NarrowAsync(ward.Team, place, birch);
        var teams = fixture.Teams();

        var ofNarrowed = await teams.GetTeamsAndTheirPeopleAsync(narrowed, Now, CancellationToken.None);
        var managing = await teams.GetTeamsAndTheirPeopleAsync(ward.Manager, Now, CancellationToken.None);

        ofNarrowed.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Id = ward.Team, Name = "Ward 4", CallerIsStaff = true, People = new HashSet<Guid> { birch, oak } });
        managing.Should().ContainSingle().Which.CallerIsStaff.Should().BeFalse();
        (await teams.GetTeamsAndTheirPeopleAsync(ward.Stranger, Now, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_ended_staff_place_gives_no_team()
    {
        var team = await fixture.NewTeamAsync();
        var staff = await fixture.NewSubjectAsync();
        await fixture.NewMemberAsync(team, staff, "staff", "now() - interval '1 day'");
        await fixture.GiveAccessAsync(team, await fixture.NewSiteAsync());

        (await fixture.Teams().GetTeamsAndTheirPeopleAsync(staff, Now, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Down_drops_the_team_policies_and_leaves_the_subject_pin()
    {
        (await NpgsqlTestCommands.QueryAsync(fixture.DownMigrator, """
            SELECT p.polname FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid WHERE c.relname = 'flag_rules'
            """, r => r.GetString(0))).Should().Equal("subject_pin");
    }

    private sealed record Pin(string Setting, Guid? Value)
    {
        public static Pin Subject(Guid? subject) => new("app.current_subject_id", subject);

        public static Pin Tenant(Guid? tenant) => new("app.current_tenant_id", tenant);
    }

    private async Task<long> ScalarAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(Pin pin, string sql) => await RowsAsync(pin, sql);

    private async Task<int> RowsAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> PinnedAsync(Pin pin)
    {
        var conn = new NpgsqlConnection(fixture.App);
        await conn.OpenAsync();
        await using var set = new NpgsqlCommand($"SELECT set_config('{pin.Setting}', '{pin.Value?.ToString() ?? string.Empty}', false)", conn);
        await set.ExecuteNonQueryAsync();
        return conn;
    }
}
