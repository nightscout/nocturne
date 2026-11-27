using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>NarrowTeamGroupsForStaff</c> on a real PostgreSQL as the migrator over a staff place that existed before
/// it, then reads the groups as the runtime role pinned to a staff member, to a manager and to nobody. A second
/// database runs it up and back down.
/// </summary>
public class NarrowTeamGroupsForStaffFixture : IAsyncLifetime
{
    internal const string PriorMigration = "20261126105924_AddTeamGroups";

    internal string Migrator { get; private set; } = string.Empty;

    internal string App { get; private set; } = string.Empty;

    internal string DownMigrator { get; private set; } = string.Empty;

    internal Guid ExistingTeam { get; private set; }

    internal Guid ExistingGroup { get; private set; }

    internal Guid ExistingPlace { get; private set; }

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "narrow_team_groups");
        Migrator = database.MigratorConnectionString;
        App = database.AppConnectionString;
        ExistingTeam = await NewTeamAsync();
        ExistingGroup = await NewGroupAsync(ExistingTeam, "Before");
        ExistingPlace = Guid.CreateVersion7();
        var member = await NewMemberAsync(ExistingTeam, await NewSubjectAsync(), "staff");
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_group_staff (id, team_group_id, team_member_id) VALUES ('{ExistingPlace}', '{ExistingGroup}', '{member}');
            """);
        await MigrateAsync(Migrator);

        var down = await SharedPostgres.CreateDatabaseAtMigrationAsync(PriorMigration, "narrow_team_groups_down");
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
            VALUES ('{subject}', 'narrow-{subject:N}', true, false, now(), now(), 'Approved');
            """);
        return subject;
    }

    internal async Task<Guid> NewSiteAsync()
    {
        var tenant = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', 'narrow-{tenant:N}', 'Site', true, now(), now());
            """);
        return tenant;
    }

    internal async Task<Guid> NewTeamAsync()
    {
        var team = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO teams (id, name) VALUES ('{team}', 'Camp');");
        return team;
    }

    internal async Task<Guid> NewMemberAsync(Guid team, Guid subject, string kind)
    {
        var member = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
            INSERT INTO team_members (id, team_id, subject_id, kind, limit_to_24_hours)
            VALUES ('{member}', '{team}', '{subject}', '{kind}', false);
            """);
        return member;
    }

    internal async Task<Guid> NewGroupAsync(Guid team, string name)
    {
        var group = Guid.CreateVersion7();
        await NpgsqlTestCommands.ExecuteAsync(Migrator, $"INSERT INTO team_groups (id, team_id, name) VALUES ('{group}', '{team}', '{name}');");
        return group;
    }

    internal Task<int> NewPersonAsync(Guid group, Guid tenant) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_group_people (id, team_group_id, tenant_id) VALUES ('{Guid.CreateVersion7()}', '{group}', '{tenant}');
        """);

    internal Task<int> NewStaffAsync(Guid team, Guid group, Guid member) => NpgsqlTestCommands.ExecuteAsync(Migrator, $"""
        INSERT INTO team_group_staff (id, team_group_id, team_id, team_member_id) VALUES ('{Guid.CreateVersion7()}', '{group}', '{team}', '{member}');
        """);

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        var options = TestContexts.MigrationsOptions(connectionString);
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }
}

[Trait("Category", "Integration")]
public class NarrowTeamGroupsForStaffTests(NarrowTeamGroupsForStaffFixture fixture) : IClassFixture<NarrowTeamGroupsForStaffFixture>
{
    /// <summary>A team with a manager, two staff members and two groups of a site each, and a second team's group.</summary>
    private sealed record Camp(
        Guid Team, Guid Manager, Guid Staff, Guid StaffMember, Guid OtherStaff, Guid Birch, Guid Oak, Guid OtherTeamGroup,
        Guid BirchSite, Guid OakSite);

    private async Task<Camp> CampAsync()
    {
        var team = await fixture.NewTeamAsync();
        var manager = await fixture.NewSubjectAsync();
        var staff = await fixture.NewSubjectAsync();
        var otherStaff = await fixture.NewSubjectAsync();
        await fixture.NewMemberAsync(team, manager, "manager");
        var staffMember = await fixture.NewMemberAsync(team, staff, "staff");
        var otherMember = await fixture.NewMemberAsync(team, otherStaff, "staff");
        var birch = await fixture.NewGroupAsync(team, "Birch");
        var oak = await fixture.NewGroupAsync(team, "Oak");
        var birchSite = await fixture.NewSiteAsync();
        var oakSite = await fixture.NewSiteAsync();
        await fixture.NewPersonAsync(birch, birchSite);
        await fixture.NewPersonAsync(oak, oakSite);
        await fixture.NewStaffAsync(team, birch, staffMember);
        await fixture.NewStaffAsync(team, birch, otherMember);
        await fixture.NewStaffAsync(team, oak, otherMember);

        var otherTeam = await fixture.NewTeamAsync();
        await fixture.NewMemberAsync(otherTeam, staff, "staff");
        var otherTeamGroup = await fixture.NewGroupAsync(otherTeam, "Birch");
        await fixture.NewPersonAsync(otherTeamGroup, birchSite);
        return new Camp(team, manager, staff, staffMember, otherStaff, birch, oak, otherTeamGroup, birchSite, oakSite);
    }

    [Fact]
    public async Task A_staff_place_that_existed_takes_its_groups_team()
    {
        (await NpgsqlTestCommands.ScalarAsync<Guid>(fixture.Migrator,
            $"SELECT team_id FROM team_group_staff WHERE id = '{fixture.ExistingPlace}'")).Should().Be(fixture.ExistingTeam);
    }

    [Fact]
    public async Task A_staff_member_reads_only_the_groups_they_are_in_those_groups_people_and_their_own_place()
    {
        var camp = await CampAsync();
        var pin = Pin.Subject(camp.Staff);

        (await IdsAsync(pin, $"SELECT id FROM team_groups WHERE team_id = '{camp.Team}'")).Should().Equal(camp.Birch);
        (await IdsAsync(pin, $"SELECT tenant_id FROM team_group_people WHERE team_group_id IN ('{camp.Birch}', '{camp.Oak}')"))
            .Should().Equal(camp.BirchSite);
        (await IdsAsync(pin, $"SELECT team_group_id FROM team_group_staff WHERE team_id = '{camp.Team}'"))
            .Should().Equal(camp.Birch);
        (await IdsAsync(pin, $"SELECT id FROM team_groups WHERE id = '{camp.OtherTeamGroup}'"))
            .Should().BeEmpty("a group of a team they are staff of is theirs only while they are in it");
    }

    [Fact]
    public async Task A_manager_reads_every_group_of_their_team_with_its_people_and_staff_and_nobody_reads_them_all()
    {
        var camp = await CampAsync();
        var groups = $"SELECT id FROM team_groups WHERE team_id = '{camp.Team}' ORDER BY id";
        var people = $"SELECT tenant_id FROM team_group_people WHERE team_group_id IN ('{camp.Birch}', '{camp.Oak}') ORDER BY tenant_id";
        var staff = $"SELECT count(*) FROM team_group_staff WHERE team_id = '{camp.Team}'";

        foreach (var pin in new[] { Pin.Subject(camp.Manager), Pin.Subject(null) })
        {
            (await IdsAsync(pin, groups)).Should().Equal(new[] { camp.Birch, camp.Oak }.Order());
            (await IdsAsync(pin, people)).Should().Equal(new[] { camp.BirchSite, camp.OakSite }.Order());
            (await ScalarAsync(pin, staff)).Should().Be(3);
        }

        (await ScalarAsync(Pin.Subject(camp.Manager), $"SELECT count(*) FROM team_groups WHERE id = '{camp.OtherTeamGroup}'"))
            .Should().Be(0, "the manager is no member of the other team");
    }

    [Fact]
    public async Task Only_a_manager_inserts_a_staff_place_and_only_in_their_own_team()
    {
        var camp = await CampAsync();
        var newcomer = await fixture.NewMemberAsync(camp.Team, await fixture.NewSubjectAsync(), "staff");
        string Place(Guid team) =>
            $"INSERT INTO team_group_staff (id, team_group_id, team_id, team_member_id) VALUES ('{Guid.CreateVersion7()}', '{camp.Oak}', '{team}', '{newcomer}')";

        await ExecuteAsync(Pin.Subject(camp.Manager), Place(camp.Team));
        (await Refused(() => ExecuteAsync(Pin.Subject(camp.Staff), Place(camp.Team)))).Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        var otherTeam = await fixture.NewTeamAsync();
        (await Refused(() => ExecuteAsync(Pin.Subject(camp.Manager), Place(otherTeam))))
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("team_groups")]
    [InlineData("team_group_people")]
    [InlineData("team_group_staff")]
    public async Task A_staff_member_neither_updates_nor_deletes_what_they_read_of_their_group_and_a_manager_does_both(string table)
    {
        var camp = await CampAsync();
        var rows = BirchRow(camp, table);
        var count = $"SELECT count(*) FROM {table} WHERE {rows}";
        var staff = Pin.Subject(camp.Staff);
        (await ScalarAsync(staff, count)).Should().Be(1, "the staff member reads the row");
        var before = await RowAsync(table, rows);

        (await RefusedOrNoneAsync(staff, $"UPDATE {table} SET id = id WHERE {rows}")).Should().BeTrue();
        (await ExecuteAsync(staff, $"DELETE FROM {table} WHERE {rows}")).Should().Be(0);
        (await RowAsync(table, rows)).Should().Be(before);

        (await ExecuteAsync(Pin.Subject(camp.Manager), $"UPDATE {table} SET id = id WHERE {rows}")).Should().Be(1);
        (await ExecuteAsync(Pin.Subject(camp.Manager), $"DELETE FROM {table} WHERE {rows}")).Should().Be(1);
        (await ScalarAsync(Pin.Subject(null), count)).Should().Be(0);
    }

    [Theory]
    [InlineData("team_groups")]
    [InlineData("team_group_people")]
    [InlineData("team_group_staff")]
    public async Task A_staff_member_who_manages_a_team_of_their_own_neither_moves_a_row_of_their_group_into_it_nor_deletes_it(string table)
    {
        var camp = await CampAsync();
        var mine = await fixture.NewTeamAsync();
        await fixture.NewMemberAsync(mine, camp.Staff, "manager");
        var mineGroup = await fixture.NewGroupAsync(mine, "Mine");
        var rows = BirchRow(camp, table);
        var staff = Pin.Subject(camp.Staff);
        (await ScalarAsync(staff, $"SELECT count(*) FROM {table} WHERE {rows}")).Should().Be(1, "the staff member reads the row");
        var before = await RowAsync(table, rows);
        var move = table switch
        {
            "team_groups" => $"team_id = '{mine}'",
            "team_group_people" => $"team_group_id = '{mineGroup}'",
            _ => $"team_id = '{mine}', team_group_id = '{mineGroup}'",
        };

        (await RefusedOrNoneAsync(staff, $"UPDATE {table} SET {move} WHERE {rows}"))
            .Should().BeTrue("the new row's team is one they manage, but the row's team is not");
        (await ExecuteAsync(staff, $"DELETE FROM {table} WHERE {rows}")).Should().Be(0);
        (await RowAsync(table, rows)).Should().Be(before);
    }

    [Fact]
    public async Task Down_gives_staff_their_teams_groups_again_and_drops_the_column()
    {
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM information_schema.columns WHERE table_name = 'team_group_staff' AND column_name = 'team_id'
            """)).Should().Be(0);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
             WHERE c.relname IN ('team_groups', 'team_group_people', 'team_group_staff') AND p.polname = 'subject_pin'
               AND pg_get_expr(p.polqual, p.polrelid) NOT LIKE '%manager%'
            """)).Should().Be(3);
        (await NpgsqlTestCommands.ScalarAsync<long>(fixture.DownMigrator, """
            SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
             WHERE c.relname IN ('team_groups', 'team_group_people', 'team_group_staff') AND p.polname LIKE 'manager_%'
            """)).Should().Be(0);
    }

    private static string BirchRow(Camp camp, string table) => table switch
    {
        "team_groups" => $"id = '{camp.Birch}'",
        "team_group_people" => $"team_group_id = '{camp.Birch}'",
        _ => $"team_group_id = '{camp.Birch}' AND team_member_id = '{camp.StaffMember}'",
    };

    /// <summary>The row as nobody pinned reads it, whole, to compare before and after a write.</summary>
    private async Task<string> RowAsync(string table, string rows)
    {
        await using var conn = await PinnedAsync(Pin.Subject(null));
        await using var command = new NpgsqlCommand($"SELECT row_to_json(t)::text FROM {table} t WHERE {rows}", conn);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Whether a write is refused for want of privilege, or affects no row.</summary>
    private async Task<bool> RefusedOrNoneAsync(Pin pin, string sql)
    {
        try
        {
            return await ExecuteAsync(pin, sql) == 0;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return true;
        }
    }

    private sealed record Pin(Guid? SubjectId)
    {
        public static Pin Subject(Guid? subject) => new(subject);
    }

    private static async Task<string?> Refused(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (PostgresException e)
        {
            return e.SqlState;
        }
    }

    private async Task<List<Guid>> IdsAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private async Task<long> ScalarAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<int> ExecuteAsync(Pin pin, string sql)
    {
        await using var conn = await PinnedAsync(pin);
        await using var command = new NpgsqlCommand(sql, conn);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> PinnedAsync(Pin pin)
    {
        var conn = new NpgsqlConnection(fixture.App);
        await conn.OpenAsync();
        await using var set = new NpgsqlCommand(
            $"SELECT set_config('app.current_subject_id', '{pin.SubjectId?.ToString() ?? string.Empty}', false)", conn);
        await set.ExecuteNonQueryAsync();
        return conn;
    }
}
